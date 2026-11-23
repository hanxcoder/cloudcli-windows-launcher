# Changelog

## 1.3.0 · 2026-11-23

- Closing the desktop window keeps CloudCLI available to browser sessions. Use the tray menu to reopen the window or exit. Exiting stops a service started by the launcher and leaves independently started services running.
- The launcher checks port 3001 before startup and reuses an existing CloudCLI service. A second launch leaves the running instance in place.
- The installer now offers to launch CloudCLI on the finish page. The option is off by default.
- Run the installer with `/UPDATE` to update an existing installation silently.

## 1.2.0 · 2026-11-23

First public Windows x64 installer. Changes since 1.0.0:

- The installer follows the scaling of each monitor using PerMonitorV2.
- The desktop window disables the default WebView2 context menu.
- Simplified Chinese startup messages use Microsoft YaHei UI.
- The Start menu uninstall shortcut uses the selected installer language.
- The installer includes the launcher's GPLv3 license and the WebView2 SDK license and notices.

Install upstream CloudCLI in WSL and the WebView2 Runtime on Windows before running the launcher. See the [deployment instructions](https://github.com/hanxcoder/cloudcli-wsl-launcher/blob/v1.2.0/README.md#deployment).
