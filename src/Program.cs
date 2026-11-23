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

[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.0.0")]

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
        const int Port = 3001;
        const string Url = "http://localhost:3001";

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
            status.Text = T("正在启动 CloudCLI...", "正在啟動 CloudCLI...", "Starting CloudCLI...");
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
            Shown += async delegate { await BootAsync(); };
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

        async Task BootAsync()
        {
            try
            {
                PortState state = await Task.Run(delegate { return Probe(); });
                if (exiting) return;
                if (state != PortState.Available && state != PortState.CloudCli) throw PortCheckError(state);

                if (state == PortState.Available)
                {
                    SetStatus(T("正在启动 WSL 里的 CloudCLI 服务...", "正在啟動 WSL 中的 CloudCLI 服務...", "Starting the CloudCLI server in WSL..."));
                    await StartServerAsync();
                }
                else
                {
                    Log("using existing CloudCLI server");
                }
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
            Log("distro: " + distro);

            // WSL may have started a service while the distribution was detected.
            PortState state = await Task.Run(delegate { return Probe(); });
            if (exiting || state == PortState.CloudCli) return;
            if (state != PortState.Available) throw PortCheckError(state);

            // Use a private stdin pipe for lifetime control. Only signal the child
            // created by this supervisor, and end the reader if that child exits.
            string command = "exec 3<&0; cloudcli start --port " + Port
                + " > ~/.cloudcli.log 2>&1 & child=$!; "
                + "(read -r stop <&3; kill -TERM $child 2>/dev/null) & watcher=$!; "
                + "wait $child; result=$?; kill $watcher 2>/dev/null; exit $result";
            string args = "-d " + distro + " --cd ~ -e bash -ic \"" + command + "\"";
            ownedServer = Process.Start(new ProcessStartInfo("wsl.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            });

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
                    Log("CloudCLI exited during startup: " + exitCode);
                    throw new InvalidOperationException(T(
                        "CloudCLI 启动后退出。请检查 WSL 中的 ~/.cloudcli.log。",
                        "CloudCLI 啟動後結束。請檢查 WSL 中的 ~/.cloudcli.log。",
                        "CloudCLI exited during startup. Check ~/.cloudcli.log in WSL."));
                }
                if (state == PortState.CloudCli) { Log("server ready"); return; }
                await Task.Delay(500);
                if (exiting) return;
            }
            Log("server NOT ready after 120s");
            throw new Exception(T("服务在 120 秒内没有就绪，详见 logs\\launcher.log", "服務在 120 秒內未就緒，詳見 logs\\launcher.log", "Server did not become ready in 120s - see logs\\launcher.log"));
        }

        async Task ShowPage()
        {
            SetStatus(T("正在加载界面...", "正在載入介面...", "Loading UI..."));

            string runtimeVersion = null;
            try { runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            if (string.IsNullOrEmpty(runtimeVersion))
            {
                Log("webview2 runtime missing");
                SetStatus(T("缺少 WebView2 Runtime，无法显示界面。", "缺少 WebView2 Runtime，無法顯示介面。", "WebView2 Runtime is missing - cannot render the UI."));
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
                    Log("navigation completed: success=" + ev.IsSuccess + " error=" + ev.WebErrorStatus);
                };
                web.CoreWebView2.Navigate(Url);
                Log("navigated");
                status.Visible = false;
            }
            catch (Exception e)
            {
                if (exiting) return;
                Log("webview failed: " + e);
                SetStatus(T("WebView2 初始化失败：", "WebView2 初始化失敗：", "WebView2 init failed: ") + e.Message);
                PromptInstallRuntime();
            }
        }

        static string RuntimePromptText()
        {
            if (Lang == 1) return "CloudCLI 需要 Microsoft Edge WebView2 Runtime。要打开微软下载页面吗？";
            if (Lang == 2) return "CloudCLI 需要 Microsoft Edge WebView2 Runtime。要開啟 Microsoft 下載頁面嗎？";
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
            return "Ubuntu";
        }

        static string CaptureWsl(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("wsl.exe", args);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                    return outp.Replace("\0", "").Trim();
                }
            }
            catch (Exception e) { Log("wsl capture failed: " + e.Message); return ""; }
        }

        static PortState Probe()
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

        static Exception PortCheckError(PortState state)
        {
            if (state == PortState.Unreachable)
                return new InvalidOperationException(T(
                    "无法检查本机端口 3001。请稍后重试。",
                    "無法檢查本機連接埠 3001。請稍後再試。",
                    "Could not check local port 3001. Try again."));

            return new InvalidOperationException(T(
                "端口 3001 已被占用，无法确认该服务是 CloudCLI。",
                "連接埠 3001 已被占用，無法確認該服務是 CloudCLI。",
                "Port 3001 is in use and the service could not be identified as CloudCLI."));
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