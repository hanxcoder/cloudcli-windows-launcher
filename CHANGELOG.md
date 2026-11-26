# Changelog

## 2.1.0 · 2026-11-27

Major update bringing zero-to-one automatic WSL/Node.js/CloudCLI onboarding, live deployment progress, multi-distro support, and expanded Node.js version manager detection.

- **End-to-end automatic setup (Option 1)**: Automatically installs WSL (`wsl --install -d Ubuntu --no-launch`) silently in the background with live log streaming, guides first-time Linux username/password creation inside an in-app WebView2 page (never popping up external consoles), downloads and verifies an isolated Node.js 22 LTS runtime, and installs `@cloudcli-ai/cloudcli` onto WSL native ext4 storage (`~/.local/share/cloudcli-launcher/`) for fast installation and startup.
- **Live deployment progress UI**: Replaced the static spinner with a 4-step live checklist featuring per-step progress bars (real MB / percentage for Node.js downloads, live progress parsing for WSL installation, indeterminate bar for npm install) and an elapsed `MM:SS` timer. Local port probing stays silent unless an error occurs.
- **Multi-WSL distribution support**: Filters out container-engine distributions (`docker-desktop`, `docker-desktop-data`, `rancher-desktop`, `rancher-desktop-data`, `podman-machine-*`) so developer distributions are never shadowed, and adds a WSL distribution selector in **Customize or use an existing setup** when two or more distributions are detected (persisted in `data/setup.txt` with backward compatibility).
- **4-tier Node.js 22+ & CloudCLI detection**: Checks system `PATH`, expanded version manager and package paths (`nvm`, `fnm`, `volta`, `asdf`, `mise`, `nodenv`, `pnpm`, `proto`, `n`, Linuxbrew, Nix, plus manager global/shim `cloudcli` binaries), a 6-second login interactive shell fallback (`"$SHELL" -lic`), and the launcher-managed isolated runtime.
- **Reliability & docs**: Fixed UTF-16LE/BOM decoding for `wsl.exe` output on localized Windows systems, separated install (600s) and server boot (120s) timeouts, and added trilingual [AI Coding CLI deployment guides](docs/agent-clis.md) (`en`, `zh-CN`, `zh-TW`).

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
