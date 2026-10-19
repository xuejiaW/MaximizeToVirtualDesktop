# MaximizeToVirtualDesktop

Automatically give maximized application windows their own Windows virtual desktop, with `Ctrl+[` / `Ctrl+]` navigation.

This fork adds Windows 10 22H2 support and automatic window management to [shanselman/MaximizeToVirtualDesktop](https://github.com/shanselman/MaximizeToVirtualDesktop). See [中文开发与使用说明](docs/DEVELOPMENT.zh-CN.md) for build commands, test evidence, and limitations.

Newly maximized windows move to a new desktop automatically. Restoring or minimizing returns the window to its original desktop, preserving minimization. Closing a background window does not change the current desktop. Temporary desktops containing other application windows are retained.

![MaximizeToVirtualDesktop demo](img/maximizetovd_compressed.gif)

## Usage

| Trigger | How |
|---------|-----|
| **Maximize** | Automatically enter a dedicated desktop; hold `Shift` to bypass |
| **Ctrl + [ / Ctrl + ]** | Previous / next desktop; stops at the edge |
| **Hotkey** | `Ctrl + Alt + Shift + X` — toggles the foreground window |
| **Manual mode** | Turn automatic mode off in Settings to use the original Shift+Click behavior |

The app runs in the system tray. Right-click the tray icon for options:
- **Restore All** — brings all windows back and removes temp desktops
- **How to Use** — shows usage instructions
- **Settings** — automatic mode, navigation, excluded apps, existing windows, and popups
- **Check for Updates** — checks for new releases on GitHub
- **Exit** — restores all windows, then exits

## How It Works

1. **Maximize** — creates a new virtual desktop, moves the window there, and switches to it. Windows 11 desktops are named `[MVD] ProcessName`; Windows 10 uses system default names.
2. **Auto-restore on un-maximize** — if you restore/un-maximize a tracked window, it's automatically sent back to its original desktop and the temp desktop is cleaned up.
3. **Auto-restore on close** — closing a tracked window triggers the same cleanup.
4. **Toggle** — pressing the hotkey on an already-tracked window restores it.
5. **Crash recovery** — empty recorded desktops are cleaned up on next launch; occupied desktops are preserved.

## Requirements

- **Windows 10 22H2** (tested on x64 build 19045.6466)
- **Windows 11** adapters are inherited from upstream and need validation on each target build
- Self-contained — no .NET installation required

The app auto-detects your Windows build and selects the correct COM vtable layout. If a future Windows update breaks things, the app enters degraded mode and checks for updates automatically.

### Shift+Click Compatibility

Shift+Click works wherever Windows 11 Snap Layouts works — apps that correctly handle `WM_NCHITTEST` and return `HTMAXBUTTON`. This includes:

| App | Works? |
|-----|--------|
| Notepad, Explorer, traditional Win32 | ✅ |
| Windows Terminal (WinUI) | ✅ |
| VS Code (Electron 13+) | ✅ |
| Visual Studio | ✅ |
| Apps with fully custom title bars | ❌ (use hotkey) |

Automatic mode listens to window state changes rather than depending on the title-bar button. Administrator windows and custom applications still require compatibility testing.

## Building

```bash
./scripts/validate.ps1
./scripts/validate.ps1 -Integration # interactive Windows session; test windows only
```

### Single-file publish

```bash
./scripts/build.ps1
```

## Architecture

```
src/MaximizeToVirtualDesktop/
├── Program.cs                  Entry point, single-instance Mutex
├── TrayApplication.cs          System tray, hotkey, lifecycle owner
├── FullScreenManager.cs        Orchestrator with rollback on every step
├── FullScreenTracker.cs        Thread-safe hwnd → tracking state map
├── VirtualDesktopService.cs    COM wrapper, 4 operations, Explorer restart recovery
├── WindowMonitor.cs            SetWinEventHook for close/un-maximize detection
├── MaximizeButtonHook.cs       WH_MOUSE_LL for Shift+Click on maximize button
├── TrackerPersistence.cs       JSON crash recovery in %LOCALAPPDATA%
└── Interop/
    ├── NativeMethods.cs        P/Invoke declarations
    ├── VirtualDesktopCom.cs    Vendored COM interfaces (from MScholtes/VirtualDesktop)
    └── DesktopManagerAdapter.cs Multi-version COM vtable adapter
```

COM interop declarations are adapted from [MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) (MIT; see `THIRD-PARTY-NOTICES`). The app uses Updatum for updates from this fork. Version-specific adapters cover Windows 10 and the inherited Windows 11 layouts. Tests are under `tests/`.

## Design Principles

1. Keep desktop operations and automatic window policy separate.
2. Make entering a desktop idempotent and preserve desktops containing other applications.
3. Coalesce WinEvent callbacks and perform window mutations on the UI thread.

## Known Limitations

- **Elevated windows** — cannot move windows running as Administrator from a non-elevated instance.
- **App crash** — empty recorded temporary desktops are cleaned up on next launch. Occupied desktops remain available for manual recovery.
- **Validation** — automated interactive tests pass on Windows 10 22H2. Windows 11, multiple monitors, elevated windows, Explorer restarts, and application-specific behavior require further testing.
- **Shortcuts** — enabled global navigation takes over `Ctrl+[` and `Ctrl+]` from focused apps. Disable navigation in Settings to release them.

## The Virtual Desktop GUID Problem

Microsoft's Virtual Desktop feature has a proper, documented COM interface — `IVirtualDesktopManager` — but it can only tell you *which* desktop a window is on and move a window *you own* between desktops. The actually useful operations — creating desktops, switching desktops, moving *any* window, naming desktops — all live behind **undocumented COM interfaces** like `IVirtualDesktopManagerInternal` and `IVirtualDesktop`.

The problem? **Microsoft changes the interface GUIDs with nearly every major Windows update.** Not the methods. Not the signatures. Just the GUIDs. This means every app that uses virtual desktop automation — this one, [Peach](https://peachapp.net), [FancyWM](https://github.com/FancyWM/fancywm), and dozens of others — breaks silently 2-3 times a year and has to scramble to update hardcoded GUIDs.

This is the single biggest fragility in this app. When it breaks, the app shows an error dialog on startup saying "Failed to initialize Virtual Desktop COM interface." The fix is straightforward but shouldn't be necessary:

### How to update the GUIDs

1. Check [MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) — Markus Scholtes maintains per-build interface files (e.g., `VirtualDesktop11-24H2.cs`) and typically updates within days of a new Windows build. Huge thanks to him for doing this thankless work for the entire community.
2. If it's a **vtable change** (new/removed methods, same GUIDs), add a new adapter class in `DesktopManagerAdapter.cs` and a new COM interface in `VirtualDesktopCom.cs`.
3. If it's a **GUID change**, update the GUIDs on the affected interfaces.
4. The fragile GUIDs are on these interfaces:

| Interface | What it does | Stable? |
|-----------|-------------|---------|
| `IVirtualDesktopManager` | Check/move owned windows | ✅ Documented, stable since Win10 |
| `IServiceProvider10` | Standard COM service lookup | ✅ Stable |
| `IObjectArray` | Standard COM collection | ✅ Stable |
| `IVirtualDesktop` | Desktop identity, name, wallpaper | ⚠️ **Breaks with Windows updates** |
| `IVirtualDesktopManagerInternal` | Create, switch, move, remove desktops | ⚠️ **Breaks with Windows updates** |
| `IApplicationView` | Window view for cross-process moves | ⚠️ **Breaks with Windows updates** |
| `IApplicationViewCollection` | Get views by window handle | ⚠️ **Breaks with Windows updates** |

### Dear Microsoft

Please stabilize the Virtual Desktop COM interfaces or provide a proper public API. Every third-party virtual desktop tool in the ecosystem depends on reverse-engineered GUIDs that break with every update. A stable API for creating, switching, naming, and moving windows between virtual desktops would eliminate an entire class of fragility. [PowerToys has asked for this too](https://github.com/microsoft/PowerToys/issues/13993).

## Prior Art & Credits

- **[Markus Scholtes (MScholtes/VirtualDesktop)](https://github.com/MScholtes/VirtualDesktop)** — the COM interface definitions we vendor are from his project (MIT license). He does the hard work of reverse-engineering and publishing updated GUIDs for every Windows build. This app and many others wouldn't be possible without his work.
- [Peach](https://peachapp.net) — MS Store app with similar hotkey UX
- PowerToys feature requests [#13993](https://github.com/microsoft/PowerToys/issues/13993), [#21597](https://github.com/microsoft/PowerToys/issues/21597)

## License

MIT

---

*Credit to Kieran Mockford for the original idea. May he rest in peace.*
