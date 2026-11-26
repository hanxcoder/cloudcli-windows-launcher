# CloudCLI Desktop Launcher for WSL 2.1.0

[English](README.md) · [简体中文](readme_zh-CN.md) · [繁體中文](readme_zh-TW.md)

這個啟動器會在 WSL 中啟動上游 CloudCLI，再透過 WebView2 在 WinForms 視窗中開啟原有的網頁介面。CloudCLI 服務與網頁介面由上游專案提供。

[更新紀錄](CHANGELOG.md)

## 部署

1. 在 Windows PowerShell 執行 `wsl -l -q`，查看啟動器會使用哪個發行版。若尚未安裝 WSL，請執行 `wsl --install` 並完成發行版的初次設定。
2. 在清單中的第一個發行版安裝 Node.js 22 以上版本及 npm。若要自行安裝[上游 CloudCLI](https://github.com/siteboon/claudecodeui)，請執行：

   ```bash
   npm install -g @cloudcli-ai/cloudcli
   command -v cloudcli
   cloudcli start --port 3001
   ```

   在 Windows 上用瀏覽器開啟 `http://localhost:3001/`，確認頁面可以載入。接著在 WSL 終端機按 `Ctrl+C` 停止測試服務。下次開啟桌面視窗時，啟動器會自動啟動服務。
3. 在 Windows 安裝 Microsoft Edge WebView2 Runtime，再下載並執行 [CloudCLI-Setup-2.1.0.exe](https://github.com/hanxcoder/cloudcli-wsl-launcher/releases/download/v2.1.0/CloudCLI-Setup-2.1.0.exe)。如需立即啟動，可在安裝完成頁勾選「執行 CloudCLI」，預設不勾選。
4. 開啟 CloudCLI。選擇「自動設定並啟動」會在畫面透過即時進度條完成背景靜默安裝 WSL、應用程式內建立 Linux 使用者，並將獨立版 Node.js 22 LTS 與 CloudCLI 安裝至 WSL 原生 ext4 目錄（預設連接埠 3001）。選擇「自訂設定或使用現有部署」可指定本機連接埠，偵測到多個開發用發行版時支援選擇 WSL 發行版（自動過濾 Docker/Rancher/Podman 等容器發行版），並支援從 `PATH` 與主流版本管理器（`nvm`、`fnm`、`volta`、`asdf`、`mise`、`nodenv`、`pnpm`、`proto`、`n`、Linuxbrew、Nix 與登入 shell 備援）偵測 Node.js 22+。如果服務由你自行管理，請取消勾選服務未執行時自動啟動的選項。連線成功後會儲存設定。

按 `F5` 可重新載入，按 `Ctrl+I` 可開啟開發人員工具。要求開新視窗的連結會交由 Windows 處理。視窗預設採用 Windows 顯示語言，也可以在啟動前將 `CLOUDCLI_LANG` 設為 `en`、`zh-CN` 或 `zh-TW`。
若要在背景更新，請加上 `/UPDATE` 參數執行安裝程式。更新會使用已登記的安裝目錄。如果找不到已安裝的啟動器，安裝程式會傳回結束代碼 2。

## 安裝 AI 程式設計 CLI

CloudCLI 只是工作環境，實際執行任務的代理（**Claude Code**、**Codex CLI**、**Gemini CLI**）需要另外安裝在 WSL 中。如果你使用的是「自動設定並啟動」，不需要安裝 PowerShell 7、nvm，也不必開啟 Linux 終端機：

1. 開啟**命令提示字元**（按 `Win+R`，輸入 `cmd`，按 Enter），或從開始功能表開啟 **Windows PowerShell**。
2. 複製要使用的工具對應的兩行指令，逐行貼上執行。第一行負責安裝，第二行啟動一次以便登入。登入完成後按 `Ctrl+C` 離開（若未離開再按一次）。
3. 在 CloudCLI 通知區域圖示上按右鍵選擇「結束」，再重新開啟 CloudCLI，讓它偵測到新安裝的工具。

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

- 這些指令會將工具安裝到啟動器準備好的 Node.js 中，CloudCLI 會自動偵測。日後要更新，重新執行安裝那一行即可。
- 如果你在啟動器中指定了其他 WSL 發行版，請在 `wsl` 後面加上 `-d 發行版名稱`，例如 `wsl -d Debian PATH=...`。
- 如果你自行管理 Node.js（nvm、fnm、Volta 等），請在 WSL 終端機中安裝，詳見 [WSL 中部署 AI 程式設計 CLI 指南](docs/agent-clis.zh-TW.md)。

## 服務與資料

啟動器會先檢查指定的連接埠。若 CloudCLI 已在執行，就直接連線。如果允許自動啟動，連接埠可用時會在 WSL 啟動服務。連接埠遭其他程式占用時會顯示錯誤。關閉視窗後，啟動器會留在工作列的通知區域。從圖示選單選擇「開啟視窗」即可回到 CloudCLI。選擇「結束」會關閉啟動器，並停止由它啟動的服務。另行啟動的服務會繼續執行。如果目前連線的服務停止，啟動器也會結束。

啟動器記錄位於安裝目錄下的 `logs\launcher.log`，服務啟動記錄則位於同一目錄下的 `logs\server.log`。WebView2 使用者資料儲存在安裝目錄下的 `data\webview2`。若目錄尚未建立，啟動器會嘗試從 `%LOCALAPPDATA%\CloudCLI\webview2` 複製舊資料。解除安裝會刪除安裝目錄中的 `logs` 與 `data`。

## 從原始碼建置

在 WSL 的 Bash 中使用 .NET SDK 和 NSIS 編譯並封裝。所需工具、完整指令與輸出路徑請見[英文 README 的建置說明](README.md#build)。

## 授權條款

本專案的啟動器採用 [GPL-3.0-only](LICENSE)。[上游 CloudCLI](https://github.com/siteboon/claudecodeui/blob/main/LICENSE) 採用 AGPLv3。隨附的 Microsoft WebView2 SDK DLL 適用微軟的授權條款。
