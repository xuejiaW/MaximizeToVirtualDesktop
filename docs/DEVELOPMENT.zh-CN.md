# 自动桌面开发版

上游：https://github.com/shanselman/MaximizeToVirtualDesktop

Fork：https://github.com/xuejiaW/MaximizeToVirtualDesktop

本地目录：`D:\MaximizeToVirtualDesktop`，开发分支：`feat/automatic-spaces`。

## 使用

运行 `artifacts\win-x64\MaximizeToVirtualDesktop.exe`，程序常驻系统托盘。

- 新窗口最大化后，自动移到独立的虚拟桌面。按钮、标题栏双击和系统最大化快捷键走同一个窗口状态监听流程。
- 新应用以最大化状态显示时，也会进入独立桌面。
- `Ctrl + [` 切换到左侧桌面，`Ctrl + ]` 切换到右侧桌面，到边界停止。
- 按住 Shift 最大化，可暂时绕过自动分配；还原后下次最大化重新按自动规则处理。
- 还原窗口会移回原桌面；最小化会移回并保持最小化。
- 关闭窗口会清理空的临时桌面。关闭后台窗口不会把当前桌面切走。
- 临时桌面如果还有其他应用窗口，会保留这个桌面，不移动这些窗口。
- 托盘 Settings 可以关闭自动模式或导航快捷键、排除指定应用，并选择是否接管启动前已最大化的窗口。
- 启动前已最大化窗口默认不接管；以后的新最大化事件照常处理。
- 本轮测试没有启用开机启动，也没有让程序常驻接管用户的应用。

这些快捷键启用后会覆盖应用内的 `Ctrl + [ / ]`；可以在 Settings 关闭桌面导航。

## 构建和验证

需要 .NET 10 SDK。运行包自带运行时，不需要另装 .NET。

```powershell
./scripts/validate.ps1
./scripts/validate.ps1 -Integration
./scripts/build.ps1
```

集成测试会短暂创建测试窗口并切换测试桌面，只管理自身或显式启动的子进程窗口。结束时校验桌面数量和当前桌面恢复原状。不要在无人桌面、服务会话或无交互的 CI 中运行集成测试。

验证环境：Windows 10 22H2，build 19045.6466，x64；.NET SDK 10.0.401。

当前验证：9 个单元测试通过，45 项集成检查通过，Release 构建无警告、无错误。集成验证包括同进程和跨进程窗口、初始最大化、重复事件、还原、最小化、后台关闭、占用桌面保护、全局快捷键注册及导航消息处理。

快捷键集成测试验证系统注册及 `WM_HOTKEY` 分发；没有模拟真实键盘按键。Shift 绕过有策略单元测试，仍需用常用应用进行手动体验验证。

生成的证据位于 `artifacts\integration.log`、`artifacts\test-results\unit-tests.trx` 和 `artifacts\settings.png`。

## 边界和后续验证

- Windows 11 保留上游接口适配，但这台 Windows 10 主机上无法实测 Windows 11。
- Windows 10 适配不修改桌面名称，使用系统默认名称。
- 管理员窗口、Electron/Unity 等自定义窗口、多屏、Explorer 重启和系统更新后的兼容性还需要专项实测。
- 自动模式忽略自身窗口、带所有者的对话框、工具窗口、不激活的窗口、被系统隐藏的窗口及排除列表中的应用。
- 私有 COM 接口可能随 Windows 更新变化，接口声明集中在 `Interop` 目录。
- 崩溃恢复只自动删除空的残留桌面；有窗口的桌面保留。
- 更新来源已改为这个 fork，避免安装上游版本而覆盖本地功能。

## 提交约定

所有新增提交使用 Angular 风格：`type(scope): imperative description`。例如 `feat(navigation): add bracket shortcuts`。不改写上游历史。
