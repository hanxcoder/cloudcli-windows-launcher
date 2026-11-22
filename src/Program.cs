using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CloudCLI
{
    static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        static void Main()
        {
            // 兜底：manifest 未生效时（旧系统）也强制 PerMonitorV2
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
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
        const string Url = "http://localhost:3001";
        static string AppDir;
        static string LogPath;

        WebView2 web;
        Label status;
        string distro = "Ubuntu";
        bool startedByUs;

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
            status.Font = new Font("Segoe UI", 12F);
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
            Shown += async delegate { await BootAsync(); };
            FormClosing += delegate { StopServer(); };
        }

        async Task BootAsync()
        {
            try
            {
                distro = DetectDistro();
                Log("distro: " + distro);

                if (!Probe())
                {
                    startedByUs = true;
                    Log("starting server");
                    SetStatus(T("正在启动 WSL 里的 CloudCLI 服务...", "正在啟動 WSL 中的 CloudCLI 服務...", "Starting the CloudCLI server in WSL..."));
                    await Task.Run(delegate { StartServer(); });
                }
                else
                {
                    Log("server already running");
                }
            }
            catch (Exception e)
            {
                Log("boot failed: " + e);
                SetStatus(T("启动失败：", "啟動失敗：", "Startup failed: ") + e.Message);
                return;
            }

            await ShowPage();
        }

        void StartServer()
        {
            string cli = CaptureWsl("-d " + distro + " -e bash -ic \"command -v cloudcli\"");
            Log("cloudcli: " + cli);
            string exe = string.IsNullOrEmpty(cli) ? "cloudcli" : cli;
            string inner = "nohup " + exe + " start > $HOME/.cloudcli.log 2>&1 & disown";
            string arg = "-d " + distro + " -e bash -ic \"" + inner + "\"";
            Log("start args: " + arg);
            StartDetached("wsl.exe", arg);

            int waited = 0;
            while (waited < 240)
            {
                Thread.Sleep(500);
                waited++;
                if (Probe()) { Log("server ready"); return; }
                if (waited == 40)
                {
                    Log("still down after 20s, rebooting WSL and retrying");
                    StartDetached("wsl.exe", "--shutdown");
                    Thread.Sleep(3000);
                    StartDetached("wsl.exe", arg);
                }
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

        void StopServer()
        {
            if (!startedByUs) return;
            startedByUs = false;
            Log("stopping cloudcli");
            try { StartDetached("wsl.exe", "-d " + distro + " -e bash -ic \"pkill -f cloudcli\""); } catch { }
            Log("--- window close ---");
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

        static void StartDetached(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception e) { Log("start failed: " + e.Message); }
        }

        static bool Probe()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Url + "/");
                req.Timeout = 2000;
                req.ReadWriteTimeout = 2000;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse()) return (int)resp.StatusCode < 400;
            }
            catch { return false; }
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