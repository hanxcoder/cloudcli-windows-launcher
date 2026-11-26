# 在 WSL 中部署 AI 编程 CLI（Claude Code、Codex、Gemini CLI 等）

[English](agent-clis.md) · [简体中文](agent-clis.zh-CN.md) · [繁體中文](agent-clis.zh-TW.md)

[CloudCLI](https://github.com/siteboon/claudecodeui) 本身只是服务端与 Web UI 框架（负责桌面/网页工作台、文件浏览、Git 与会话管理），真正执行 AI 编程能力的后端来自你在 WSL 中安装并登录的各个 CLI 工具。

请将需要使用的 CLI 安装在 **CloudCLI 所使用的同一个 WSL 发行版** 中（即 Windows PowerShell 运行 `wsl -l -q` 输出的第一个发行版）。

---

## 前置准备

在 WSL 终端中确认已准备好 Node.js 22+ 与 `npm`（推荐使用 [nvm](https://github.com/nvm-sh/nvm) 管理）：

```bash
node --version
npm --version
```

> **提示**：如果你在全新的 WSL 上通过启动器的「帮我配置并启动」完成了初始化，启动器会将独立版 Node.js 安装在 WSL 内的 `~/.local/share/cloudcli-launcher/` 目录下，不会修改你的系统环境变量。若需要在 WSL 中自行全局安装 Claude Code、Codex 等 CLI，请先在 WSL 用户环境中安装 Node.js 22+（如通过 `nvm`）。

---

## 1. Claude Code（Anthropic）

在 WSL 中安装：

```bash
npm install -g @anthropic-ai/claude-code
```

验证并完成首次登录：

```bash
claude --version
claude
```

按终端提示完成 Anthropic 账号登录（或配置 `ANTHROPIC_API_KEY`）。登录成功后即可退出终端，直接在 CloudCLI 桌面界面中使用。

---

## 2. OpenAI Codex CLI

在 WSL 中安装：

```bash
npm install -g @openai/codex
```

验证并登录：

```bash
codex --version
codex
```

按提示使用 ChatGPT 账号登录，或在 WSL 环境中配置 `OPENAI_API_KEY`。

---

## 3. Gemini CLI（Google）

在 WSL 中安装：

```bash
npm install -g @google/gemini-cli
```

验证并登录：

```bash
gemini --version
gemini
```

按提示完成 Google 账号授权或配置 `GEMINI_API_KEY`。

---

## 4. 其他受支持的 CLI（Cursor CLI、OpenCode 等）

上游 CloudCLI 同时支持 **Cursor CLI**、**OpenCode** 等更多终端 Agent。具体支持列表与官方安装方式请参阅上游 [CloudCLI 项目文档](https://github.com/siteboon/claudecodeui)。

---

## 常见问题排查

1. **确认装对了 WSL 发行版**：
   在 Windows PowerShell 运行 `wsl -l -q`，确保 CLI 安装并登录在排在第一位的发行版中。
2. **确认命令在 `PATH` 中可见**：
   ```bash
   command -v claude codex gemini
   ```
   如果使用 `nvm`、`fnm`、`volta`、`mise` 或 `asdf`，请确保安装了这些 CLI 的 Node 版本已设为默认版本。
3. **安装或登录后界面未立即识别**：
   在 CloudCLI 窗口中按 `F5` 刷新，或在右下角系统托盘右键点击 **退出** 后重新打开，让服务端重新扫描环境与登录凭据。
