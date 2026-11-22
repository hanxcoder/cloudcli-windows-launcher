# CloudCLI Desktop Launcher for WSL 1.1.0

[English](README.md) · [简体中文](readme_zh-CN.md) · [繁體中文](readme_zh-TW.md)

這個 Windows 啟動器會在 WSL 中啟動上游 CloudCLI，再透過 WebView2 在 WinForms 視窗中開啟原有的網頁介面。CloudCLI 服務與網頁介面由上游專案提供。

1.1.0 安裝程式採用 PerMonitorV2，依各螢幕的縮放比例顯示。桌面視窗已停用 WebView2 預設的右鍵選單。

## 部署

1. 在 Windows PowerShell 執行 `wsl -l -q`，查看啟動器會使用哪個發行版。若尚未安裝 WSL，請執行 `wsl --install` 並完成發行版的初次設定。
2. 在清單中的第一個 WSL 發行版安裝 Node.js 22 或更新版本及 npm，再安裝 [Siteboon 的上游 CloudCLI](https://github.com/siteboon/claudecodeui)：

   ```bash
   npm install -g @cloudcli-ai/cloudcli
   command -v cloudcli
   cloudcli start --port 3001
   ```

   在 Windows 瀏覽器開啟 `http://localhost:3001/`，確認頁面可以載入。接著在 WSL 終端機按 `Ctrl+C` 停止測試用的服務，以便確認桌面啟動器能自動啟動服務。
3. 在 Windows 安裝 Microsoft Edge WebView2 Runtime。複製此儲存庫，依[從原始碼建置](#從原始碼建置)產生並執行 `dist\CloudCLI-Setup-1.1.0.exe`。若沒有 NSIS，可直接執行 `dist\CloudCLI.exe`，並將隨附的 DLL 留在同一目錄。
4. 若使用安裝程式，請從開始功能表或桌面捷徑開啟 CloudCLI。確認桌面視窗可以載入 CloudCLI 網頁。

按 `F5` 可重新載入，按 `Ctrl+I` 可開啟開發人員工具。要求開新視窗的連結會交由 Windows 處理。視窗預設採用 Windows 顯示語言，也可以在啟動前將 `CLOUDCLI_LANG` 設為 `en`、`zh-CN` 或 `zh-TW`。

按 `F5` 可重新載入，按 `Ctrl+I` 可開啟開發人員工具。要求開新視窗的連結會交由 Windows 處理。視窗預設採用 Windows 顯示語言，也可以在啟動前將 `CLOUDCLI_LANG` 設為 `en`、`zh-CN` 或 `zh-TW`。

## 服務與資料

啟動器會先連線至 `http://localhost:3001/`，若連線失敗，就在 WSL 中執行 `cloudcli start`。如果約 20 秒後仍無法連線，1.1.0 版會執行 `wsl --shutdown` 再試一次。這會關閉所有執行中的 WSL 發行版。如果啟動器曾嘗試啟動服務，關閉視窗時就會在該發行版執行 `pkill -f cloudcli`。命令列符合條件的其他程序也可能被終止。

啟動器記錄位於安裝目錄下的 `logs\launcher.log`，服務啟動記錄則位於 WSL 的 `~/.cloudcli.log`。WebView2 使用者資料儲存在安裝目錄下的 `data\webview2`。若目錄尚未建立，啟動器會嘗試從 `%LOCALAPPDATA%\CloudCLI\webview2` 複製舊資料。解除安裝會刪除安裝目錄中的 `logs` 與 `data`。

## 從原始碼建置

在專案目錄開啟 Windows PowerShell 並執行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

指令碼會重建 `dist\`，使用 .NET Framework 4.x 的 64 位元 C# 編譯器建置啟動器，再複製 `lib\` 中的 WebView2 DLL。若在 `%LOCALAPPDATA%\electron-builder\Cache` 找到 NSIS 的 `makensis.exe`，也會產生 `CloudCLI-Setup-1.1.0.exe`。
