using System.Diagnostics;
using System.Runtime.InteropServices;
using MaximizeToVirtualDesktop.Interop;
using Updatum;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// System tray application. Hosts the NotifyIcon, handles the global hotkey,
/// and owns the lifecycle of all components.
/// </summary>
internal sealed class TrayApplication : Form
{
    private const int HOTKEY_ID = 0x1;
    private const int HOTKEY_PIN_ID = 0x2;
    private uint _shellRestartMessage;
    private bool _comInitialized;

    private readonly AppSettings _settings;
    private readonly NotifyIcon _trayIcon;
    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _tracker;
    private readonly FullScreenManager _manager;
    private readonly WindowMonitor _monitor;
    private readonly MaximizeButtonHook _mouseHook;
    private readonly DesktopNavigationHotkeys _navigation;
    private readonly System.Windows.Forms.Timer _cleanupTimer;
    private System.Windows.Forms.Timer? _retryTimer;

    internal static readonly UpdatumManager Updater = new("xuejiaW", "MaximizeToVirtualDesktop")
    {
        FetchOnlyLatestRelease = true,
        InstallUpdateSingleFileExecutableName = "MaximizeToVirtualDesktop",
    };

    public TrayApplication()
    {
        // Make the form invisible — we're a tray-only app
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = new Size(0, 0);

        // Load persisted settings first so all components use them
        _settings = AppSettings.Load();

        // Initialize components
        _vds = new VirtualDesktopService();
        _tracker = new FullScreenTracker();
        _manager = new FullScreenManager(_vds, _tracker, _settings);
        _monitor = new WindowMonitor(_manager, _tracker, this, _settings);
        _mouseHook = new MaximizeButtonHook(_manager, this, _settings);
        _navigation = new DesktopNavigationHotkeys(right =>
        {
            if (_comInitialized) _vds.SwitchAdjacentDesktop(right);
        });

        // System tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = BuildTooltipText(),
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        // Periodic cleanup of stale entries (every 30 seconds)
        _cleanupTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _cleanupTimer.Tick += (_, _) => _manager.CleanupStaleEntries();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Check Windows version before attempting COM init
        var buildNumber = GetWindowsBuildNumber();
        Trace.WriteLine($"TrayApplication: Windows build {buildNumber}");

        if (buildNumber < 19045)
        {
            // Older Windows builds have different private desktop interfaces.
            MessageBox.Show(
                "MaximizeToVirtualDesktop requires Windows 10 22H2 or Windows 11.\n\n" +
                $"Your system is running Windows build {buildNumber}.\n" +
                "This Windows build is outside the supported desktop interface range.",
                "MaximizeToVirtualDesktop — Unsupported Windows Version",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
            return;
        }

        // Initialize COM (adapter auto-selects based on build number)
        _comInitialized = _vds.Initialize(buildNumber);
        if (!_comInitialized)
        {
            Trace.WriteLine("TrayApplication: COM initialization failed — entering degraded mode.");
            _trayIcon.Text = "Maximize to Virtual Desktop\n⚠️ COM failed — checking for updates...";
            _trayIcon.BalloonTipTitle = "Maximize to Virtual Desktop";
            _trayIcon.BalloonTipText =
                "Virtual Desktop COM interface failed to initialize.\n" +
                "This usually means Windows updated and broke the internal APIs.\n" +
                "Checking for an updated version now...";
            _trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
            _trayIcon.ShowBalloonTip(5000);

            // Immediately check for updates, then retry every 5 minutes
            _ = CheckForUpdatesAsync(userInitiated: false, comFailure: true);
            _retryTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
            _retryTimer.Tick += async (_, _) =>
            {
                // Try reinitializing COM in case an in-place Windows update fixed it
                if (_vds.Reinitialize())
                {
                    Trace.WriteLine("TrayApplication: COM reinitialized successfully!");
                    _comInitialized = true;
                    _retryTimer!.Stop();
                    _retryTimer.Dispose();
                    _retryTimer = null;
                    _trayIcon.Text = BuildTooltipText();
                    StartMonitoring();
                    return;
                }
                await CheckForUpdatesAsync(userInitiated: false, comFailure: true);
            };
            _retryTimer.Start();

            // Register for Explorer restart — COM might work after Explorer restarts
            _shellRestartMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            return;
        }

        // Register for Explorer restart notification
        _shellRestartMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        // Recover orphaned desktops from a previous crash
        RecoverOrphanedDesktops();

        // Start monitoring
        StartMonitoring();

        Trace.WriteLine("TrayApplication: Started.");

        // Show first-run balloon tip
        ShowFirstRunBalloon();

        // Check for updates asynchronously
        _ = CheckForUpdatesAsync();
    }

    private void StartMonitoring()
    {
        _monitor.Start();
        _mouseHook.Install();
        _cleanupTimer.Start();

        RegisterConfiguredHotkeys();
    }

    private List<string> RegisterConfiguredHotkeys()
    {
        foreach (int id in new[] { HOTKEY_ID, HOTKEY_PIN_ID })
            NativeMethods.UnregisterHotKey(Handle, id);
        var failures = new List<string>();
        void Register(int id, uint modifiers, uint key, string name)
        {
            if (!NativeMethods.RegisterHotKey(Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, key))
                failures.Add(name);
        }
        Register(HOTKEY_ID, _settings.HotkeyModifiers, _settings.HotkeyKey, "Maximize hotkey");
        Register(HOTKEY_PIN_ID, _settings.PinHotkeyModifiers, _settings.PinHotkeyKey, "Pin hotkey");
        failures.AddRange(_navigation.Configure(_settings.EnableDesktopNavigation));
        if (failures.Count != 0)
        {
            Trace.WriteLine($"TrayApplication: Cannot register {string.Join(", ", failures)}.");
            _trayIcon.BalloonTipTitle = "Shortcut already in use";
            _trayIcon.BalloonTipText = $"Could not register: {string.Join(", ", failures)}. Change the conflicting app's shortcut or disable desktop navigation in Settings.";
            _trayIcon.ShowBalloonTip(5000);
        }
        return failures;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)NativeMethods.WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_ID)
        {
            OnHotkeyPressed();
            return;
        }

        if (m.Msg == (int)NativeMethods.WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_PIN_ID)
        {
            OnPinHotkeyPressed();
            return;
        }

        // Explorer restart: COM objects are now invalid, reinitialize
        if (_shellRestartMessage != 0 && m.Msg == (int)_shellRestartMessage)
        {
            Trace.WriteLine("TrayApplication: Explorer restarted, reinitializing COM...");

            // Windows destroys all virtual desktops on Explorer restart —
            // our tracked COM refs are now stale and must be released.
            _tracker.ClearAll();

            if (_vds.Reinitialize() && !_comInitialized)
            {
                // Recovered from degraded mode!
                Trace.WriteLine("TrayApplication: COM recovered after Explorer restart!");
                _comInitialized = true;
                _retryTimer?.Stop();
                _retryTimer?.Dispose();
                _retryTimer = null;
                _trayIcon.Text = BuildTooltipText();
                StartMonitoring();
            }
            return;
        }

        base.WndProc(ref m);
    }

    private void OnHotkeyPressed()
    {
        if (!_comInitialized)
        {
            Trace.WriteLine("TrayApplication: Hotkey pressed but COM not initialized.");
            return;
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Handle)
        {
            Trace.WriteLine("TrayApplication: Hotkey pressed but no valid foreground window.");
            return;
        }

        Trace.WriteLine($"TrayApplication: Hotkey pressed, toggling window {hwnd}");
        _manager.Toggle(hwnd);
    }

    private void OnPinHotkeyPressed()
    {
        if (!_comInitialized)
        {
            Trace.WriteLine("TrayApplication: Pin hotkey pressed but COM not initialized.");
            return;
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Handle)
        {
            Trace.WriteLine("TrayApplication: Pin hotkey pressed but no valid foreground window.");
            return;
        }

        Trace.WriteLine($"TrayApplication: Pin hotkey pressed, toggling pin for window {hwnd}");
        _manager.PinToggle(hwnd);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem("No windows tracked") { Enabled = false };
        menu.Opening += (_, _) =>
        {
            var count = _tracker.Count;
            statusItem.Text = count == 0
                ? "No windows tracked"
                : $"{count} window(s) on virtual desktops";
        };
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var restoreAllItem = new ToolStripMenuItem("Restore All", null, (_, _) =>
        {
            _manager.RestoreAll();
        });
        menu.Items.Add(restoreAllItem);

        menu.Items.Add(new ToolStripSeparator());

        var howToUseItem = new ToolStripMenuItem("How to Use", null, (_, _) =>
        {
            ShowUsageInfo(_settings);
        });
        menu.Items.Add(howToUseItem);

        var settingsItem = new ToolStripMenuItem("Settings...", null, (_, _) =>
        {
            OpenSettings();
        });
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var updateItem = new ToolStripMenuItem("Check for Updates...", null, async (_, _) =>
        {
            await CheckForUpdatesAsync(userInitiated: true);
        });
        menu.Items.Add(updateItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) =>
        {
            Application.Exit();
        });
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        dlg.ApplyToSettings();
        if (!_settings.Save())
        {
            MessageBox.Show(
                "Settings could not be saved. Your changes will apply for this session but won't persist after restart.",
                "Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (_comInitialized) RegisterConfiguredHotkeys();
        if (_settings.AutoManageMaximizedWindows && _settings.ManageExistingWindowsOnStart)
            _monitor.ScanExistingWindows();
        _trayIcon.Text = BuildTooltipText();
        Trace.WriteLine("TrayApplication: Settings saved and hotkeys updated.");
    }

    private async Task CheckForUpdatesAsync(bool userInitiated = false, bool comFailure = false)
    {
        try
        {
            if (!userInitiated && !comFailure) await Task.Delay(5000);

            var updateFound = await Updater.CheckForUpdatesAsync();

            if (!updateFound)
            {
                if (userInitiated)
                    MessageBox.Show("You're running the latest version.", "No Updates",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (comFailure)
                    _trayIcon.Text = "Maximize to Virtual Desktop\n⚠️ COM failed — no update available yet";
                return;
            }

            var release = Updater.LatestRelease!;
            var changelog = Updater.GetChangelog(true) ?? "No release notes available.";

            var message = comFailure
                ? $"A fix may be available! Version {release.TagName} is ready.\n\n{changelog}\n\nDownload and install?"
                : $"Version {release.TagName} is available.\n\n{changelog}\n\nDownload and install?";

            var result = MessageBox.Show(message,
                comFailure ? "Update Available — May Fix COM Issue" : "Update Available",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (result == DialogResult.Yes)
            {
                var asset = await Updater.DownloadUpdateAsync();
                if (asset != null)
                {
                    await Updater.InstallUpdateAsync(asset);
                }
                else if (userInitiated)
                {
                    MessageBox.Show("Failed to download the update.", "Update Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TrayApplication: Update check failed: {ex.Message}");
            if (userInitiated)
                MessageBox.Show($"Update check failed: {ex.Message}", "Update Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static readonly string FirstRunMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MaximizeToVirtualDesktop", ".firstrun");

    private void ShowFirstRunBalloon()
    {
        try
        {
            if (File.Exists(FirstRunMarker)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(FirstRunMarker)!);
            File.WriteAllText(FirstRunMarker, "");

            var maximizeKey = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey);
            var pinKey = FormatHotkey(_settings.PinHotkeyModifiers, _settings.PinHotkeyKey);
            var clickDesc = _settings.AutoManageMaximizedWindows || _settings.InvertShiftClick ? "Click" : "Shift+Click";

            _trayIcon.BalloonTipTitle = "Maximize to Virtual Desktop";
            _trayIcon.BalloonTipText =
                $"Press {maximizeKey} or {clickDesc} the maximize button " +
                "to maximize a window to its own virtual desktop.\n" +
                $"Press {pinKey} to pin a window to all desktops.";
            _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TrayApplication: First-run balloon failed: {ex.Message}");
        }
    }

    private static void ShowUsageInfo(AppSettings settings)
    {
        using var form = new Form
        {
            Text = "How to Use — Maximize to Virtual Desktop",
            Size = new Size(620, 580),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
        };

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
        };

        // Add inner padding via a wrapper panel
        var contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 0),
        };
        contentPanel.Controls.Add(rtb);

        // Build RTF content
        rtb.SelectionFont = new Font("Segoe UI Variable Display", 14f, FontStyle.Bold);
        rtb.AppendText("Maximize to Virtual Desktop\n\n");

        var maximizeKey = FormatHotkey(settings.HotkeyModifiers, settings.HotkeyKey);
        var pinKey = FormatHotkey(settings.PinHotkeyModifiers, settings.PinHotkeyKey);
        var clickDesc = settings.AutoManageMaximizedWindows || settings.InvertShiftClick
            ? "Click maximize button"
            : "Shift + Click maximize button";
        var clickInstruction = settings.AutoManageMaximizedWindows || settings.InvertShiftClick
            ? "Click any window's maximize button. Hold Shift for a normal maximize."
            : "Hold Shift and click any window's maximize button.";

        AppendSection(rtb, "Maximize a Window to Its Own Desktop",
            (maximizeKey, "Toggles the focused window to/from its own virtual desktop."),
            (clickDesc, clickInstruction));

        if (settings.AutoManageMaximizedWindows)
            AppendSection(rtb, "Automatic Mode",
                ("Maximize", "Use the maximize button, title bar, or Win+Up. Hold Shift to bypass automatic placement."),
                ("Excluded apps", "List apps in Settings to leave their windows on their current desktop."));
        if (settings.EnableDesktopNavigation)
            AppendSection(rtb, "Switch Desktops",
                ("Ctrl+[", "Switch to the desktop on the left."),
                ("Ctrl+]", "Switch to the desktop on the right. Navigation stops at the edge."));

        AppendSection(rtb, "Pin a Window to All Desktops",
            (pinKey, "Toggles pin/unpin so the focused window appears on every desktop."));

        AppendSection(rtb, "How It Works",
            ("Maximize", "The window moves to a new virtual desktop and is maximized full-screen."),
            ("Restore", "Close or restore the window to automatically return to your original desktop."),
            ("Restore All", "Use the tray menu to bring all windows back at once."));

        var panel = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 32),
            FlatStyle = FlatStyle.System,
        };
        okButton.Location = new Point((panel.Width - okButton.Width) / 2, 9);
        panel.Resize += (_, _) => okButton.Location = new Point((panel.Width - okButton.Width) / 2, 9);
        panel.Controls.Add(okButton);

        form.AcceptButton = okButton;
        form.Controls.Add(contentPanel);
        form.Controls.Add(panel);
        form.ShowDialog();
    }

    private static void AppendSection(RichTextBox rtb, string heading, params (string key, string desc)[] items)
    {
        rtb.SelectionFont = new Font("Segoe UI Variable Display", 10.5f, FontStyle.Bold);
        rtb.AppendText(heading + "\n");

        foreach (var (key, desc) in items)
        {
            rtb.SelectionFont = new Font("Segoe UI Variable Display", 9.5f, FontStyle.Regular);
            rtb.AppendText("  ");
            rtb.SelectionFont = new Font("Consolas", 9f, FontStyle.Bold);
            rtb.AppendText(key);
            rtb.SelectionFont = new Font("Segoe UI Variable Display", 9.5f, FontStyle.Regular);
            rtb.AppendText("  —  " + desc + "\n");
        }

        rtb.AppendText("\n");
    }

    private static int GetWindowsBuildNumber()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuildNumber")?.ToString();
            return int.TryParse(build, out var num) ? num : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string BuildTooltipText()
    {
        var maximize = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey);
        var pin = FormatHotkey(_settings.PinHotkeyModifiers, _settings.PinHotkeyKey);
        var click = _settings.AutoManageMaximizedWindows || _settings.InvertShiftClick ? "Click maximize" : "Shift+Click maximize";
        return $"Maximize to Virtual Desktop\n{maximize} | {click} | {pin} to pin";
    }

    private static string FormatHotkey(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(GetKeyName(vk));
        return string.Join("+", parts);
    }

    private static string GetKeyName(uint vk)
    {
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return $"F{vk - 0x70 + 1}";
        return $"0x{vk:X2}";
    }

    private void RecoverOrphanedDesktops()
    {
        var persisted = TrackerPersistence.Load();
        if (persisted.Count == 0) return;

        Trace.WriteLine($"TrayApplication: Found {persisted.Count} orphaned desktop(s) from previous session.");

        foreach (var entry in persisted)
        {
            var desktop = _vds.FindDesktop(entry.TempDesktopId);
            if (desktop != null)
            {
                // A crash may leave real application windows on this desktop.
                // Preserve occupied desktops instead of relocating those windows.
                if (!_vds.HasApplicationWindows(entry.TempDesktopId)) _vds.RemoveDesktop(desktop);
                ComRelease.Release(desktop);
            }
        }

        TrackerPersistence.Delete();
        Trace.WriteLine("TrayApplication: Orphaned desktop recovery complete.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Trace.WriteLine("TrayApplication: Shutting down...");

        _retryTimer?.Stop();
        _retryTimer?.Dispose();

        _cleanupTimer.Stop();
        _cleanupTimer.Dispose();

        // Stop event producers before restoring windows, so shutdown cannot
        // automatically adopt a window again while desktop cleanup runs.
        _monitor.Dispose();
        _mouseHook.Dispose();
        _manager.RestoreAll();

        // Clean up native resources
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_PIN_ID);
        _navigation.Dispose();
        _mouseHook.Dispose();
        _monitor.Dispose();
        _vds.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        Trace.WriteLine("TrayApplication: Shutdown complete.");
        base.OnFormClosing(e);
    }
}
