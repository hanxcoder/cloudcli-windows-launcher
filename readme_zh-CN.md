# CloudCLI Desktop Launcher for WSL 2.0.0

[English](README.md) · [简体中文](readme_zh-CN.md) · [繁體中文](readme_zh-TW.md)

这个启动器通过 WSL 启动已安装的 `cloudcli`，并在 WinForms 窗口中用 WebView2 打开原有的 Web 界面。安装包包含启动器和 WebView2 SDK DLL，CloudCLI 服务及其 Web 界面由上游提供。

[更新日志](CHANGELOG.md)

## 部署

1. 在 Windows PowerShell 中运行 `wsl -l -q`，查看启动器会使用哪个发行版。若尚未安装 WSL，运行 `wsl --install` 并完成发行版的首次设置。
2. 在列表中的第一个发行版内安装 Node.js 22 或更高版本以及 npm。如果想自行安装[上游 CloudCLI](https://github.com/siteboon/claudecodeui)，可以运行：

   ```bash
   npm install -g @cloudcli-ai/cloudcli
   command -v cloudcli
   cloudcli start --port 3001
   ```

   在 Windows 上用浏览器打开 `http://localhost:3001/`，确认页面能加载。然后在 WSL 终端按 `Ctrl+C` 停止测试服务。下次打开桌面窗口时，启动器会自动启动服务。
3. 在 Windows 安装 Microsoft Edge WebView2 Runtime，然后下载并运行 [CloudCLI-Setup-2.0.0.exe](https://github.com/hanxcoder/cloudcli-wsl-launcher/releases/download/v2.0.0/CloudCLI-Setup-2.0.0.exe)。如需立即启动，可在安装完成页勾选“运行 CloudCLI”，默认不勾选。
4. 打开 CloudCLI。选“帮我配置并启动”会优先复用已有的 CloudCLI，缺少时才安装到启动器自己的数据目录，默认端口为 3001。选“自定义或使用已有部署”可指定本机端口。如果服务由你自己管理，取消勾选服务未运行时自动启动的选项即可。连接成功后会记住设置。

窗口语言默认跟随 Windows 显示语言，也可以在启动前设置 `CLOUDCLI_LANG` 为 `zh-CN`、`zh-TW` 或 `en`。在窗口中可用 `F5` 刷新，或用 `Ctrl+I` 打开开发者工具。网页请求打开新窗口时，启动器会把链接交给 Windows。
静默更新已有安装时，用 `/UPDATE` 参数运行安装包。更新会使用已登记的安装目录。找不到已安装的启动器时，安装程序会以退出码 2 结束。


## 服务启停与数据

启动器先检查设置的端口。已有 CloudCLI 服务时直接连接；如果允许自动启动，端口空闲时会在 WSL 中启动服务。端口被其他程序占用时会显示错误。关闭窗口后，启动器会留在系统托盘里。在托盘菜单中选择“打开窗口”可重新打开 CloudCLI。选择“退出”会关闭启动器，并停止由它启动的服务。单独启动的服务会继续运行。如果当前连接的服务停止，启动器也会退出。

- 启动器日志在安装目录下的 `logs\launcher.log`。
- 服务启动日志在启动器安装目录下的 `logs\server.log`。
- WebView2 用户数据在安装目录下的 `data\webview2`。如果该目录尚未创建，启动器会尝试从 `%LOCALAPPDATA%\CloudCLI\webview2` 迁移旧数据。卸载时会删除安装目录内的 `logs` 和 `data`。

## 从源码构建

在 WSL 的 Bash 中使用 .NET SDK 和 NSIS 编译打包。依赖、完整命令和输出路径见[英文 README 的构建说明](README.md#build)。

## 许可证

本项目的启动器采用 [GPL-3.0-only](LICENSE)。[上游 CloudCLI](https://github.com/siteboon/claudecodeui/blob/main/LICENSE) 采用 AGPLv3。随包提供的 Microsoft WebView2 SDK DLL 适用微软的许可条款。
