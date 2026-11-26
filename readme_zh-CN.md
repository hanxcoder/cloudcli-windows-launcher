# CloudCLI Desktop Launcher for WSL 2.1.0

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
3. 在 Windows 安装 Microsoft Edge WebView2 Runtime，然后下载并运行 [CloudCLI-Setup-2.1.0.exe](https://github.com/hanxcoder/cloudcli-wsl-launcher/releases/download/v2.1.0/CloudCLI-Setup-2.1.0.exe)。如需立即启动，可在安装完成页勾选“运行 CloudCLI”，默认不勾选。
4. 打开 CloudCLI。选“帮我配置并启动”会在界面通过实时进度条完成后台静默安装 WSL、应用内创建 Linux 用户，并将独立版 Node.js 22 LTS 与 CloudCLI 安装至 WSL 原生 ext4 目录（默认端口 3001）。选“自定义或使用已有部署”可指定本机端口，存在多个开发用发行版时支持选择具体 WSL 发行版（自动过滤 Docker/Rancher/Podman 等容器发行版），并支持从 `PATH` 及主流版本管理器（`nvm`、`fnm`、`volta`、`asdf`、`mise`、`nodenv`、`pnpm`、`proto`、`n`、Linuxbrew、Nix 与登录 shell 兜底）检测 Node.js 22+。如果服务由你自己管理，取消勾选服务未运行时自动启动的选项即可。连接成功后会记住设置。

窗口语言默认跟随 Windows 显示语言，也可以在启动前设置 `CLOUDCLI_LANG` 为 `zh-CN`、`zh-TW` 或 `en`。在窗口中可用 `F5` 刷新，或用 `Ctrl+I` 打开开发者工具。网页请求打开新窗口时，启动器会把链接交给 Windows。
静默更新已有安装时，用 `/UPDATE` 参数运行安装包。更新会使用已登记的安装目录。找不到已安装的启动器时，安装程序会以退出码 2 结束。

## 安装 AI 编程 CLI

CloudCLI 只是工作台，真正干活的智能体（**Claude Code**、**Codex CLI**、**Gemini CLI**）需要另外装在 WSL 里。如果你用的是“帮我配置并启动”，不需要装 PowerShell 7、nvm，也不用打开 Linux 终端：

1. 打开**命令提示符**（按 `Win+R`，输入 `cmd`，回车），或在开始菜单打开 **Windows PowerShell**。
2. 复制想用的工具对应的两行命令，一行一行粘贴运行。第一行负责安装，第二行启动一次用来登录。登录完成后按 `Ctrl+C` 退出（没退出就再按一次）。
3. 右键 CloudCLI 托盘图标选择“退出”，再重新打开 CloudCLI，让它识别新装的工具。

**Claude Code**

```
wsl PATH=~/.local/share/cloudcli-launcher/node/bin:/usr/local/bin:/usr/bin:/bin npm install -g @anthropic-ai/claude-code
wsl PATH=~/.local/share/cloudcli-launcher/node/bin:/usr/local/bin:/usr/bin:/bin claude
```

**Codex CLI**

```
wsl PATH=~/.local/share/cloudcli-launcher/node/bin:/usr/local/bin:/usr/bin:/bin npm install -g @openai/codex
wsl PATH=~/.local/share/cloudcli-launcher/node/bin:/usr/local/bin:/usr/bin:/bin codex
```

**Gemini CLI**

```
wsl PATH=~/.local/share/cloudcli-launcher/node/bin:/usr/local/bin:/usr/bin:/bin npm install -g @google/gemini-cli
wsl PATH=~/.local/share/cloudcli-launcher/node/bin:/usr/local/bin:/usr/bin:/bin gemini
```

- 这些命令会把工具装进启动器准备好的 Node.js 里，CloudCLI 能自动找到。以后想更新，重新运行安装那一行即可。
- 如果你在启动器里指定了其他 WSL 发行版，在 `wsl` 后面加上 `-d 发行版名`，例如 `wsl -d Debian PATH=...`。
- 如果你自己管理 Node.js（nvm、fnm、Volta 等），请在 WSL 终端里安装，见 [WSL 中部署 AI 编程 CLI 指南](docs/agent-clis.zh-CN.md)。

## 服务启停与数据

启动器先检查设置的端口。已有 CloudCLI 服务时直接连接；如果允许自动启动，端口空闲时会在 WSL 中启动服务。端口被其他程序占用时会显示错误。关闭窗口后，启动器会留在系统托盘里。在托盘菜单中选择“打开窗口”可重新打开 CloudCLI。选择“退出”会关闭启动器，并停止由它启动的服务。单独启动的服务会继续运行。如果当前连接的服务停止，启动器也会退出。

- 启动器日志在安装目录下的 `logs\launcher.log`。
- 服务启动日志在启动器安装目录下的 `logs\server.log`。
- WebView2 用户数据在安装目录下的 `data\webview2`。如果该目录尚未创建，启动器会尝试从 `%LOCALAPPDATA%\CloudCLI\webview2` 迁移旧数据。卸载时会删除安装目录内的 `logs` 和 `data`。

## 从源码构建

在 WSL 的 Bash 中使用 .NET SDK 和 NSIS 编译打包。依赖、完整命令和输出路径见[英文 README 的构建说明](README.md#build)。

## 许可证

本项目的启动器采用 [GPL-3.0-only](LICENSE)。[上游 CloudCLI](https://github.com/siteboon/claudecodeui/blob/main/LICENSE) 采用 AGPLv3。随包提供的 Microsoft WebView2 SDK DLL 适用微软的许可条款。
