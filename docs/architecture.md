# 架构与依赖

项目使用 C# 和 .NET 10。三个进程各自负责一件事：

- `src/SmsCodeClipper/`：后台与命令行，负责设置、通知缓存、验证码提取、剪贴板、提示音、托盘、热键、本地 API 和接收器监督。
- `src/AncsReader/`：独立蓝牙接收器，使用 Windows BLE/GATT 和 Apple ANCS，通过标准输出 JSONL 交给后台。
- `src/ClipperWindow/`：WPF 小票窗口，通过本机 API 获取状态；关闭窗口不停止后台。图标统一放在 `src/Assets/`。

后台的 HTTP 服务使用 .NET 自带的 ASP.NET Core，托盘和剪贴板使用 Windows Forms，热键和键入使用 Win32 API。界面和接收器不依赖后台程序集。

状态查询直接使用内存中的最近验证码；历史与通知文件按修改时间缓存，并在写入后失效。托盘按状态变化刷新，使用图标内置的 64 像素图像；窗口跳过未变化的内容刷新，最小化时把状态查询间隔从 2 秒延长到 10 秒，恢复后立即查询。

## 数据目录与保留期限

在源码目录构建运行时，数据保存在项目根目录的 `settings.json`、`history.json`、`notifications.json`、`runtime.json` 和 `log.txt`。将完整 `dist` 目录放到其他位置后，默认数据目录为 `%LOCALAPPDATA%/SMSCodeClipper`。

`--data-dir` 或 `SMS_CODE_CLIPPER_DATA_DIR` 可指定数据目录，子进程会继承。`SMS_CODE_CLIPPER_READER`、`SMS_CODE_CLIPPER_WINDOW` 可分别指定两个子程序的绝对路径。

通知缓存最多 200 条，验证码历史最多 50 条。启动时及每小时自动清理三天前的日志与记录，日志超过 1 MiB 时压缩为最新的约 512 KiB；设置不受影响。它们可能含手机号和短信正文，分享前需脱敏。数据文件及临时副本已被 `.gitignore` 排除。

持久设置写盘成功后才会应用；写入失败时，窗口或托盘会提示保存失败，接收器继续使用当前设置。单独暂停自动复制只影响本次运行。历史新增或清理后，后台更新历史版本号，小票在下次轮询时刷新列表。

## 生命周期

Windows 命名互斥量保证单实例；启动协调锁串行化后台启动。`--ensure` 复用现有实例，`--background` 请求旧实例退出后重启，绝不按进程名强杀其他程序。`--ensure --owner-pid PID` 仅在新建后台时绑定所属进程。

后台通过实例标识确认启动，未获客户端确认的子进程 20 秒后退出。API 绑定或运行信息写入失败时不启动蓝牙接收。退出后将自己的 `runtime.json` 端口置零，客户端据此判定未运行。

接收器在独立进程运行，断开、服务变化、输出溢出或心跳超时会触发退避重连。重连后优先处理实时通知，同时补收近期通知，按内容哈希去重。较早的验证码只进历史，同一秒内实时通知优先于补收通知。主动断开会停止接收器。设置变更可立即唤醒重连等待。

本地 API 仅绑定 `127.0.0.1`，检查 Host、Origin 和所有请求的 `X-Requested-With: clipper`，请求体上限 64 KiB。这些检查限制网页跨站请求，不是对本机程序的身份认证。

## 构建与测试

`./scripts/build.ps1 build` 将后台发布到 `dist`，接收器和窗口分别放在 `dist/ancs-reader`、`dist/clipper-window`。三个程序均自带运行时，请保留完整目录。`SMS_CODE_CLIPPER_DOTNET` 可指定 SDK 的 `dotnet.exe`；否则优先使用本地 `build/dotnet`，再使用 PATH。

`./scripts/build.ps1 test` 执行 C# 回归测试、接收器解析自测和 WPF 布局自测，GitHub Actions 使用相同入口。回归测试使用临时目录和模拟接收器，不连接真实手机、不写入系统剪贴板、不模拟真实键入。
