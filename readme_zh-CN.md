# CloudCLI Desktop Launcher for WSL 1.3.0

[English](README.md) · [简体中文](readme_zh-CN.md) · [繁體中文](readme_zh-TW.md)

这个 Windows 启动器通过 WSL 启动已安装的 `cloudcli`，并在 WinForms 窗口中用 WebView2 打开原有的 Web 界面。安装包包含启动器和 WebView2 SDK DLL，CloudCLI 服务及其 Web 界面由上游提供。

[更新日志](CHANGELOG.md)

## 部署

1. 在 Windows PowerShell 中运行 `wsl -l -q`，查看启动器会使用哪个发行版。若尚未安装 WSL，运行 `wsl --install` 并完成发行版的首次设置。
2. 在列表中的第一个 WSL 发行版安装 Node.js 22 或更高版本及 npm，再安装 [Siteboon 的上游 CloudCLI](https://github.com/siteboon/claudecodeui)：

   ```bash
   npm install -g @cloudcli-ai/cloudcli
   command -v cloudcli
   cloudcli start --port 3001
   ```

   在 Windows 浏览器打开 `http://localhost:3001/`，确认页面能加载。然后在 WSL 终端按 `Ctrl+C` 停掉测试实例，以便验证桌面壳能自动启动服务。
3. 在 Windows 安装 Microsoft Edge WebView2 Runtime，然后下载并运行 [CloudCLI-Setup-1.3.0.exe](https://github.com/hanxcoder/cloudcli-wsl-launcher/releases/download/v1.3.0/CloudCLI-Setup-1.3.0.exe)。如需立即启动，可在安装完成页勾选「运行 CloudCLI」，默认不勾选。
4. 使用安装包时，从开始菜单或桌面快捷方式打开 CloudCLI。确认桌面窗口能加载 CloudCLI 网页。

窗口语言默认跟随 Windows 显示语言，也可以在启动前设置 `CLOUDCLI_LANG` 为 `zh-CN`、`zh-TW` 或 `en`。在窗口中可用 `F5` 刷新，或用 `Ctrl+I` 打开开发者工具。网页请求打开新窗口时，启动器会把链接交给 Windows。
静默更新已有安装时，用 `/UPDATE` 参数运行安装包。更新会使用已登记的安装目录。找不到已安装的启动器时，安装程序会以退出码 2 结束。


## 服务启停与数据

启动器先检查端口 3001。已有 CloudCLI 服务时直接打开，端口空闲时在 WSL 启动服务，端口被其他程序占用时显示错误。关闭窗口会隐藏到 Windows 托盘。选择「打开窗口」可恢复窗口，选择「退出」会关闭启动器并停止由它启动的服务。单独启动的服务会继续运行。如果当前连接的服务停止，启动器也会退出。

- Windows 启动器日志在安装目录下的 `logs\launcher.log`。
- WSL 服务启动日志在发行版用户目录下的 `~/.cloudcli.log`。
- WebView2 用户数据在安装目录下的 `data\webview2`。如果该目录尚未创建，启动器会尝试从 `%LOCALAPPDATA%\CloudCLI\webview2` 迁移旧数据。卸载时会删除安装目录内的 `logs` 和 `data`。

## 从源码构建

在 Windows PowerShell 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

脚本会重建 `dist\`，用 .NET Framework 4.x 的 64 位 `csc.exe` 编译 `src\Program.cs`，并复制 `lib\` 中的 WebView2 SDK DLL。如果在 `%LOCALAPPDATA%\electron-builder\Cache` 中找到 NSIS 的 `makensis.exe`，脚本还会使用 `src\installer.nsi` 生成 `dist\CloudCLI-Setup-1.3.0.exe`。

## 许可证

本项目的 Windows 启动器采用 [GPL-3.0-only](LICENSE)。[上游 CloudCLI](https://github.com/siteboon/claudecodeui/blob/main/LICENSE) 采用 AGPLv3。随包提供的 Microsoft WebView2 SDK DLL 适用微软的许可条款。
