# CloudCLI Desktop Launcher for WSL 1.3.0

[English](README.md) · [简体中文](readme_zh-CN.md) · [繁體中文](readme_zh-TW.md)

Open the upstream CloudCLI web interface in a Windows desktop window. This WinForms launcher uses WebView2 to display the page and WSL to run the service.

[Release notes](CHANGELOG.md)

## Deployment

1. In Windows PowerShell, run `wsl -l -q` to find the distribution the launcher will use. If WSL is missing, install it with `wsl --install` and complete the distribution's first-run setup.
2. In the first listed WSL distribution, install Node.js 22 or later and npm. Then install [upstream CloudCLI (Siteboon)](https://github.com/siteboon/claudecodeui):

   ```bash
   npm install -g @cloudcli-ai/cloudcli
   command -v cloudcli
   cloudcli start --port 3001
   ```

   Open `http://localhost:3001/` in a Windows browser to check that the CloudCLI page loads. Press `Ctrl+C` in the WSL terminal to stop the test instance before trying automatic startup.
3. Install the Microsoft Edge WebView2 Runtime on Windows, then download and run [CloudCLI-Setup-1.3.0.exe](https://github.com/hanxcoder/cloudcli-wsl-launcher/releases/download/v1.3.0/CloudCLI-Setup-1.3.0.exe). On the final installer page, select **Run CloudCLI** to open it immediately. The checkbox is cleared by default.
4. Open CloudCLI from the Start menu or desktop shortcut if you used the installer. Check that the CloudCLI web interface loads in the desktop window.

Press `F5` to reload or `Ctrl+I` to open developer tools. Links requesting a new window open through Windows. The launcher follows the Windows display language. Set `CLOUDCLI_LANG` to `en`, `zh-CN`, or `zh-TW` to override it.
To update an existing installation silently, run the installer with `/UPDATE`. It uses the registered installation directory and exits with code 2 if the launcher is missing.


## Service and data

The launcher checks port 3001 before starting CloudCLI. It opens an existing CloudCLI service, starts one in WSL when the port is free, or reports an error when another service occupies the port. Closing the window hides it in the Windows notification area. Use **Open window** to restore it or **Exit** to stop a service started by this launcher and close the launcher. A service started separately keeps running. The launcher exits if the connected service stops.

The launcher writes to `logs\launcher.log` under its installation directory. Service startup output goes to `~/.cloudcli.log` in WSL. WebView2 stores its profile in `data\webview2` under the installation directory and imports an existing profile from `%LOCALAPPDATA%\CloudCLI\webview2` on first use if available. Uninstalling removes the local `logs` and `data` directories.

## Build

Run the following in Windows PowerShell from the project directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The script recreates `dist\`, compiles the launcher with the 64-bit .NET Framework 4.x C# compiler, and copies the WebView2 DLLs from `lib\`. If it finds NSIS's `makensis.exe` under `%LOCALAPPDATA%\electron-builder\Cache`, it also builds `CloudCLI-Setup-1.3.0.exe`.

## License

The Windows launcher is licensed under [GPL-3.0-only](LICENSE). [Upstream CloudCLI](https://github.com/siteboon/claudecodeui/blob/main/LICENSE) is licensed under AGPLv3. The bundled Microsoft WebView2 SDK DLLs are subject to Microsoft’s license terms.
