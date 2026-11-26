# Deploying AI Coding CLIs in WSL (Claude Code, Codex, Gemini CLI, etc.)

[English](agent-clis.md) · [简体中文](agent-clis.zh-CN.md) · [繁體中文](agent-clis.zh-TW.md)

[CloudCLI](https://github.com/siteboon/claudecodeui) is the server and web UI framework—it provides the desktop/web workspace, file browser, Git view, and session manager, while the actual AI coding agents are provided by the CLI tools installed inside your WSL distribution.

Install the CLI(s) you want to use inside **the same WSL distribution** that CloudCLI runs in (the first distribution returned by `wsl -l -q` in Windows PowerShell).

---

## Prerequisites

Make sure Node.js 22+ and `npm` are available on your WSL shell `PATH` (for example via [nvm](https://github.com/nvm-sh/nvm)):

```bash
node --version
npm --version
```

> **Note**: If you used the launcher's **Set it up for me** option on a fresh WSL distribution, the launcher installs an isolated Node.js runtime under its own `data/runtime/` folder so it does not touch your shell configuration. To install global CLI tools like Claude Code or Codex in WSL, install Node.js 22+ in your WSL user environment (for example with `nvm`) first.

---

## 1. Claude Code (Anthropic)

Install in WSL:

```bash
npm install -g @anthropic-ai/claude-code
```

Verify and sign in:

```bash
claude --version
claude
```

Follow the interactive prompt to sign in with your Anthropic account (or configure your `ANTHROPIC_API_KEY`). Once signed in, you can exit the terminal session and use Claude Code directly inside CloudCLI.

---

## 2. OpenAI Codex CLI

Install in WSL:

```bash
npm install -g @openai/codex
```

Verify and sign in:

```bash
codex --version
codex
```

Sign in with your ChatGPT account or set `OPENAI_API_KEY` in your WSL environment.

---

## 3. Gemini CLI (Google)

Install in WSL:

```bash
npm install -g @google/gemini-cli
```

Verify and sign in:

```bash
gemini --version
gemini
```

Complete Google account login or configure `GEMINI_API_KEY` according to the prompt.

---

## 4. Other Supported CLIs (Cursor CLI, OpenCode, etc.)

Upstream CloudCLI also supports additional terminal agents such as **Cursor CLI** and **OpenCode**. Refer to the upstream [CloudCLI documentation](https://github.com/siteboon/claudecodeui) for the latest supported agents and their official installation commands.

---

## Verification & Troubleshooting

1. **Check the target WSL distribution**:
   Run `wsl -l -q` in Windows PowerShell. Make sure you installed and signed in to your CLIs inside that first listed distribution.
2. **Confirm executables are on `PATH`**:
   ```bash
   command -v claude codex gemini
   ```
   If you use `nvm`, `fnm`, `volta`, `mise`, or `asdf`, make sure the active Node version containing these global packages is your default version.
3. **Refresh CloudCLI after installing or signing in**:
   Press `F5` inside the CloudCLI window, or right-click the system tray icon, choose **Exit**, and reopen CloudCLI so the server picks up newly installed CLIs and credentials.
