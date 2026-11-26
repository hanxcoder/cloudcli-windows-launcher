# Changelog

## 2.0.0 · 2026-11-26

First stable release of CloudCLI Desktop Launcher for WSL.

- Redesigned the first-run setup screen and startup loading screen inside WebView2 with a dark card layout, inline port validation, error banner, and loading spinner, reusing a single WebView2 instance through to the CloudCLI page.
- Upgraded the system tray menu on Windows 11 to use native DWM rounded corners, subtle border, soft system shadow, compact Fluent-proportioned items, and ClearType text rendering, with automatic fallback on Windows 10 and high-contrast themes.

## 1.4.0 · 2026-11-24

- Choose automatic setup or connect to an existing deployment from the main window on first launch. Custom setup supports a local port and an option to connect only. Successful settings carry over to the next launch.
- Automatic setup reuses CloudCLI when available. If installation is needed, npm installs it under the launcher's data directory.
- The launcher starts CloudCLI through WSL directly and checks the Node.js version. It also looks for compatible installations managed by tools such as nvm.
- Startup logs distinguish a missing executable on PATH from a failed probe and record timings. Service output is available in `logs/server.log` under the installation directory.
- Updated English, Simplified Chinese and Traditional Chinese setup messages.

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
