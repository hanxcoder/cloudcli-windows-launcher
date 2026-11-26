# 在 WSL 中部署 AI 程式設計 CLI（Claude Code、Codex、Gemini CLI 等）

[English](agent-clis.md) · [简体中文](agent-clis.zh-CN.md) · [繁體中文](agent-clis.zh-TW.md)

[CloudCLI](https://github.com/siteboon/claudecodeui) 本身只是伺服器端與網頁介面框架（提供桌面／網頁工作區、檔案瀏覽、Git 與對話工作階段管理），實際提供 AI 程式開發能力的後端來自你在 WSL 中安裝並登入的各項 CLI 工具。

請將要使用的 CLI 安裝在 **與 CloudCLI 相同的 WSL 發行版** 中（也就是在 Windows PowerShell 執行 `wsl -l -q` 所列出的第一個發行版）。

---

## 前置準備

請先在 WSL 終端機確認已具備 Node.js 22 以上版本與 `npm`（建議使用 [nvm](https://github.com/nvm-sh/nvm) 管理）：

```bash
node --version
npm --version
```

> **提示**：如果你在全新的 WSL 環境中使用啟動器的「自動設定並啟動」完成初始化，啟動器會將獨立版 Node.js 安裝於 WSL 內的 `~/.local/share/cloudcli-launcher/` 目錄下，不會更動你的 shell 設定。若要在 WSL 內全域安裝 Claude Code、Codex 等 CLI，請先在你的 WSL 使用者環境安裝 Node.js 22+（例如透過 `nvm`）。

---

## 1. Claude Code（Anthropic）

於 WSL 中安裝：

```bash
npm install -g @anthropic-ai/claude-code
```

驗證並完成初次登入：

```bash
claude --version
claude
```

依終端機提示完成 Anthropic 帳號登入（或設定 `ANTHROPIC_API_KEY`）。登入完成後即可離開終端機，直接在 CloudCLI 桌面視窗中使用。

---

## 2. OpenAI Codex CLI

於 WSL 中安裝：

```bash
npm install -g @openai/codex
```

驗證並登入：

```bash
codex --version
codex
```

依提示登入 ChatGPT 帳號，或在 WSL 環境中設定 `OPENAI_API_KEY`。

---

## 3. Gemini CLI（Google）

於 WSL 中安裝：

```bash
npm install -g @google/gemini-cli
```

驗證並登入：

```bash
gemini --version
gemini
```

依提示完成 Google 帳號授權，或設定 `GEMINI_API_KEY`。

---

## 4. 其他支援的 CLI（Cursor CLI、OpenCode 等）

上游 CloudCLI 亦支援 **Cursor CLI**、**OpenCode** 等終端機 Agent。最新支援清單與官方安裝說明請參閱上游 [CloudCLI 專案文件](https://github.com/siteboon/claudecodeui)。

---

## 驗證與排錯

1. **確認安裝於正確的 WSL 發行版**：
   在 Windows PowerShell 執行 `wsl -l -q`，確認 CLI 安裝並登入於清單中的第一個發行版。
2. **確認執行檔位於 `PATH` 中**：
   ```bash
   command -v claude codex gemini
   ```
   若使用 `nvm`、`fnm`、`volta`、`mise` 或 `asdf`，請確認裝有這些全域套件的 Node 版本已設為預設版本。
3. **安裝或登入後介面尚未辨識**：
   在 CloudCLI 視窗按 `F5` 重新載入，或於通知區域右鍵選單點擊 **結束** 後重新開啟，讓伺服器重新掃描 CLI 與登入憑證。
