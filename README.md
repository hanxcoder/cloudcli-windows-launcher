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

From the project root in WSL, use Bash with a .NET SDK and NSIS on your PATH. The commands below use the .NET Framework 4.8 assemblies from the Windows host mounted at `/mnt/c`.

```bash
(
  set -e
  sdk=$(dotnet --list-sdks | tail -1)
  sdk_root=$(sed -n 's/.*\[\(.*\)\]/\1/p' <<< "$sdk")
  csc="$sdk_root/${sdk%% *}/Roslyn/bincore/csc.dll"
  fw=/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319
  mkdir -p dist
  dotnet "$csc" -nologo -nostdlib -target:winexe -platform:x64 \
    -out:dist/CloudCLI.exe -win32icon:src/icon.ico -win32manifest:src/app.manifest \
    -r:"$fw"/{mscorlib,System,System.Core,System.Windows.Forms,System.Drawing}.dll \
    -r:lib/Microsoft.Web.WebView2.{Core,WinForms}.dll src/Program.cs
  cp lib/*.dll LICENSE WebView2-*.txt src/icon.ico src/installer.nsi dist/
  cd dist
  makensis -WX -V2 installer.nsi
)
```

The installer is written to `dist/CloudCLI-Setup-1.3.0.exe`.

## License

CloudCLI Desktop Launcher for WSL is licensed under [GPL-3.0-only](LICENSE). [Upstream CloudCLI](https://github.com/siteboon/claudecodeui/blob/main/LICENSE) is licensed under AGPLv3. The bundled Microsoft WebView2 SDK DLLs are subject to Microsoft’s license terms.
