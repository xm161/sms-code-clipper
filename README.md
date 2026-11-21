# SMS Code Clipper

SMS Code Clipper 是用 C# 编写的 Windows x64 托盘工具。它通过低功耗蓝牙（BLE）连接 iPhone，使用 Apple Notification Center Service（ANCS）接收短信通知，将识别出的验证码复制到 Windows 剪贴板。也可以开启自动填入。

## 日常启动与连接

**Windows x64 用户请在 [Releases](https://github.com/xm161/sms-code-clipper/releases/latest) 下载 `SMS-Code-Clipper-win-x64.zip`，完整解压后双击 `launch.vbs` 打开小票面板。** 启动时不会弹出控制台窗口。解压目录中包含 .NET 运行时、蓝牙接收器和面板程序，移动时请保留整个目录。

首次连接时，先将电脑与 iPhone 通过蓝牙配对，在 iPhone 的蓝牙设置中为这台电脑开启“共享系统通知”，再回到小票面板选择手机并连接。关闭面板后，后台会继续接收通知。通过系统托盘可以重新打开面板或退出程序。

## 从源码构建与测试

首次从源码运行，或修改代码后需要更新本地程序时，请安装 .NET 10 SDK，在仓库根目录执行构建命令。构建完成后即可按上面的方式双击启动。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 build
```

构建后，可用同一脚本运行后台回归测试、接收器自测和 WPF 面板自测。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 test
```

## 验证码提取与本地数据

后台根据短信中的验证码关键词提取连续的 4 至 8 位数字，过滤订单号和金额。遇到用空格或连字符分组的号码时，会跳过整个号码，以免只复制其中一段。断线重连后，补收到的较旧验证码只进入历史记录，不会覆盖剪贴板中的新码。

普通短信也会进入本机通知缓存，其中可能包含短信正文和手机号。复制日志只记录操作结果。后台在启动时及运行期间每小时清理超过 3 天的日志和短信记录，关闭期间到期的记录会保留至下次启动。日志写入后若超过 1 MiB，则保留最近约 512 KiB 的内容。

## 相关文档

- [架构与依赖](docs/architecture.md)
- [故障排查](docs/troubleshooting.md)

发布目录中的 `licenses/` 按组件和版本保存随包运行时的许可证、第三方声明及 Windows SDK 许可链接。应用图标由 AI 绘制。
