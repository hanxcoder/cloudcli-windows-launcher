using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: System.Reflection.AssemblyVersion("1.4.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.4.0.0")]

namespace CloudCLI
{
    static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        static void Main()
        {
            bool firstInstance;
            using (var instance = new Mutex(false, @"Local\CloudCLI.WslLauncher", out firstInstance))
            {
                if (!firstInstance) return;

                // 兜底：manifest 未生效时（旧系统）也强制 PerMonitorV2
                try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }

    class MainForm : Form
    {
        // 0 = English, 1 = 简体中文, 2 = 繁體中文；可用 CLOUDCLI_LANG=en|zh-CN|zh-TW 强制
        static readonly int Lang = DetectLang();

        static int DetectLang()
        {
            string forced = (Environment.GetEnvironmentVariable("CLOUDCLI_LANG") ?? "").Trim().ToLowerInvariant();
            if (forced.Length > 0) return ParseLang(forced);
            return ParseLang((CultureInfo.CurrentUICulture.Name ?? "").ToLowerInvariant());
        }

        static int ParseLang(string tag)
        {
            if (tag.StartsWith("zh"))
            {
                if (tag.Contains("tw") || tag.Contains("hk") || tag.Contains("mo") || tag.Contains("hant")) return 2;
                return 1;
            }
            return 0;
        }

        static string LangName()
        {
            if (Lang == 1) return "zh-CN";
            if (Lang == 2) return "zh-TW";
            return "en-US";
        }

        static string T(string cn, string tw, string en)
        {
            if (Lang == 1) return cn;
            if (Lang == 2) return tw;
            return en;
        }
        int Port = 3001;
        string Url { get { return "http://localhost:" + Port.ToString(CultureInfo.InvariantCulture); } }
        string setupMode;
        string SetupFile { get { return Path.Combine(AppDir, "data", "setup.txt"); } }

        void LoadSetup()
        {
            if (!File.Exists(SetupFile)) return;
            var lines = File.ReadAllLines(SetupFile);
            int port;
            if (lines.Length != 2 || !int.TryParse(lines[1], out port) || port < 1 || port > 65535) return;
            if (lines[0] != "auto" && lines[0] != "existing" && lines[0] != "connect") return;
            setupMode = lines[0];
            Port = port;
        }

        void SaveSetup()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SetupFile));
            string temp = SetupFile + ".tmp";
            File.WriteAllLines(temp, new[] { setupMode, Port.ToString(CultureInfo.InvariantCulture) });
            if (File.Exists(SetupFile)) File.Replace(temp, SetupFile, null);
            else File.Move(temp, SetupFile);
        }

        void ShowSetup(string error)
        {
            var screen = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1,
                BackColor = status.BackColor, ForeColor = status.ForeColor, Font = status.Font, AutoScroll = true };
            screen.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            screen.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var panel = new FlowLayoutPanel { Anchor = AnchorStyles.None, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                FlowDirection = FlowDirection.TopDown, Padding = new Padding(16) };
            var heading = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 12),
                Text = T("设置 CloudCLI", "設定 CloudCLI", "Set up CloudCLI") };
            panel.Controls.Add(heading);
            if (error != null)
                panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(480, 0), Text = error,
                    ForeColor = Color.LightSalmon, Margin = new Padding(3, 0, 3, 12) });
            var automatic = new RadioButton { AutoSize = true, Text = T("帮我配置并启动", "自動設定並啟動", "Set it up for me") };
            var info = new Label { AutoSize = true, MaximumSize = new Size(440, 0), Text = T(
                "优先使用已有的 CloudCLI。需要安装时，会通过 npm 安装到启动器自己的目录。需要 WSL、Node.js 22 或更高版本和 npm。",
                "優先使用現有的 CloudCLI。需要安裝時，會透過 npm 安裝到啟動器自己的目錄。需要 WSL、Node.js 22 以上版本及 npm。",
                "Use an existing installation or download CloudCLI to the launcher's own folder. Requires WSL, Node.js 22 or later, and npm.") };
            var custom = new RadioButton { AutoSize = true, Text = T("自定义或使用已有部署", "自訂設定或使用現有部署", "Customize or use an existing setup") };
            var label = new Label { AutoSize = true, Text = T("本机端口", "本機連接埠", "Local port") };
            var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Port };
            var start = new CheckBox { AutoSize = true, Checked = setupMode != "connect", Text = T(
                "服务未运行时，启动 WSL 中已安装的 CloudCLI", "服務未執行時，啟動 WSL 裡已安裝的 CloudCLI",
                "Start the installed CloudCLI in WSL if the service is offline") };
            var next = new Button { AutoSize = true, Text = T("继续", "繼續", "Continue"), ForeColor = SystemColors.ControlText, BackColor = SystemColors.Control };
            panel.Controls.AddRange(new Control[] { automatic, info, custom, label, port, start, next });
            custom.CheckedChanged += delegate { label.Visible = port.Visible = start.Visible = custom.Checked; };
            automatic.Checked = setupMode == null || setupMode == "auto";
            custom.Checked = !automatic.Checked;
            label.Visible = port.Visible = start.Visible = custom.Checked;
            screen.Controls.Add(panel);
            Controls.Add(screen);
            screen.BringToFront();
            status.Visible = false;
            AcceptButton = next;
            automatic.Focus();
            next.Click += async delegate
            {
                next.Enabled = false;
                setupMode = automatic.Checked ? "auto" : start.Checked ? "existing" : "connect";
                Port = automatic.Checked ? 3001 : (int)port.Value;
                AcceptButton = null;
                screen.Dispose();
                await StartFromWindowAsync(true);
            };
        }

        async Task StartFromWindowAsync(bool configure)
        {
            SetStatus(T("正在启动 CloudCLI…", "正在啟動 CloudCLI…", "Starting CloudCLI…"));
            await BootAsync(configure);
            if (!connected && !exiting) ShowSetup(status.Text);
        }

        enum PortState { Available, CloudCli, Occupied, Unreachable }
        static string AppDir;
        static string LogPath;

        WebView2 web;
        Label status;
        const int TrayMenuCornerDiameter = 16;
        const int TrayMenuSelectionInset = 4;

        sealed class TrayMenuPopup : ContextMenuStrip
        {
            protected override Padding DefaultPadding
            {
                get
                {
                    Padding padding = base.DefaultPadding;
                    int cornerRadius = ScalePixels(TrayMenuCornerDiameter, DeviceDpi) / 2;
                    return new Padding(padding.Left, Math.Max(padding.Top, cornerRadius),
                        padding.Right, Math.Max(padding.Bottom, cornerRadius));
                }
            }
        }

        sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    base.OnRenderToolStripBackground(e);
                    return;
                }

                using (var brush = new SolidBrush(Color.FromArgb(245, 245, 245)))
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (SystemInformation.HighContrast) base.OnRenderToolStripBorder(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    base.OnRenderSeparator(e);
                    return;
                }

                int inset = ScalePixels(12, e.Graphics.DpiX);
                int y = e.Item.Height / 2;
                using (var pen = new Pen(Color.FromArgb(222, 222, 222)))
                    e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                Rectangle text = e.TextRectangle;
                text.Y = (e.Item.Height - text.Height) / 2;
                e.TextRectangle = text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    base.OnRenderMenuItemBackground(e);
                    return;
                }
                if (!e.Item.Selected && !e.Item.Pressed) return;

                var menu = e.ToolStrip;
                float dpi = e.Graphics.DpiX;
                int inset = ScalePixels(TrayMenuSelectionInset, dpi);
                Rectangle item = e.Item.Bounds;
                Rectangle inner = menu.ClientRectangle;
                inner.Inflate(-inset, -inset);

                Rectangle bounds = Rectangle.Intersect(item, inner);
                bounds.Offset(-item.Left, -item.Top);
                if (bounds.Width <= 0 || bounds.Height <= 0) return;

                using (var brush = new SolidBrush(Color.FromArgb(218, 218, 218)))
                    e.Graphics.FillRectangle(brush, bounds);
            }
        }

        static int ScalePixels(int value, float dpi)
        {
            return Math.Max(1, (int)Math.Round(value * dpi / 96f));
        }

        static GraphicsPath RoundedRectangle(Rectangle bounds, int diameter)
        {
            diameter = Math.Min(diameter, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        System.Windows.Forms.Timer serviceMonitor;
        Process ownedServer;
        bool connected;
        bool exiting;
        int failedChecks;

        public MainForm()
        {
            AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string logDir = Path.Combine(AppDir, "logs");
            try { Directory.CreateDirectory(logDir); } catch { }
            LogPath = Path.Combine(logDir, "launcher.log");
            Log("--- window start --- lang=" + LangName());

            Text = "CloudCLI";
            Width = 1440;
            Height = 900;
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(13, 13, 15);

            try
            {
                string ico = Path.Combine(AppDir, "icon.ico");
                if (File.Exists(ico)) Icon = new Icon(ico);
            }
            catch { }

            status = new Label();
            status.Text = T("正在启动 CloudCLI…", "正在啟動 CloudCLI…", "Starting CloudCLI...");
            status.ForeColor = Color.Gainsboro;
            status.BackColor = Color.FromArgb(13, 13, 15);
            status.TextAlign = ContentAlignment.MiddleCenter;
            status.Dock = DockStyle.Fill;
            status.Font = new Font(Lang == 1 ? "Microsoft YaHei UI" : "Segoe UI", 12F);
            Controls.Add(status);

            KeyPreview = true;
            KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F5 && web != null && web.CoreWebView2 != null)
                {
                    web.CoreWebView2.Reload();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.I && web != null && web.CoreWebView2 != null)
                {
                    web.CoreWebView2.OpenDevToolsWindow();
                    e.Handled = true;
                }
            };
            trayMenu = new TrayMenuPopup
            {
                ShowImageMargin = false,
                DropShadowEnabled = true,
                Renderer = new TrayMenuRenderer()
            };
            trayMenu.Opened += delegate
            {
                Version windowsVersion = Environment.OSVersion.Version;
                if (windowsVersion.Major < 10 || windowsVersion.Build < 22000 || SystemInformation.HighContrast)
                {
                    trayMenu.Region = null;
                    return;
                }

                using (var graphics = trayMenu.CreateGraphics())
                using (var path = RoundedRectangle(trayMenu.ClientRectangle, ScalePixels(TrayMenuCornerDiameter, graphics.DpiX)))
                    trayMenu.Region = new Region(path);
            };
            trayMenu.Items.Add(T("打开窗口", "開啟視窗", "Open window"), null, delegate { OpenWindow(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(T("退出", "結束", "Exit"), null, delegate { ExitLauncher(); });
            foreach (ToolStripItem item in trayMenu.Items)
                if (item is ToolStripMenuItem) item.Padding = new Padding(12, 5, 12, 5);
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "CloudCLI", ContextMenuStrip = trayMenu, Visible = true };
            tray.MouseClick += delegate (object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) OpenWindow();
            };

            serviceMonitor = new System.Windows.Forms.Timer { Interval = 5000 };
            serviceMonitor.Tick += async delegate { await CheckServiceAsync(); };
            Shown += async delegate
            {
                try { LoadSetup(); }
                catch (IOException e) { Log("could not read setup: " + e.Message); }
                if (setupMode == null) ShowSetup(null);
                else await StartFromWindowAsync(false);
            };
        }

        void OpenWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitLauncher()
        {
            exiting = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && connected && !exiting)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            exiting = true;
            serviceMonitor.Dispose();
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            StopOwnedServer();
            base.OnFormClosed(e);
        }

        void StopOwnedServer()
        {
            if (ownedServer == null) return;

            // EOF on the control pipe tells the WSL supervisor to stop its own child.
            try { ownedServer.StandardInput.Close(); }
            catch (IOException e) { Log("could not close server control pipe: " + e.Message); }
            finally
            {
                ownedServer.Dispose();
                ownedServer = null;
            }
        }

        async Task CheckServiceAsync()
        {
            serviceMonitor.Stop();
            if (ownedServer != null)
            {
                if (ownedServer.HasExited) ExitLauncher();
            }
            else
            {
                PortState state = await Task.Run(delegate { return Probe(); });
                if (exiting) return;

                failedChecks = state == PortState.CloudCli ? 0 : failedChecks + 1;
                if (failedChecks >= 2) ExitLauncher();
            }

            if (!exiting) serviceMonitor.Start();
        }

        async Task BootAsync(bool configure)
        {
            try
            {
                PortState state = await Task.Run(delegate { return Probe(); });
                if (exiting) return;
                if (state != PortState.Available && state != PortState.CloudCli) throw PortCheckError(state);

                if (state == PortState.Available)
                {
                    SetStatus(T("正在 WSL 中启动 CloudCLI 服务…", "正在 WSL 中啟動 CloudCLI 服務…", "Starting the CloudCLI server in WSL..."));
                    if (setupMode == "connect") throw new InvalidOperationException(T(
                        "请先启动已有的 CloudCLI 服务，然后重试。", "請先啟動現有的 CloudCLI 服務，再試一次。",
                        "Start your CloudCLI service, then try again."));
                    await StartServerAsync();
                }
                else
                {
                    Log("using existing CloudCLI server");
                }
                if (configure && !exiting) SaveSetup();
            }
            catch (Exception e)
            {
                if (exiting) return;
                StopOwnedServer();
                Log("boot failed: " + e);
                SetStatus(T("启动失败：", "啟動失敗：", "Startup failed: ") + e.Message);
                return;
            }

            if (exiting) return;
            connected = true;
            serviceMonitor.Start();
            await ShowPage();
        }

        async Task StartServerAsync()
        {
            string distro = await Task.Run(delegate { return DetectDistro(); });
            if (distro == null) throw new InvalidOperationException(T(
                "未找到可用的 WSL 发行版。请先安装并初始化一个发行版，然后重试。",
                "找不到可用的 WSL 發行版。請先安裝發行版並完成初次設定，再試一次。",
                "No WSL distribution was found. Install a distribution and complete its first-run setup, then try again."));
            Log("distro: " + distro);

            // WSL may have started a service while the distribution was detected.
            PortState state = await Task.Run(delegate { return Probe(); });
            if (exiting || state == PortState.CloudCli) return;
            if (state != PortState.Available) throw PortCheckError(state);

            var discovery = Stopwatch.StartNew();
            string node = await Task.Run(delegate { return FindNode(distro); });
            Log("Selected Node.js after " + discovery.ElapsedMilliseconds + " ms: " + node);
            if (exiting) return;
            state = await Task.Run(delegate { return Probe(); });
            if (exiting || state == PortState.CloudCli) return;
            if (state != PortState.Available) throw PortCheckError(state);
            var processInfo = new ProcessStartInfo("wsl.exe", DistroArgument(distro)
                + " --exec " + QuoteArgument(node) + " ./launcher-service.cjs " + setupMode + " " + Port)
            {
                WorkingDirectory = AppDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            };
            ownedServer = Process.Start(processInfo);

            var startup = Stopwatch.StartNew();
            while (startup.Elapsed < TimeSpan.FromSeconds(120))
            {
                state = await Task.Run(delegate { return Probe(); });
                if (exiting) return;

                if (ownedServer.HasExited)
                {
                    int exitCode = ownedServer.ExitCode;
                    StopOwnedServer();
                    if (state == PortState.CloudCli) return;
                    if (exitCode == -1)
                    {
                        Log("wsl.exe could not start the distribution: " + distro);
                        throw new InvalidOperationException(T("WSL 未能启动发行版：", "WSL 無法啟動發行版：",
                            "WSL could not start the distribution: ") + distro);
                    }
                    if (setupMode == "auto" && exitCode == 79)
                        throw new InvalidOperationException(T("CloudCLI 安装失败。请查看启动器目录下的 logs/server.log。",
                            "無法安裝 CloudCLI。請查看啟動器目錄下的 logs/server.log。", "CloudCLI installation failed. Check logs/server.log in the launcher folder."));
                    if (exitCode == 76)
                        throw new InvalidOperationException(T("未检测到 Node.js 管理工具，当前也没有可用的 Node.js 22 或更高版本。请在以下发行版安装 nvm 或 fnm，再安装 Node.js：",
                            "找不到 Node.js 版本管理工具，也沒有可用的 Node.js 22 以上版本。請先安裝 nvm 或 fnm，再安裝 Node.js：",
                            "No Node version manager or compatible Node.js runtime was found. Set up nvm or fnm and install Node.js 22 or later in ") + distro);
                    if (exitCode == 77)
                        throw new InvalidOperationException(T("未找到 CloudCLI 本体。请先自行安装，或选择“帮我配置并启动”。",
                            "找不到 CloudCLI。請先自行安裝，或選擇「自動設定並啟動」。",
                            "CloudCLI is missing. Install it yourself or choose Set it up for me."));
                    if (exitCode == 80)
                        throw new InvalidOperationException(T("已找到兼容的 Node.js，但此安装缺少 npm。请修复该 Node.js 安装后重试。",
                            "已找到相容的 Node.js，但缺少 npm。請修復這個 Node.js 安裝後再試一次。",
                            "A compatible Node.js was found, but npm is missing. Repair this Node.js installation and try again."));
                    if (exitCode == 78)
                        throw new InvalidOperationException(T("已检测到 Node.js 管理工具。请在以下发行版中安装或切换到 Node.js 22 或更高版本：",
                            "已找到 Node.js 版本管理工具。請在下列發行版安裝或切換至 Node.js 22 以上版本：", "Use your Node version manager to install or select Node.js 22 or later in ") + distro);
                    Log("CloudCLI exited during startup: " + exitCode);
                    throw new InvalidOperationException(T(
                        "CloudCLI 在启动过程中退出了。请查看启动器目录下的 logs/server.log。",
                        "CloudCLI 在啟動時結束。請查看啟動器目錄下的 logs/server.log。",
                        "CloudCLI exited during startup. Check logs/server.log in the launcher folder."));
                }
                if (state == PortState.CloudCli) { Log("server ready after " + startup.ElapsedMilliseconds + " ms"); return; }
                await Task.Delay(500);
                if (exiting) return;
            }
            Log("server NOT ready after 120s");
            throw new Exception(T("等待服务启动超时（2 分钟）。请查看日志 logs\\launcher.log", "已等候 2 分鐘，服務仍未就緒。請查看記錄檔 logs\\launcher.log", "Timed out after 2 minutes waiting for CloudCLI to start. Check logs\\launcher.log"));
        }

        async Task ShowPage()
        {
            SetStatus(T("正在加载 CloudCLI 页面…", "正在載入 CloudCLI 網頁…", "Loading CloudCLI…"));

            string runtimeVersion = null;
            try { runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            if (string.IsNullOrEmpty(runtimeVersion))
            {
                Log("webview2 runtime missing");
                SetStatus(T("缺少 WebView2 Runtime，无法显示界面。", "缺少 WebView2 Runtime，無法顯示介面。", "Install the Microsoft Edge WebView2 Runtime to display CloudCLI."));
                PromptInstallRuntime();
                return;
            }
            Log("webview2 runtime: " + runtimeVersion);

            try
            {
                string profile = Path.Combine(AppDir, "data", "webview2");
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudCLI", "webview2");
                try
                {
                    if (!Directory.Exists(profile) && Directory.Exists(legacy))
                    {
                        Log("migrating webview2 data from " + legacy);
                        CopyDir(legacy, profile);
                    }
                }
                catch (Exception ex) { Log("migrate failed: " + ex.Message); }
                Directory.CreateDirectory(profile);

                web = new WebView2();
                web.Dock = DockStyle.Fill;
                web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = profile };
                Controls.Add(web);
                web.BringToFront();

                await web.EnsureCoreWebView2Async(null);
                if (exiting) return;
                web.CoreWebView2.Settings.AreDevToolsEnabled = true;
                web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                    try { Process.Start(e.Uri); } catch { }
                };
                web.CoreWebView2.NavigationCompleted += delegate (object s2, CoreWebView2NavigationCompletedEventArgs ev)
                {
                    if (ev.IsSuccess) Log("navigation completed successfully");
                    else Log("navigation failed: " + ev.WebErrorStatus);
                };
                web.CoreWebView2.Navigate(Url);
                Log("navigated");
                status.Visible = false;
            }
            catch (Exception e)
            {
                if (exiting) return;
                Log("webview failed: " + e);
                SetStatus(T("WebView2 初始化失败：", "WebView2 初始化失敗：", "Could not initialize WebView2: ") + e.Message);
                PromptInstallRuntime();
            }
        }

        static string RuntimePromptText()
        {
            if (Lang == 1) return "CloudCLI 需要 Microsoft Edge WebView2 Runtime。是否打开微软的下载页面？";
            if (Lang == 2) return "CloudCLI 需要 Microsoft Edge WebView2 Runtime。要前往 Microsoft 的下載頁面嗎？";
            return "CloudCLI requires the Microsoft Edge WebView2 Runtime. Open the Microsoft download page?";
        }

        void PromptInstallRuntime()
        {
            DialogResult r = MessageBox.Show(this, RuntimePromptText(), "CloudCLI", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
            {
                try { Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703"); }
                catch (Exception e) { Log("open download page failed: " + e.Message); }
            }
        }

        void SetStatus(string text)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(delegate { status.Text = text; })); }
                else { status.Text = text; status.Visible = true; }
            }
            catch { }
        }

        static string DetectDistro()
        {
            string raw = CaptureWsl("-l -q").Replace("\0", "").Trim();
            foreach (string line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.Length > 64 || t.StartsWith("-")) continue;
                bool ok = true;
                foreach (char c in t)
                {
                    if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ' ')) { ok = false; break; }
                }
                if (ok) return t;
            }
            return null;
        }

        static string QuoteArgument(string value)
        {
            if (value.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("Invalid executable path");
            return value.Contains(" ") ? "\"" + value + "\"" : value;
        }

        static string FindNode(string distro)
        {
            string prefix = DistroArgument(distro) + " --exec ";
            string versionOnPath = CaptureWsl(prefix + "/usr/bin/env node --version", expectedMissing: "node");
            Version parsed;
            bool compatible = Version.TryParse(versionOnPath.TrimStart('v'), out parsed) && parsed.Major >= 22;
            // A usable pair on PATH takes precedence over any manager-specific layout.
            if (compatible && CaptureWsl(prefix + "/usr/bin/env cloudcli --version", expectedMissing: "cloudcli").Length > 0)
                return "node";
            string home = CaptureWsl(prefix + "/usr/bin/printenv HOME");
            var candidates = new System.Collections.Generic.List<string>();
            bool managerFound = false;
            string root = @"\\wsl.localhost\" + distro;
            if (home.StartsWith("/"))
            {
                foreach (string relative in new[] { "/.nvm/versions/node", "/.local/share/fnm/node-versions",
                    "/.volta/tools/image/node", "/.asdf/installs/nodejs", "/.local/share/mise/installs/node",
                    "/.nodenv/versions" })
                {
                    string versions = home + relative;
                    string directory = root + versions.Replace('/', '\\');
                    if (!Directory.Exists(directory)) continue;
                    managerFound = true;
                    var entries = Directory.GetDirectories(directory);
                    Array.Sort(entries, delegate(string left, string right)
                    {
                        Version a, b;
                        Version.TryParse(Path.GetFileName(left).TrimStart('v'), out a);
                        Version.TryParse(Path.GetFileName(right).TrimStart('v'), out b);
                        return System.Collections.Generic.Comparer<Version>.Default.Compare(b, a);
                    });
                    foreach (string entry in entries)
                    {
                        string bin = relative.Contains("/fnm/") ? "/installation/bin/node" : "/bin/node";
                        candidates.Add(versions + "/" + Path.GetFileName(entry) + bin);
                    }
                }
                managerFound |= File.Exists(root + home.Replace('/', '\\') + @"\.nvm\nvm.sh");
            }

            string fallback = compatible ? "node" : null;
            foreach (string candidate in candidates)
            {
                string version = CaptureWsl(prefix + QuoteArgument(candidate) + " --version");
                if (!Version.TryParse(version.TrimStart('v'), out parsed) || parsed.Major < 22) continue;
                if (fallback == null) fallback = candidate;
                string sibling = candidate.Substring(0, candidate.LastIndexOf('/') + 1) + "cloudcli";
                if (File.Exists(root + sibling.Replace('/', '\\'))) return candidate;
            }
            if (fallback != null) return fallback;
            if (managerFound) throw new InvalidOperationException(T("请在此发行版中启用 Node.js 22 或更高版本：",
                "請在此發行版啟用 Node.js 22 以上版本：", "Select Node.js 22 or later in ") + distro);
            throw new InvalidOperationException(T("未找到可用的 Node.js 或版本管理工具：",
                "找不到可用的 Node.js 或版本管理工具：", "No compatible Node.js installation or version manager was found in ") + distro);
        }

        static string DistroArgument(string distro)
        {
            if (!distro.Contains(" ")) return "-d " + distro;
            // Use the registered GUID for multiword names. Some WSL versions
            // treat quotes around the distribution name as part of the name.
            using (var registrations = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Lxss"))
            {
                if (registrations != null)
                    foreach (string id in registrations.GetSubKeyNames())
                        using (var entry = registrations.OpenSubKey(id))
                        {
                            Guid guid;
                            if (entry != null && string.Equals(entry.GetValue("DistributionName") as string,
                                distro, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(id, out guid))
                                return "--distribution-id " + guid.ToString("B");
                        }
            }
            throw new InvalidOperationException(T("找不到 WSL 发行版：", "找不到 WSL 發行版：",
                "Could not find the registered WSL distribution: ") + distro);
        }

        static string CaptureWsl(string args, string expectedMissing = null)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var psi = new ProcessStartInfo("wsl.exe", args);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    var errors = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(20000))
                    {
                        Log("WSL probe timed out after " + timer.ElapsedMilliseconds + " ms: " + args);
                        p.Kill();
                        return "";
                    }
                    string detail = errors.Result.Replace("\0", "").Trim();
                    string context = args + " (exit " + p.ExitCode + ", " + timer.ElapsedMilliseconds + " ms)";
                    if (expectedMissing != null && p.ExitCode == 127)
                    {
                        Log(expectedMissing + " unavailable on PATH; checking managed installations. " + context);
                        return "";
                    }
                    if (p.ExitCode != 0)
                    {
                        if (detail.Length == 0) detail = output.Result.Replace("\0", "").Trim();
                        Log("WSL probe failed: " + context + (detail.Length == 0 ? "" : " - " + detail));
                        return "";
                    }
                    Log("WSL probe completed: " + context);
                    return output.Result.Replace("\0", "").Trim();
                }
            }
            catch (Exception e) { Log("WSL probe error after " + timer.ElapsedMilliseconds + " ms: " + args + " - " + e.Message); return ""; }
        }

        PortState Probe()
        {
            using (var client = new TcpClient())
            {
                try
                {
                    var connection = client.ConnectAsync("localhost", Port);
                    if (!connection.Wait(5000)) return PortState.Unreachable;
                }
                catch (AggregateException e)
                {
                    var socketError = e.GetBaseException() as SocketException;
                    return socketError != null && socketError.SocketErrorCode == SocketError.ConnectionRefused
                        ? PortState.Available : PortState.Unreachable;
                }
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(Url + "/");
                request.Timeout = 2000;
                request.ReadWriteTimeout = 2000;
                request.AllowAutoRedirect = false;
                request.Proxy = null;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    if (response.StatusCode != HttpStatusCode.OK) return PortState.Occupied;

                    // Older CloudCLI releases can still use the original page title.
                    var prefix = new char[8192];
                    int length = reader.ReadBlock(prefix, 0, prefix.Length);
                    const string cloudCliTitle = @"<title>\s*(CloudCLI(?: UI)?|Claude Code UI)\s*</title>";
                    if (Regex.IsMatch(new string(prefix, 0, length), cloudCliTitle, RegexOptions.IgnoreCase))
                        return PortState.CloudCli;
                }
            }
            catch (WebException) { }
            catch (IOException) { }

            return PortState.Occupied;
        }

        Exception PortCheckError(PortState state)
        {
            if (state == PortState.Unreachable)
                return new InvalidOperationException(string.Format(T(
                    "无法检查本机端口 {0}。请稍后重试。",
                    "無法檢查本機連接埠 {0}。請稍後再試。",
                    "Could not check local port {0}. Try again."), Port));

            return new InvalidOperationException(string.Format(T(
                "端口 {0} 已被占用，无法确认该服务是 CloudCLI。",
                "連接埠 {0} 使用中，無法確認目前使用它的服務是否為 CloudCLI。",
                "Port {0} is in use. The launcher could not verify that CloudCLI is running on it."), Port));
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true); } catch { }
            }
            foreach (string d in Directory.GetDirectories(from))
            {
                CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
            }
        }

        static void Log(string m)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  " + m + "\r\n"); } catch { }
        }
    }
}