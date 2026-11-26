using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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

[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

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

        bool webReady;
        bool showingInternalPage;
        bool setupSubmitting;

        async Task<bool> EnsureWebViewAsync()
        {
            if (webReady && web != null && web.CoreWebView2 != null) return true;

            string runtimeVersion = null;
            try { runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            if (string.IsNullOrEmpty(runtimeVersion))
            {
                Log("webview2 runtime missing");
                SetStatus(T("缺少 WebView2 Runtime，无法显示界面。", "缺少 WebView2 Runtime，無法顯示介面。", "Install the Microsoft Edge WebView2 Runtime to display CloudCLI."));
                PromptInstallRuntime();
                return false;
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
                web.DefaultBackgroundColor = Color.FromArgb(13, 13, 15);
                web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = profile };
                Controls.Add(web);
                web.BringToFront();

                await web.EnsureCoreWebView2Async(null);
                if (exiting) return false;
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
                    if (!showingInternalPage)
                    {
                        if (ev.IsSuccess) Log("navigation completed successfully");
                        else Log("navigation failed: " + ev.WebErrorStatus);
                    }
                };
                web.CoreWebView2.WebMessageReceived += async delegate (object s, CoreWebView2WebMessageReceivedEventArgs ev)
                {
                    await HandleWebMessageAsync(ev);
                };
                webReady = true;
                return true;
            }
            catch (Exception e)
            {
                if (exiting) return false;
                Log("webview failed: " + e);
                SetStatus(T("WebView2 初始化失败：", "WebView2 初始化失敗：", "Could not initialize WebView2: ") + e.Message);
                PromptInstallRuntime();
                return false;
            }
        }

        async Task HandleWebMessageAsync(CoreWebView2WebMessageReceivedEventArgs ev)
        {
            if (exiting || connected || !showingInternalPage || setupSubmitting) return;
            string message;
            try { message = ev.TryGetWebMessageAsString(); }
            catch { return; }
            if (string.IsNullOrEmpty(message)) return;

            string[] parts = message.Split('|');
            if (parts.Length != 3 || parts[0] != "submit") return;
            string mode = parts[1];
            int chosenPort;
            if (mode != "auto" && mode != "existing" && mode != "connect") return;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out chosenPort) || chosenPort < 1 || chosenPort > 65535) return;

            setupSubmitting = true;
            setupMode = mode;
            Port = mode == "auto" ? 3001 : chosenPort;
            try
            {
                await StartFromWindowAsync(true);
            }
            finally
            {
                setupSubmitting = false;
            }
        }

        void ShowSetup(string error)
        {
            if (!webReady || web == null || web.CoreWebView2 == null)
            {
                SetStatus(error ?? T("缺少 WebView2 Runtime，无法显示设置界面。", "缺少 WebView2 Runtime，無法顯示設定介面。", "WebView2 Runtime is required to display setup."));
                return;
            }
            showingInternalPage = true;
            status.Visible = false;
            web.Visible = true;
            web.BringToFront();
            web.CoreWebView2.NavigateToString(BuildSetupHtml(error));
            web.Focus();
        }

        void ShowLoading(string text)
        {
            if (!webReady || web == null || web.CoreWebView2 == null)
            {
                SetStatus(text);
                return;
            }
            showingInternalPage = true;
            status.Visible = false;
            web.Visible = true;
            web.BringToFront();
            web.CoreWebView2.NavigateToString(BuildLoadingHtml(text));
        }

        static string JsEscape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "\\n").Replace("</", "<\\/");
        }

        static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? "");
        }

        static string SharedCss()
        {
            return @"
                :root {
                    --bg: #0d0d0f;
                    --surface: #15161a;
                    --surface-hover: #1b1d23;
                    --surface-active: #171d29;
                    --border: rgba(255, 255, 255, 0.08);
                    --border-strong: rgba(255, 255, 255, 0.16);
                    --accent: #3b82f6;
                    --accent-hover: #2563eb;
                    --accent-soft: rgba(59, 130, 246, 0.14);
                    --text: #f3f4f6;
                    --text-secondary: #9ca3af;
                    --text-muted: #6b7280;
                    --danger-bg: rgba(239, 68, 68, 0.10);
                    --danger-border: rgba(239, 68, 68, 0.32);
                    --danger-text: #fca5a5;
                    --radius: 14px;
                }
                * { box-sizing: border-box; margin: 0; padding: 0; }
                html, body {
                    height: 100%;
                    background: var(--bg);
                    color: var(--text);
                    font-family: 'Segoe UI Variable', 'Segoe UI', 'Microsoft YaHei UI', system-ui, -apple-system, sans-serif;
                    -webkit-font-smoothing: antialiased;
                    user-select: none;
                }
                body {
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    padding: 24px;
                    background-image:
                        radial-gradient(640px 320px at 50% 0%, rgba(59, 130, 246, 0.12), transparent 70%),
                        radial-gradient(480px 280px at 85% 100%, rgba(99, 102, 241, 0.07), transparent 70%);
                }
            ";
        }

        static string LogoSvg()
        {
            return @"<svg width=""26"" height=""26"" viewBox=""0 0 24 24"" fill=""none"" aria-hidden=""true"">
                <rect x=""2.5"" y=""3.5"" width=""19"" height=""17"" rx=""4.5"" stroke=""#60a5fa"" stroke-width=""1.7""/>
                <path d=""M7.5 9.5L10.5 12L7.5 14.5"" stroke=""#f3f4f6"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""/>
                <path d=""M12.5 14.5H16.5"" stroke=""#60a5fa"" stroke-width=""1.8"" stroke-linecap=""round""/>
            </svg>";
        }

        string BuildLoadingHtml(string message)
        {
            return "<!DOCTYPE html><html lang=\"" + LangName() + "\"><head><meta charset=\"utf-8\">"
                + "<style>" + SharedCss() + @"
                .wrap {
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    gap: 20px;
                    text-align: center;
                    max-width: 480px;
                }
                .brand {
                    width: 56px;
                    height: 56px;
                    border-radius: 16px;
                    background: linear-gradient(145deg, rgba(59, 130, 246, 0.18), rgba(99, 102, 241, 0.08));
                    border: 1px solid rgba(96, 165, 250, 0.25);
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    box-shadow: 0 12px 32px rgba(0, 0, 0, 0.35);
                }
                .spinner {
                    width: 24px;
                    height: 24px;
                    border: 2.5px solid rgba(255, 255, 255, 0.12);
                    border-top-color: #60a5fa;
                    border-radius: 50%;
                    animation: spin 0.8s linear infinite;
                }
                @keyframes spin { to { transform: rotate(360deg); } }
                .status-row {
                    display: flex;
                    align-items: center;
                    gap: 12px;
                }
                .msg {
                    font-size: 14.5px;
                    color: var(--text-secondary);
                    line-height: 1.5;
                    user-select: text;
                }
                .sub {
                    font-size: 12px;
                    color: var(--text-muted);
                    letter-spacing: 0.02em;
                }
                </style></head><body>
                <div class=""wrap"">
                    <div class=""brand"">" + LogoSvg() + @"</div>
                    <div class=""status-row"">
                        <div class=""spinner""></div>
                        <div class=""msg"" id=""statusText"">" + Html(message) + @"</div>
                    </div>
                    <div class=""sub"">CloudCLI Desktop Launcher for WSL</div>
                </div>
                <script>
                    window.__setStatus = function(t) {
                        var el = document.getElementById('statusText');
                        if (el) el.textContent = t;
                    };
                </script></body></html>";
        }

        string BuildSetupHtml(string error)
        {
            bool isAuto = setupMode == null || setupMode == "auto";
            bool startWhenOffline = setupMode != "connect";
            string title = T("设置 CloudCLI", "設定 CloudCLI", "Set up CloudCLI");
            string subtitle = T(
                "选择启动方式，连接 WSL 中的 CloudCLI 服务",
                "選擇啟動方式，連線至 WSL 中的 CloudCLI 服務",
                "Choose how to connect to CloudCLI in WSL");
            string autoTitle = T("帮我配置并启动", "自動設定並啟動", "Set it up for me");
            string recommended = T("推荐", "推薦", "Recommended");
            string autoDesc = T(
                "优先复用已有的 CloudCLI；需要安装时，会通过 npm 安装到启动器自己的目录。需要 WSL、Node.js 22 或更高版本和 npm。",
                "優先使用現有的 CloudCLI；需要安裝時，會透過 npm 安裝到啟動器自己的目錄。需要 WSL、Node.js 22 以上版本及 npm。",
                "Use an existing installation or download CloudCLI to the launcher's own folder. Requires WSL, Node.js 22 or later, and npm.");
            string customTitle = T("自定义或使用已有部署", "自訂設定或使用現有部署", "Customize or use an existing setup");
            string customDesc = T(
                "指定自定义本机端口，或直接连接你自己管理的 CloudCLI 服务。",
                "指定自訂本機連接埠，或直接連線至你自行管理的 CloudCLI 服務。",
                "Use a custom local port or connect directly to a service you manage yourself.");
            string portLabel = T("本机端口", "本機連接埠", "Local port");
            string portHint = T("范围 1 – 65535", "範圍 1 – 65535", "Range 1 – 65535");
            string portErrText = T("请输入 1 到 65535 之间的有效端口号", "請輸入 1 到 65535 之間的有效連接埠號碼", "Enter a valid port number between 1 and 65535");
            string startLabel = T(
                "服务未运行时，自动启动 WSL 中已安装的 CloudCLI",
                "服務未執行時，自動啟動 WSL 裡已安裝的 CloudCLI",
                "Start the installed CloudCLI in WSL if the service is offline");
            string continueText = T("继续", "繼續", "Continue");
            string startingText = T("正在启动…", "正在啟動…", "Starting…");
            string footerHint = T(
                "首次连接成功后会自动记住你的选择",
                "首次連線成功後會自動記住你的選擇",
                "Your choice will be saved after a successful connection");

            string errorHtml = "";
            if (!string.IsNullOrEmpty(error))
            {
                errorHtml = @"<div class=""alert"" role=""alert"">
                    <svg class=""alert-icon"" width=""18"" height=""18"" viewBox=""0 0 24 24"" fill=""none"">
                        <circle cx=""12"" cy=""12"" r=""9.5"" stroke=""#f87171"" stroke-width=""1.8""/>
                        <path d=""M12 7.5V12.5"" stroke=""#f87171"" stroke-width=""1.8"" stroke-linecap=""round""/>
                        <circle cx=""12"" cy=""16.2"" r=""1"" fill=""#f87171""/>
                    </svg>
                    <div class=""alert-text"">" + Html(error) + @"</div>
                </div>";
            }

            return "<!DOCTYPE html><html lang=\"" + LangName() + "\"><head><meta charset=\"utf-8\">"
                + "<style>" + SharedCss() + @"
                .card {
                    width: 100%;
                    max-width: 540px;
                    background: var(--surface);
                    border: 1px solid var(--border);
                    border-radius: 20px;
                    padding: 32px;
                    box-shadow: 0 24px 64px rgba(0, 0, 0, 0.55), 0 1px 0 rgba(255, 255, 255, 0.04) inset;
                }
                .header {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                    margin-bottom: 24px;
                }
                .logo {
                    width: 44px;
                    height: 44px;
                    border-radius: 12px;
                    background: linear-gradient(145deg, rgba(59, 130, 246, 0.2), rgba(99, 102, 241, 0.08));
                    border: 1px solid rgba(96, 165, 250, 0.28);
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    flex-shrink: 0;
                }
                .title {
                    font-size: 20px;
                    font-weight: 600;
                    letter-spacing: -0.01em;
                    color: var(--text);
                }
                .subtitle {
                    font-size: 13px;
                    color: var(--text-secondary);
                    margin-top: 3px;
                }
                .alert {
                    display: flex;
                    align-items: flex-start;
                    gap: 10px;
                    background: var(--danger-bg);
                    border: 1px solid var(--danger-border);
                    border-radius: 12px;
                    padding: 12px 14px;
                    margin-bottom: 20px;
                }
                .alert-icon { flex-shrink: 0; margin-top: 1px; }
                .alert-text {
                    font-size: 13px;
                    line-height: 1.55;
                    color: var(--danger-text);
                    user-select: text;
                    word-break: break-word;
                }
                .options {
                    display: flex;
                    flex-direction: column;
                    gap: 12px;
                }
                .option {
                    border: 1px solid var(--border);
                    border-radius: var(--radius);
                    padding: 16px 18px;
                    background: rgba(255, 255, 255, 0.015);
                    cursor: pointer;
                    transition: border-color 0.16s ease, background 0.16s ease, box-shadow 0.16s ease;
                }
                .option:hover {
                    background: var(--surface-hover);
                    border-color: var(--border-strong);
                }
                .option.selected {
                    background: var(--surface-active);
                    border-color: rgba(59, 130, 246, 0.65);
                    box-shadow: 0 0 0 1px rgba(59, 130, 246, 0.25);
                }
                .opt-head {
                    display: flex;
                    align-items: center;
                    gap: 12px;
                }
                .radio {
                    width: 18px;
                    height: 18px;
                    border-radius: 50%;
                    border: 2px solid var(--text-muted);
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    flex-shrink: 0;
                    transition: border-color 0.15s ease;
                }
                .option.selected .radio {
                    border-color: var(--accent);
                }
                .radio::after {
                    content: '';
                    width: 8px;
                    height: 8px;
                    border-radius: 50%;
                    background: var(--accent);
                    transform: scale(0);
                    transition: transform 0.15s ease;
                }
                .option.selected .radio::after {
                    transform: scale(1);
                }
                .opt-title-row {
                    display: flex;
                    align-items: center;
                    gap: 8px;
                    flex-wrap: wrap;
                    flex: 1;
                }
                .opt-title {
                    font-size: 14.5px;
                    font-weight: 600;
                    color: var(--text);
                }
                .badge {
                    font-size: 11px;
                    font-weight: 600;
                    padding: 2px 8px;
                    border-radius: 999px;
                    background: var(--accent-soft);
                    color: #93c5fd;
                    border: 1px solid rgba(96, 165, 250, 0.25);
                }
                .pill {
                    font-size: 11px;
                    font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace;
                    padding: 2px 7px;
                    border-radius: 6px;
                    background: rgba(255, 255, 255, 0.05);
                    color: var(--text-secondary);
                    margin-left: auto;
                }
                .opt-desc {
                    font-size: 12.8px;
                    line-height: 1.55;
                    color: var(--text-secondary);
                    margin-top: 8px;
                    padding-left: 30px;
                }
                .custom-fields {
                    margin-top: 14px;
                    padding-top: 14px;
                    padding-left: 30px;
                    border-top: 1px solid var(--border);
                    display: none;
                    flex-direction: column;
                    gap: 14px;
                }
                .option.selected .custom-fields {
                    display: flex;
                }
                .field-row {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 12px;
                }
                .field-label {
                    font-size: 13px;
                    color: var(--text);
                    font-weight: 500;
                }
                .field-hint {
                    font-size: 11.5px;
                    color: var(--text-muted);
                    margin-top: 2px;
                }
                .port-input {
                    width: 124px;
                    height: 36px;
                    padding: 0 12px;
                    border-radius: 9px;
                    border: 1px solid var(--border-strong);
                    background: #0d0e12;
                    color: var(--text);
                    font-size: 14px;
                    font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace;
                    text-align: right;
                    outline: none;
                    transition: border-color 0.15s ease, box-shadow 0.15s ease;
                }
                .port-input:focus {
                    border-color: var(--accent);
                    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.18);
                }
                .port-input.invalid {
                    border-color: #ef4444;
                    box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.18);
                }
                .port-err {
                    display: none;
                    font-size: 12px;
                    color: var(--danger-text);
                    margin-top: -8px;
                }
                .port-err.show { display: block; }
                .toggle-row {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 14px;
                    cursor: pointer;
                }
                .toggle-text {
                    font-size: 13px;
                    line-height: 1.45;
                    color: var(--text-secondary);
                }
                .switch {
                    position: relative;
                    width: 40px;
                    height: 22px;
                    flex-shrink: 0;
                }
                .switch input {
                    opacity: 0;
                    width: 0;
                    height: 0;
                }
                .slider {
                    position: absolute;
                    inset: 0;
                    background: rgba(255, 255, 255, 0.14);
                    border-radius: 999px;
                    transition: background 0.18s ease;
                }
                .slider::before {
                    content: '';
                    position: absolute;
                    width: 16px;
                    height: 16px;
                    left: 3px;
                    top: 3px;
                    border-radius: 50%;
                    background: #fff;
                    transition: transform 0.18s ease;
                }
                .switch input:checked + .slider {
                    background: var(--accent);
                }
                .switch input:checked + .slider::before {
                    transform: translateX(18px);
                }
                .actions {
                    margin-top: 24px;
                    display: flex;
                    flex-direction: column;
                    gap: 10px;
                }
                .btn {
                    width: 100%;
                    height: 44px;
                    border: none;
                    border-radius: 12px;
                    background: var(--accent);
                    color: #fff;
                    font-size: 14.5px;
                    font-weight: 600;
                    cursor: pointer;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    gap: 8px;
                    transition: background 0.15s ease, transform 0.08s ease, opacity 0.15s ease;
                }
                .btn:hover:not(:disabled) { background: var(--accent-hover); }
                .btn:active:not(:disabled) { transform: scale(0.992); }
                .btn:disabled { opacity: 0.6; cursor: not-allowed; }
                .footer-hint {
                    text-align: center;
                    font-size: 12px;
                    color: var(--text-muted);
                }
                </style></head><body>
                <main class=""card"">
                    <div class=""header"">
                        <div class=""logo"">" + LogoSvg() + @"</div>
                        <div>
                            <h1 class=""title"">" + Html(title) + @"</h1>
                            <div class=""subtitle"">" + Html(subtitle) + @"</div>
                        </div>
                    </div>
                    " + errorHtml + @"
                    <div class=""options"" role=""radiogroup"">
                        <div class=""option" + (isAuto ? " selected" : "") + @""" id=""optAuto"" tabindex=""0"" role=""radio"" aria-checked=""" + (isAuto ? "true" : "false") + @""">
                            <div class=""opt-head"">
                                <div class=""radio""></div>
                                <div class=""opt-title-row"">
                                    <span class=""opt-title"">" + Html(autoTitle) + @"</span>
                                    <span class=""badge"">" + Html(recommended) + @"</span>
                                    <span class=""pill"">:3001</span>
                                </div>
                            </div>
                            <div class=""opt-desc"">" + Html(autoDesc) + @"</div>
                        </div>
                        <div class=""option" + (!isAuto ? " selected" : "") + @""" id=""optCustom"" tabindex=""0"" role=""radio"" aria-checked=""" + (!isAuto ? "true" : "false") + @""">
                            <div class=""opt-head"">
                                <div class=""radio""></div>
                                <div class=""opt-title-row"">
                                    <span class=""opt-title"">" + Html(customTitle) + @"</span>
                                </div>
                            </div>
                            <div class=""opt-desc"">" + Html(customDesc) + @"</div>
                            <div class=""custom-fields"" id=""customFields"">
                                <div class=""field-row"">
                                    <div>
                                        <div class=""field-label"">" + Html(portLabel) + @"</div>
                                        <div class=""field-hint"">" + Html(portHint) + @"</div>
                                    </div>
                                    <input class=""port-input"" id=""portInput"" type=""number"" min=""1"" max=""65535"" value=""" + Port.ToString(CultureInfo.InvariantCulture) + @""" />
                                </div>
                                <div class=""port-err"" id=""portErr"">" + Html(portErrText) + @"</div>
                                <label class=""toggle-row"" id=""startRow"">
                                    <span class=""toggle-text"">" + Html(startLabel) + @"</span>
                                    <span class=""switch"">
                                        <input type=""checkbox"" id=""startCheck""" + (startWhenOffline ? " checked" : "") + @" />
                                        <span class=""slider""></span>
                                    </span>
                                </label>
                            </div>
                        </div>
                    </div>
                    <div class=""actions"">
                        <button class=""btn"" id=""submitBtn"" type=""button"">
                            <span id=""btnLabel"">" + Html(continueText) + @"</span>
                            <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"">
                                <path d=""M5 12H19M13 6L19 12L13 18"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""/>
                            </svg>
                        </button>
                        <div class=""footer-hint"">" + Html(footerHint) + @"</div>
                    </div>
                </main>
                <script>
                    var mode = '" + (isAuto ? "auto" : "custom") + @"';
                    var submitting = false;
                    var optAuto = document.getElementById('optAuto');
                    var optCustom = document.getElementById('optCustom');
                    var portInput = document.getElementById('portInput');
                    var portErr = document.getElementById('portErr');
                    var startCheck = document.getElementById('startCheck');
                    var submitBtn = document.getElementById('submitBtn');
                    var btnLabel = document.getElementById('btnLabel');

                    function selectMode(next) {
                        mode = next;
                        var a = mode === 'auto';
                        optAuto.classList.toggle('selected', a);
                        optCustom.classList.toggle('selected', !a);
                        optAuto.setAttribute('aria-checked', String(a));
                        optCustom.setAttribute('aria-checked', String(!a));
                        if (a) {
                            portInput.classList.remove('invalid');
                            portErr.classList.remove('show');
                        }
                    }

                    function validatePort() {
                        var raw = (portInput.value || '').trim();
                        if (!/^\d+$/.test(raw)) return null;
                        var n = Number(raw);
                        if (!Number.isInteger(n) || n < 1 || n > 65535) return null;
                        return n;
                    }

                    optAuto.addEventListener('click', function() { selectMode('auto'); });
                    optCustom.addEventListener('click', function(e) {
                        selectMode('custom');
                        if (e.target === optCustom) portInput.focus();
                    });
                    optAuto.addEventListener('keydown', function(e) {
                        if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); selectMode('auto'); }
                    });
                    optCustom.addEventListener('keydown', function(e) {
                        if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); selectMode('custom'); }
                    });
                    document.getElementById('customFields').addEventListener('click', function(e) {
                        e.stopPropagation();
                    });
                    portInput.addEventListener('input', function() {
                        var valid = validatePort() !== null;
                        portInput.classList.toggle('invalid', !valid);
                        portErr.classList.toggle('show', !valid);
                    });

                    function submit() {
                        if (submitting) return;
                        var resolvedMode = mode === 'auto' ? 'auto' : (startCheck.checked ? 'existing' : 'connect');
                        var resolvedPort = 3001;
                        if (mode !== 'auto') {
                            var p = validatePort();
                            if (p === null) {
                                portInput.classList.add('invalid');
                                portErr.classList.add('show');
                                portInput.focus();
                                return;
                            }
                            resolvedPort = p;
                        }
                        submitting = true;
                        submitBtn.disabled = true;
                        btnLabel.textContent = '" + JsEscape(startingText) + @"';
                        window.chrome.webview.postMessage('submit|' + resolvedMode + '|' + resolvedPort);
                    }

                    submitBtn.addEventListener('click', submit);
                    document.addEventListener('keydown', function(e) {
                        if (e.key === 'Enter' && document.activeElement !== optAuto && document.activeElement !== optCustom) {
                            e.preventDefault();
                            submit();
                        }
                    });
                </script></body></html>";
        }

        async Task StartFromWindowAsync(bool configure)
        {
            string initialText = T("正在启动 CloudCLI…", "正在啟動 CloudCLI…", "Starting CloudCLI…");
            ShowLoading(initialText);
            status.Text = initialText;
            await BootAsync(configure);
            if (!connected && !exiting) ShowSetup(status.Text);
        }

        enum PortState { Available, CloudCli, Occupied, Unreachable }
        static string AppDir;
        static string LogPath;

        WebView2 web;
        Label status;

        static class TrayWindowChrome
        {
            [DllImport("dwmapi.dll")]
            static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

            [DllImport("ntdll.dll")]
            static extern int RtlGetVersion(ref OsVersionInfo version);

            [StructLayout(LayoutKind.Sequential)]
            struct OsVersionInfo
            {
                public int Size;
                public int MajorVersion;
                public int MinorVersion;
                public int BuildNumber;
                public int PlatformId;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                public string ServicePack;
            }

            const int DwmwaWindowCornerPreference = 33;
            const int DwmwaBorderColor = 34;
            const int DwmwcpRound = 2;
            const int FirstWindows11Build = 22000;

            public const int WsThickFrame = 0x00040000;
            const int WmNcCalcSize = 0x0083;
            const int WmNcHitTest = 0x0084;
            const int WmGetMinMaxInfo = 0x0024;
            const int HtClient = 1;
            const int FirstResizeHitTest = 10;
            const int LastResizeHitTest = 18;
            const int MinTrackSizeOffset = 24;

            public static readonly Color BorderColor = Color.FromArgb(218, 222, 228);
            public static readonly bool SupportsModernChrome = DetectModernChrome();

            static bool DetectModernChrome()
            {
                try
                {
                    var v = new OsVersionInfo { Size = Marshal.SizeOf(typeof(OsVersionInfo)) };
                    if (RtlGetVersion(ref v) != 0) return false;
                    if (v.MajorVersion > 10) return true;
                    return v.MajorVersion == 10 && v.BuildNumber >= FirstWindows11Build;
                }
                catch { return false; }
            }

            public static bool TryApplyModernChrome(IntPtr handle)
            {
                if (!SupportsModernChrome || SystemInformation.HighContrast) return false;
                int pref = DwmwcpRound;
                try
                {
                    if (DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref pref, sizeof(int)) != 0)
                        return false;
                    int colorRef = BorderColor.R | (BorderColor.G << 8) | (BorderColor.B << 16);
                    DwmSetWindowAttribute(handle, DwmwaBorderColor, ref colorRef, sizeof(int));
                    return true;
                }
                catch { return false; }
            }

            public static bool TrySuppressFrame(ref Message m)
            {
                if (!SupportsModernChrome || SystemInformation.HighContrast) return false;
                if (m.Msg != WmNcCalcSize || m.WParam == IntPtr.Zero) return false;
                m.Result = IntPtr.Zero;
                return true;
            }

            public static void NormalizeFrameResults(ref Message m)
            {
                if (!SupportsModernChrome || SystemInformation.HighContrast) return;
                if (m.Msg == WmGetMinMaxInfo)
                {
                    Marshal.WriteInt32(m.LParam, MinTrackSizeOffset, 1);
                    Marshal.WriteInt32(m.LParam, MinTrackSizeOffset + 4, 1);
                    return;
                }
                if (m.Msg == WmNcHitTest)
                {
                    int hit = m.Result.ToInt32();
                    if (hit >= FirstResizeHitTest && hit <= LastResizeHitTest)
                        m.Result = new IntPtr(HtClient);
                }
            }
        }

        sealed class TrayMenuPopup : ContextMenuStrip
        {
            public bool NativeChrome { get; private set; }

            public TrayMenuPopup()
            {
                DropShadowEnabled = !TrayWindowChrome.SupportsModernChrome || SystemInformation.HighContrast;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    if (TrayWindowChrome.SupportsModernChrome && !SystemInformation.HighContrast)
                        cp.Style |= TrayWindowChrome.WsThickFrame;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                NativeChrome = TrayWindowChrome.TryApplyModernChrome(Handle);
            }

            protected override void WndProc(ref Message m)
            {
                if (TrayWindowChrome.TrySuppressFrame(ref m)) return;
                base.WndProc(ref m);
                TrayWindowChrome.NormalizeFrameResults(ref m);
            }
        }

        sealed class TrayMenuItem : ToolStripMenuItem
        {
            public int TextLeftInset { get; set; }
            public int TextRightInset { get; set; }

            public TrayMenuItem(string text, EventHandler onClick) : base(text, null, onClick) { }
        }

        sealed class TrayMenuColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Color.White; } }
            public override Color ImageMarginGradientBegin { get { return Color.White; } }
            public override Color ImageMarginGradientMiddle { get { return Color.White; } }
            public override Color ImageMarginGradientEnd { get { return Color.White; } }
            public override Color MenuBorder { get { return Color.Transparent; } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(243, 244, 246); } }
        }

        sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            public TrayMenuRenderer() : base(new TrayMenuColorTable()) { RoundedEdges = false; }

            static bool HasNativeChrome(ToolStrip strip)
            {
                var popup = strip as TrayMenuPopup;
                return popup != null && popup.NativeChrome;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    base.OnRenderToolStripBackground(e);
                    return;
                }
                if (HasNativeChrome(e.ToolStrip))
                {
                    e.Graphics.Clear(Color.White);
                    return;
                }
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.White))
                using (var path = RoundedRectangle(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 12))
                    e.Graphics.FillPath(brush, path);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    base.OnRenderToolStripBorder(e);
                    return;
                }
                if (HasNativeChrome(e.ToolStrip)) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(TrayWindowChrome.BorderColor))
                using (var path = RoundedRectangle(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 12))
                    e.Graphics.DrawPath(pen, path);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    base.OnRenderSeparator(e);
                    return;
                }
                int inset = ScalePixels(10, e.Graphics.DpiX);
                int y = e.Item.Height / 2;
                using (var pen = new Pen(Color.FromArgb(234, 236, 239)))
                    e.Graphics.DrawLine(pen, inset, y, Math.Max(inset, e.Item.Width - inset), y);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    base.OnRenderMenuItemBackground(e);
                    return;
                }
                if (!e.Item.Selected && !e.Item.Pressed) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color fill = e.Item.Pressed ? Color.FromArgb(230, 232, 236) : Color.FromArgb(242, 243, 245);
                int padX = ScalePixels(5, e.Graphics.DpiX);
                int padY = ScalePixels(2, e.Graphics.DpiY);
                var rect = new Rectangle(padX, padY, Math.Max(1, e.Item.Width - padX * 2), Math.Max(1, e.Item.Height - padY * 2));
                using (var brush = new SolidBrush(fill))
                using (var path = RoundedRectangle(rect, ScalePixels(8, e.Graphics.DpiX)))
                    e.Graphics.FillPath(brush, path);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    base.OnRenderItemText(e);
                    return;
                }
                e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Color textColor = e.Item.Enabled ? Color.FromArgb(31, 35, 40) : Color.FromArgb(156, 163, 175);
                var item = e.Item as TrayMenuItem;
                Rectangle rect = e.TextRectangle;
                if (item != null)
                {
                    rect.X = item.TextLeftInset;
                    rect.Width = Math.Max(1, e.Item.Width - item.TextLeftInset - item.TextRightInset);
                }
                rect.Y = 0;
                rect.Height = e.Item.Height;
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                if ((e.TextFormat & TextFormatFlags.RightToLeft) == TextFormatFlags.RightToLeft)
                    flags |= TextFormatFlags.RightToLeft;
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, rect, textColor, flags);
            }
        }

        TrayMenuItem CreateTrayMenuItem(string text, Font font, int menuWidth, EventHandler onClick)
        {
            int inset = ScalePixels(14, DeviceDpi);
            int textHeight = TextRenderer.MeasureText(text, font).Height;
            int itemHeight = Math.Max(ScalePixels(32, DeviceDpi), textHeight + ScalePixels(12, DeviceDpi));
            return new TrayMenuItem(text, onClick)
            {
                AutoSize = false,
                Margin = new Padding(0),
                Size = new Size(menuWidth, itemHeight),
                TextAlign = ContentAlignment.MiddleLeft,
                TextLeftInset = inset,
                TextRightInset = inset
            };
        }

        int MeasureTrayMenuWidth(Font font, params string[] labels)
        {
            int maxText = 0;
            foreach (string label in labels)
                maxText = Math.Max(maxText, TextRenderer.MeasureText(label, font).Width);
            return Math.Max(ScalePixels(140, DeviceDpi), maxText + ScalePixels(52, DeviceDpi));
        }

        void ApplyTrayMenuRegion()
        {
            if (trayMenu == null) return;
            var popup = trayMenu as TrayMenuPopup;
            if ((popup != null && popup.NativeChrome) || SystemInformation.HighContrast)
            {
                if (trayMenu.Region != null)
                {
                    Region old = trayMenu.Region;
                    trayMenu.Region = null;
                    old.Dispose();
                }
                return;
            }
            if (trayMenu.Width < 2 || trayMenu.Height < 2) return;
            Region previous = trayMenu.Region;
            using (var path = RoundedRectangle(new Rectangle(0, 0, trayMenu.Width, trayMenu.Height), 12))
                trayMenu.Region = new Region(path);
            if (previous != null) previous.Dispose();
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
                if (e.KeyCode == Keys.F5 && web != null && web.CoreWebView2 != null && !showingInternalPage)
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
            string menuFontName = Lang == 1 ? "Microsoft YaHei UI" : Lang == 2 ? "Microsoft JhengHei UI" : "Segoe UI";
            var menuFont = new Font(menuFontName, 9f, FontStyle.Regular, GraphicsUnit.Point);
            string openLabel = T("打开窗口", "開啟視窗", "Open window");
            string exitLabel = T("退出", "結束", "Exit");
            int menuWidth = MeasureTrayMenuWidth(menuFont, openLabel, exitLabel);
            int menuPadY = ScalePixels(4, DeviceDpi);
            int sepMarginY = ScalePixels(2, DeviceDpi);

            trayMenu = new TrayMenuPopup
            {
                AutoSize = true,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(31, 35, 40),
                Font = menuFont,
                Padding = new Padding(0, menuPadY, 0, menuPadY),
                ShowCheckMargin = false,
                ShowImageMargin = false,
                Renderer = new TrayMenuRenderer(),
                MinimumSize = new Size(menuWidth, 0)
            };
            trayMenu.Items.Add(CreateTrayMenuItem(openLabel, menuFont, menuWidth, delegate { OpenWindow(); }));
            trayMenu.Items.Add(new ToolStripSeparator { AutoSize = false, Size = new Size(menuWidth, ScalePixels(5, DeviceDpi)), Margin = new Padding(0, sepMarginY, 0, sepMarginY) });
            trayMenu.Items.Add(CreateTrayMenuItem(exitLabel, menuFont, menuWidth, delegate { ExitLauncher(); }));
            trayMenu.Opening += delegate { ApplyTrayMenuRegion(); };
            trayMenu.SizeChanged += delegate { ApplyTrayMenuRegion(); };
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
                bool ready = await EnsureWebViewAsync();
                if (exiting || !ready) return;
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
            if (!await EnsureWebViewAsync() || exiting) return;
            showingInternalPage = false;
            web.CoreWebView2.Navigate(Url);
            Log("navigated");
            status.Visible = false;
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
                Action apply = delegate
                {
                    status.Text = text;
                    if (webReady && showingInternalPage && web != null && web.CoreWebView2 != null)
                    {
                        web.CoreWebView2.ExecuteScriptAsync("window.__setStatus && window.__setStatus('" + JsEscape(text) + "');");
                    }
                    else
                    {
                        status.Visible = true;
                    }
                };
                if (InvokeRequired) BeginInvoke(apply);
                else apply();
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