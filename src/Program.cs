using System;
using System.ComponentModel;
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
        string setupDistro;
        string SetupFile { get { return Path.Combine(AppDir, "data", "setup.txt"); } }

        void LoadSetup()
        {
            if (!File.Exists(SetupFile)) return;
            var lines = File.ReadAllLines(SetupFile);
            int port;
            if ((lines.Length != 2 && lines.Length != 3) || !int.TryParse(lines[1], out port) || port < 1 || port > 65535) return;
            if (lines[0] != "auto" && lines[0] != "existing" && lines[0] != "connect") return;
            string distro = null;
            if (lines.Length == 3)
            {
                string d = lines[2].Trim();
                if (d.Length > 0)
                {
                    if (!IsValidDistroName(d)) return;
                    if (lines[0] == "existing") distro = d;
                }
            }
            setupMode = lines[0];
            Port = port;
            setupDistro = distro;
        }

        void SaveSetup()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SetupFile));
            string temp = SetupFile + ".tmp";
            string[] payload = (setupMode == "existing" && !string.IsNullOrEmpty(setupDistro))
                ? new[] { setupMode, Port.ToString(CultureInfo.InvariantCulture), setupDistro }
                : new[] { setupMode, Port.ToString(CultureInfo.InvariantCulture) };
            File.WriteAllLines(temp, payload);
            if (File.Exists(SetupFile)) File.Replace(temp, SetupFile, null);
            else File.Move(temp, SetupFile);
        }

        bool webReady;
        bool showingInternalPage;
        bool showingProgressPage;
        bool setupSubmitting;
        readonly string[] stepStates = new string[4];
        readonly string[] stepDetails = new string[4];
        readonly int[] stepPercents = new[] { -1, -1, -1, -1 };
        sealed class WslUserChoice { public bool Skip; public string Username; public string Password; }
        TaskCompletionSource<WslUserChoice> wslUserTcs;

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
                    if (showingInternalPage && showingProgressPage)
                    {
                        SyncProgressToWeb();
                    }
                    else if (!showingInternalPage)
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
            if (exiting || connected || !showingInternalPage) return;
            string message;
            try { message = ev.TryGetWebMessageAsString(); }
            catch { return; }
            if (string.IsNullOrEmpty(message)) return;

            var pendingUser = wslUserTcs;
            if (pendingUser != null)
            {
                if (message == "wsluser-skip")
                {
                    wslUserTcs = null;
                    pendingUser.TrySetResult(new WslUserChoice { Skip = true });
                    return;
                }
                if (message.StartsWith("wsluser|"))
                {
                    string rest = message.Substring("wsluser|".Length);
                    int sep = rest.IndexOf('|');
                    if (sep <= 0) return;
                    string user = rest.Substring(0, sep);
                    string pass = rest.Substring(sep + 1);
                    if (!Regex.IsMatch(user, "^[a-z_][a-z0-9_-]{0,31}$") || user == "root") return;
                    if (string.IsNullOrEmpty(pass) || pass.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return;
                    wslUserTcs = null;
                    pendingUser.TrySetResult(new WslUserChoice { Username = user, Password = pass });
                    return;
                }
            }

            if (setupSubmitting) return;

            string[] parts = message.Split('|');
            if ((parts.Length != 3 && parts.Length != 4) || parts[0] != "submit") return;
            string mode = parts[1];
            int chosenPort;
            if (mode != "auto" && mode != "existing" && mode != "connect") return;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out chosenPort) || chosenPort < 1 || chosenPort > 65535) return;
            string chosenDistro = parts.Length == 4 ? parts[3].Trim() : "";
            if (chosenDistro.Length > 0 && !IsValidDistroName(chosenDistro)) return;

            setupSubmitting = true;
            setupMode = mode;
            setupDistro = (mode == "existing" && chosenDistro.Length > 0) ? chosenDistro : null;
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

        async Task ShowSetupAsync(string error)
        {
            var distros = await Task.Run(delegate { return ListDistros(5000); });
            if (exiting) return;
            ShowSetup(error, distros);
        }

        void ShowSetup(string error, System.Collections.Generic.IList<string> distros = null)
        {
            if (!webReady || web == null || web.CoreWebView2 == null)
            {
                SetStatus(error ?? T("缺少 WebView2 Runtime，无法显示设置界面。", "缺少 WebView2 Runtime，無法顯示設定介面。", "WebView2 Runtime is required to display setup."));
                return;
            }
            showingInternalPage = true;
            showingProgressPage = false;
            status.Visible = false;
            web.Visible = true;
            web.BringToFront();
            web.CoreWebView2.NavigateToString(BuildSetupHtml(error, distros));
            web.Focus();
        }

        void ResetProgress()
        {
            for (int i = 0; i < stepStates.Length; i++)
            {
                stepStates[i] = "pending";
                stepDetails[i] = "";
                stepPercents[i] = -1;
            }
        }

        void ShowProgress(string initialDetail)
        {
            if (string.IsNullOrEmpty(stepStates[0]) || stepStates[0] == "pending")
            {
                stepStates[0] = "active";
                stepDetails[0] = initialDetail ?? "";
            }
            if (!webReady || web == null || web.CoreWebView2 == null)
            {
                SetStatus(initialDetail);
                return;
            }
            showingInternalPage = true;
            showingProgressPage = true;
            status.Visible = false;
            web.Visible = true;
            web.BringToFront();
            web.CoreWebView2.NavigateToString(BuildProgressHtml());
        }

        void ShowWslUserSetup(string distro, string error)
        {
            if (!webReady || web == null || web.CoreWebView2 == null) return;
            showingInternalPage = true;
            showingProgressPage = false;
            status.Visible = false;
            web.Visible = true;
            web.BringToFront();
            web.CoreWebView2.NavigateToString(BuildWslUserHtml(distro, error));
            web.Focus();
        }

        string BuildWslUserHtml(string distro, string error)
        {
            string title = T("创建 WSL Linux 用户", "建立 WSL Linux 使用者", "Create WSL Linux user");
            string subtitle = string.Format(T(
                "发行版 {0} 已安装完成，目前仅有 root 账户。请设置一个日常使用的 Linux 用户名和密码（之后可直接用它管理代码与登录工具）。",
                "發行版 {0} 已安裝完成，目前僅有 root 帳戶。請設定一個日常使用的 Linux 使用者名稱與密碼。",
                "Distribution {0} is installed and currently only has root. Create a standard Linux user account to run CloudCLI and your coding CLIs."), distro);
            string userLabel = T("Linux 用户名", "Linux 使用者名稱", "Linux username");
            string userHint = T("小写字母或下划线开头，支持小写字母、数字、- 和 _", "小寫字母或底線開頭，支援小寫字母、數字、- 與 _", "Start with a-z or _; lowercase letters, digits, - and _");
            string userErrText = T("请输入有效的 Linux 用户名（不能为 root）", "請輸入有效的 Linux 使用者名稱（不可為 root）", "Enter a valid Linux username (cannot be root)");
            string passLabel = T("密码", "密碼", "Password");
            string passConfirmLabel = T("确认密码", "確認密碼", "Confirm password");
            string passErrText = T("密码不能为空", "密碼不可為空", "Password is required");
            string mismatchText = T("两次输入的密码不一致", "兩次輸入的密碼不一致", "Passwords do not match");
            string submitText = T("创建用户并继续", "建立使用者並繼續", "Create user & continue");
            string creatingText = T("正在创建…", "正在建立…", "Creating…");
            string skipText = T("跳过，暂用 root 继续", "略過，暫以 root 繼續", "Skip (continue as root)");

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
                    margin: auto;
                    width: 100%;
                    max-width: 480px;
                    background: var(--surface);
                    border: 1px solid var(--border);
                    border-radius: 20px;
                    padding: 30px;
                    box-shadow: 0 24px 64px rgba(0, 0, 0, 0.55), 0 1px 0 rgba(255, 255, 255, 0.04) inset;
                }
                .header {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                    margin-bottom: 18px;
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
                .title { font-size: 19px; font-weight: 600; color: var(--text); letter-spacing: -0.01em; }
                .subtitle { font-size: 13px; color: var(--text-secondary); line-height: 1.5; margin-bottom: 20px; }
                .alert {
                    display: flex;
                    align-items: flex-start;
                    gap: 10px;
                    background: var(--danger-bg);
                    border: 1px solid var(--danger-border);
                    border-radius: 12px;
                    padding: 12px 14px;
                    margin-bottom: 18px;
                }
                .alert-icon { flex-shrink: 0; margin-top: 1px; }
                .alert-text {
                    font-size: 13px;
                    line-height: 1.55;
                    color: var(--danger-text);
                    user-select: text;
                    word-break: break-word;
                }
                .fields { display: flex; flex-direction: column; gap: 14px; }
                .field { display: flex; flex-direction: column; gap: 6px; }
                .label { font-size: 13px; font-weight: 500; color: var(--text); }
                .hint { font-size: 11.5px; color: var(--text-muted); }
                .input {
                    width: 100%;
                    height: 40px;
                    padding: 0 12px;
                    border-radius: 10px;
                    border: 1px solid var(--border-strong);
                    background: #0d0e12;
                    color: var(--text);
                    font-size: 14px;
                    outline: none;
                    transition: border-color 0.15s ease, box-shadow 0.15s ease;
                }
                .input:focus {
                    border-color: var(--accent);
                    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.18);
                }
                .input.invalid {
                    border-color: #ef4444;
                    box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.18);
                }
                .err { display: none; font-size: 12px; color: var(--danger-text); }
                .err.show { display: block; }
                .actions { margin-top: 22px; display: flex; flex-direction: column; gap: 10px; }
                .btn {
                    width: 100%;
                    height: 42px;
                    border: none;
                    border-radius: 12px;
                    background: var(--accent);
                    color: #fff;
                    font-size: 14px;
                    font-weight: 600;
                    cursor: pointer;
                    transition: background 0.15s ease, opacity 0.15s ease;
                }
                .btn:hover:not(:disabled) { background: var(--accent-hover); }
                .btn:disabled { opacity: 0.6; cursor: not-allowed; }
                .btn-ghost {
                    width: 100%;
                    height: 36px;
                    border: 1px solid var(--border);
                    border-radius: 10px;
                    background: transparent;
                    color: var(--text-secondary);
                    font-size: 13px;
                    cursor: pointer;
                    transition: background 0.15s ease, color 0.15s ease;
                }
                .btn-ghost:hover:not(:disabled) { background: rgba(255, 255, 255, 0.04); color: var(--text); }
                .btn-ghost:disabled { opacity: 0.5; cursor: not-allowed; }
                </style></head><body>
                <main class=""card"">
                    <div class=""header"">
                        <div class=""logo"">" + LogoSvg() + @"</div>
                        <h1 class=""title"">" + Html(title) + @"</h1>
                    </div>
                    <p class=""subtitle"">" + Html(subtitle) + @"</p>
                    " + errorHtml + @"
                    <div class=""fields"">
                        <div class=""field"">
                            <label class=""label"" for=""uInput"">" + Html(userLabel) + @"</label>
                            <input class=""input"" id=""uInput"" type=""text"" placeholder=""ubuntu"" autocomplete=""off"" spellcheck=""false"" />
                            <div class=""hint"">" + Html(userHint) + @"</div>
                            <div class=""err"" id=""uErr"">" + Html(userErrText) + @"</div>
                        </div>
                        <div class=""field"">
                            <label class=""label"" for=""pInput"">" + Html(passLabel) + @"</label>
                            <input class=""input"" id=""pInput"" type=""password"" />
                            <div class=""err"" id=""pErr"">" + Html(passErrText) + @"</div>
                        </div>
                        <div class=""field"">
                            <label class=""label"" for=""p2Input"">" + Html(passConfirmLabel) + @"</label>
                            <input class=""input"" id=""p2Input"" type=""password"" />
                            <div class=""err"" id=""p2Err"">" + Html(mismatchText) + @"</div>
                        </div>
                    </div>
                    <div class=""actions"">
                        <button class=""btn"" id=""createBtn"" type=""button"">" + Html(submitText) + @"</button>
                        <button class=""btn-ghost"" id=""skipBtn"" type=""button"">" + Html(skipText) + @"</button>
                    </div>
                </main>
                <script>
                    var uInput = document.getElementById('uInput');
                    var pInput = document.getElementById('pInput');
                    var p2Input = document.getElementById('p2Input');
                    var uErr = document.getElementById('uErr');
                    var pErr = document.getElementById('pErr');
                    var p2Err = document.getElementById('p2Err');
                    var createBtn = document.getElementById('createBtn');
                    var skipBtn = document.getElementById('skipBtn');
                    var busy = false;

                    function validUser() {
                        var u = (uInput.value || '').trim();
                        return /^[a-z_][a-z0-9_-]{0,31}$/.test(u) && u !== 'root';
                    }

                    uInput.addEventListener('input', function() {
                        var ok = validUser();
                        uInput.classList.toggle('invalid', !ok);
                        uErr.classList.toggle('show', !ok);
                    });

                    function submitUser() {
                        if (busy) return;
                        var u = (uInput.value || '').trim();
                        var p = pInput.value || '';
                        var p2 = p2Input.value || '';
                        var okU = validUser();
                        var okP = p.length > 0 && p.indexOf('\n') === -1 && p.indexOf('\r') === -1;
                        var okP2 = p === p2;
                        uInput.classList.toggle('invalid', !okU);
                        uErr.classList.toggle('show', !okU);
                        pInput.classList.toggle('invalid', !okP);
                        pErr.classList.toggle('show', !okP);
                        p2Input.classList.toggle('invalid', !okP2);
                        p2Err.classList.toggle('show', !okP2);
                        if (!okU) { uInput.focus(); return; }
                        if (!okP) { pInput.focus(); return; }
                        if (!okP2) { p2Input.focus(); return; }
                        busy = true;
                        createBtn.disabled = true;
                        skipBtn.disabled = true;
                        createBtn.textContent = '" + JsEscape(creatingText) + @"';
                        window.chrome.webview.postMessage('wsluser|' + u + '|' + p);
                    }

                    createBtn.addEventListener('click', submitUser);
                    skipBtn.addEventListener('click', function() {
                        if (busy) return;
                        busy = true;
                        createBtn.disabled = true;
                        skipBtn.disabled = true;
                        window.chrome.webview.postMessage('wsluser-skip');
                    });
                    document.addEventListener('keydown', function(e) {
                        if (e.key === 'Enter') { e.preventDefault(); submitUser(); }
                    });
                    uInput.focus();
                </script></body></html>";
        }

        void UpdateStep(int index, string state, string detail)
        {
            if (index < 0 || index >= stepStates.Length) return;
            try
            {
                Action apply = delegate
                {
                    stepStates[index] = state ?? "pending";
                    stepDetails[index] = detail ?? "";
                    if (stepStates[index] == "done" || stepStates[index] == "error" || stepStates[index] == "pending")
                        stepPercents[index] = -1;
                    if (!string.IsNullOrEmpty(detail)) status.Text = detail;
                    if (webReady && showingInternalPage && showingProgressPage && web != null && web.CoreWebView2 != null)
                    {
                        web.CoreWebView2.ExecuteScriptAsync("window.__setStep && window.__setStep("
                            + index.ToString(CultureInfo.InvariantCulture) + ",'"
                            + JsEscape(stepStates[index]) + "','" + JsEscape(stepDetails[index]) + "');");
                    }
                };
                if (InvokeRequired) BeginInvoke(apply);
                else apply();
            }
            catch { }
        }

        void UpdateStepProgress(int index, int percent)
        {
            if (index < 0 || index >= stepPercents.Length) return;
            try
            {
                Action apply = delegate
                {
                    stepPercents[index] = percent;
                    if (webReady && showingInternalPage && showingProgressPage && web != null && web.CoreWebView2 != null)
                    {
                        web.CoreWebView2.ExecuteScriptAsync("window.__setStepProgress && window.__setStepProgress("
                            + index.ToString(CultureInfo.InvariantCulture) + ","
                            + percent.ToString(CultureInfo.InvariantCulture) + ");");
                    }
                };
                if (InvokeRequired) BeginInvoke(apply);
                else apply();
            }
            catch { }
        }

        void SyncProgressToWeb()
        {
            if (!webReady || !showingInternalPage || !showingProgressPage || web == null || web.CoreWebView2 == null) return;
            var sb = new StringBuilder();
            for (int i = 0; i < stepStates.Length; i++)
            {
                sb.Append("window.__setStep && window.__setStep(")
                  .Append(i.ToString(CultureInfo.InvariantCulture)).Append(",'")
                  .Append(JsEscape(stepStates[i] ?? "pending")).Append("','")
                  .Append(JsEscape(stepDetails[i] ?? "")).Append("');");
                if (stepPercents[i] != -1)
                {
                    sb.Append("window.__setStepProgress && window.__setStepProgress(")
                      .Append(i.ToString(CultureInfo.InvariantCulture)).Append(",")
                      .Append(stepPercents[i].ToString(CultureInfo.InvariantCulture)).Append(");");
                }
            }
            web.CoreWebView2.ExecuteScriptAsync(sb.ToString());
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
                html {
                    height: 100%;
                    background: var(--bg);
                }
                body {
                    min-height: 100%;
                    background: var(--bg);
                    color: var(--text);
                    font-family: 'Segoe UI Variable', 'Segoe UI', 'Microsoft YaHei UI', system-ui, -apple-system, sans-serif;
                    -webkit-font-smoothing: antialiased;
                    user-select: none;
                    display: flex;
                    flex-direction: column;
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

        string BuildProgressHtml()
        {
            string heading = T("正在部署并启动 CloudCLI", "正在部署並啟動 CloudCLI", "Deploying and starting CloudCLI");
            string subheading = setupMode == "auto"
                ? T("自动检查并准备所需运行环境，请稍候", "自動檢查並準備所需執行環境，請稍候", "Automatically checking and preparing your environment")
                : T("正在连接并检查 WSL 服务状态", "正在連線並檢查 WSL 服務狀態", "Connecting and verifying your WSL service");
            string[] titles = new[]
            {
                T("检查并准备 WSL 环境", "檢查並準備 WSL 環境", "Check & prepare WSL"),
                T("检查并准备 Node.js 22+ 与 npm", "檢查並準備 Node.js 22+ 與 npm", "Check & prepare Node.js 22+ & npm"),
                T("检查并准备 CloudCLI", "檢查並準備 CloudCLI", "Check & prepare CloudCLI"),
                T("启动服务并连接界面", "啟動服務並連線介面", "Start service & connect")
            };

            var stepsHtml = new StringBuilder();
            for (int i = 0; i < titles.Length; i++)
            {
                string state = string.IsNullOrEmpty(stepStates[i]) ? "pending" : stepStates[i];
                string detail = stepDetails[i] ?? "";
                stepsHtml.Append("<div class=\"step ").Append(state).Append("\" id=\"step-").Append(i).Append("\">")
                    .Append("<div class=\"step-icon\" aria-hidden=\"true\">")
                    .Append("<span class=\"icon-pending\"></span>")
                    .Append("<span class=\"icon-active\"></span>")
                    .Append("<svg class=\"icon-done\" width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" fill=\"none\"><path d=\"M5 12.5L10 17.5L19 7\" stroke=\"currentColor\" stroke-width=\"2.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>")
                    .Append("<svg class=\"icon-error\" width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" fill=\"none\"><path d=\"M7 7L17 17M17 7L7 17\" stroke=\"currentColor\" stroke-width=\"2.4\" stroke-linecap=\"round\"/></svg>")
                    .Append("</div>")
                    .Append("<div class=\"step-body\">")
                    .Append("<div class=\"step-title-row\">")
                    .Append("<div class=\"step-title\">").Append(Html(titles[i])).Append("</div>")
                    .Append("<div class=\"step-meta\" id=\"step-meta-").Append(i).Append("\"></div>")
                    .Append("</div>")
                    .Append("<div class=\"step-detail\" id=\"step-detail-").Append(i).Append("\">").Append(Html(detail)).Append("</div>")
                    .Append("<div class=\"subbar\" id=\"subbar-").Append(i).Append("\"><div class=\"subbar-fill\" id=\"subbar-fill-").Append(i).Append("\"></div></div>")
                    .Append("</div></div>");
            }

            return "<!DOCTYPE html><html lang=\"" + LangName() + "\"><head><meta charset=\"utf-8\">"
                + "<style>" + SharedCss() + @"
                .card {
                    margin: auto;
                    width: 100%;
                    max-width: 520px;
                    background: var(--surface);
                    border: 1px solid var(--border);
                    border-radius: 20px;
                    padding: 30px;
                    box-shadow: 0 24px 64px rgba(0, 0, 0, 0.55), 0 1px 0 rgba(255, 255, 255, 0.04) inset;
                }
                .header {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                    margin-bottom: 18px;
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
                .title { font-size: 19px; font-weight: 600; color: var(--text); letter-spacing: -0.01em; }
                .subtitle { font-size: 13px; color: var(--text-secondary); margin-top: 3px; }
                .bar-track {
                    height: 4px;
                    border-radius: 999px;
                    background: rgba(255, 255, 255, 0.07);
                    overflow: hidden;
                    margin-bottom: 20px;
                }
                .bar-fill {
                    height: 100%;
                    width: 10%;
                    border-radius: 999px;
                    background: linear-gradient(90deg, #3b82f6, #60a5fa);
                    transition: width 0.28s ease;
                }
                .steps {
                    display: flex;
                    flex-direction: column;
                    gap: 8px;
                }
                .step {
                    display: flex;
                    align-items: flex-start;
                    gap: 12px;
                    padding: 11px 14px;
                    border-radius: 12px;
                    border: 1px solid transparent;
                    transition: background 0.18s ease, border-color 0.18s ease, opacity 0.18s ease;
                }
                .step.pending { opacity: 0.52; }
                .step.active {
                    background: var(--surface-active);
                    border-color: rgba(59, 130, 246, 0.36);
                    opacity: 1;
                }
                .step.done { opacity: 0.92; }
                .step.error {
                    background: var(--danger-bg);
                    border-color: var(--danger-border);
                    opacity: 1;
                }
                .step-icon {
                    width: 22px;
                    height: 22px;
                    margin-top: 1px;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    flex-shrink: 0;
                }
                .icon-pending, .icon-active, .icon-done, .icon-error { display: none; }
                .step.pending .icon-pending {
                    display: block;
                    width: 10px;
                    height: 10px;
                    border-radius: 50%;
                    border: 2px solid var(--text-muted);
                }
                .step.active .icon-active {
                    display: block;
                    width: 18px;
                    height: 18px;
                    border: 2.2px solid rgba(96, 165, 250, 0.25);
                    border-top-color: #60a5fa;
                    border-radius: 50%;
                    animation: spin 0.8s linear infinite;
                }
                .step.done .icon-done {
                    display: block;
                    color: #34d399;
                }
                .step.error .icon-error {
                    display: block;
                    color: #f87171;
                }
                @keyframes spin { to { transform: rotate(360deg); } }
                .step-body { flex: 1; min-width: 0; }
                .step-title-row {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 10px;
                }
                .step-title { font-size: 13.8px; font-weight: 500; color: var(--text); }
                .step-meta {
                    font-size: 11.5px;
                    font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace;
                    font-variant-numeric: tabular-nums;
                    color: #93c5fd;
                    flex-shrink: 0;
                }
                .step-meta:empty { display: none; }
                .step-detail {
                    font-size: 12.2px;
                    color: var(--text-secondary);
                    margin-top: 3px;
                    line-height: 1.45;
                    user-select: text;
                    word-break: break-word;
                }
                .step-detail:empty { display: none; }
                .step.error .step-detail { color: var(--danger-text); }
                .subbar {
                    display: none;
                    height: 4px;
                    border-radius: 999px;
                    background: rgba(255, 255, 255, 0.08);
                    overflow: hidden;
                    margin-top: 8px;
                }
                .subbar.show { display: block; }
                .subbar-fill {
                    height: 100%;
                    width: 0%;
                    border-radius: 999px;
                    background: linear-gradient(90deg, #3b82f6, #60a5fa);
                    transition: width 0.25s ease;
                }
                .subbar-fill.indeterminate {
                    width: 38%;
                    background: linear-gradient(90deg, transparent, #3b82f6, #60a5fa, transparent);
                    animation: subslide 1.35s ease-in-out infinite;
                }
                @keyframes subslide {
                    0% { transform: translateX(-110%); }
                    100% { transform: translateX(280%); }
                }
                </style></head><body>
                <main class=""card"">
                    <div class=""header"">
                        <div class=""logo"">" + LogoSvg() + @"</div>
                        <div>
                            <h1 class=""title"">" + Html(heading) + @"</h1>
                            <div class=""subtitle"">" + Html(subheading) + @"</div>
                        </div>
                    </div>
                    <div class=""bar-track""><div class=""bar-fill"" id=""barFill""></div></div>
                    <div class=""steps"">" + stepsHtml.ToString() + @"</div>
                </main>
                <script>
                    var stepPct = [-1, -1, -1, -1];
                    var stepStartTs = {};

                    function formatElapsed(ms) {
                        var sec = Math.max(0, Math.floor(ms / 1000));
                        var m = Math.floor(sec / 60);
                        var s = sec % 60;
                        return (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
                    }

                    function renderMeta(idx) {
                        var el = document.getElementById('step-' + idx);
                        var meta = document.getElementById('step-meta-' + idx);
                        if (!el || !meta) return;
                        if (!el.classList.contains('active')) {
                            meta.textContent = '';
                            return;
                        }
                        var p = stepPct[idx];
                        var parts = [];
                        if (p >= 0 && p <= 100) parts.push(p + '%');
                        if (stepStartTs[idx]) parts.push(formatElapsed(Date.now() - stepStartTs[idx]));
                        meta.textContent = parts.join(' · ');
                    }

                    function refreshBar() {
                        var steps = document.querySelectorAll('.step');
                        var score = 0;
                        for (var i = 0; i < steps.length; i++) {
                            if (steps[i].classList.contains('done')) score += 1;
                            else if (steps[i].classList.contains('active')) {
                                var p = stepPct[i];
                                score += (p >= 0 && p <= 100) ? (0.15 + 0.8 * (p / 100)) : 0.45;
                            }
                        }
                        var pct = Math.max(8, Math.min(100, Math.round((score / steps.length) * 100)));
                        var fill = document.getElementById('barFill');
                        if (fill) fill.style.width = pct + '%';
                    }

                    window.__setStep = function(idx, state, detail) {
                        var el = document.getElementById('step-' + idx);
                        var d = document.getElementById('step-detail-' + idx);
                        if (!el) return;
                        var nextState = state || 'pending';
                        el.className = 'step ' + nextState;
                        if (d) d.textContent = detail || '';
                        if (nextState !== 'active') {
                            stepPct[idx] = -1;
                            delete stepStartTs[idx];
                            var bar = document.getElementById('subbar-' + idx);
                            if (bar) bar.classList.remove('show');
                        } else if (!stepStartTs[idx]) {
                            stepStartTs[idx] = Date.now();
                        }
                        renderMeta(idx);
                        refreshBar();
                    };

                    window.__setStepProgress = function(idx, pct) {
                        var bar = document.getElementById('subbar-' + idx);
                        var fill = document.getElementById('subbar-fill-' + idx);
                        if (!bar || !fill) return;
                        stepPct[idx] = pct;
                        if (pct === -1) {
                            bar.classList.remove('show');
                            fill.classList.remove('indeterminate');
                            fill.style.width = '0%';
                        } else if (pct === -2) {
                            if (!stepStartTs[idx]) stepStartTs[idx] = Date.now();
                            bar.classList.add('show');
                            fill.classList.add('indeterminate');
                            fill.style.width = '';
                        } else {
                            var clamped = Math.max(0, Math.min(100, pct));
                            if (!stepStartTs[idx]) stepStartTs[idx] = Date.now();
                            bar.classList.add('show');
                            fill.classList.remove('indeterminate');
                            fill.style.width = clamped + '%';
                        }
                        renderMeta(idx);
                        refreshBar();
                    };

                    window.__setStatus = function(t) {
                        var active = document.querySelector('.step.active .step-detail');
                        if (active) active.textContent = t || '';
                    };

                    setInterval(function() {
                        for (var i = 0; i < 4; i++) renderMeta(i);
                    }, 1000);
                    refreshBar();
                </script></body></html>";
        }

        string BuildSetupHtml(string error, System.Collections.Generic.IList<string> distros = null)
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
                "全自动检查并准备运行环境：缺少 WSL 时引导安装，缺少 Node.js 22+ 或 CloudCLI 时自动安装到启动器独立目录。",
                "全自動檢查並準備執行環境：缺少 WSL 時引導安裝，缺少 Node.js 22+ 或 CloudCLI 時自動安裝到啟動器獨立目錄。",
                "Automatically checks and prepares WSL (guiding installation if missing), an isolated Node.js 22+ runtime if needed, and CloudCLI in the launcher's folder.");
            string customTitle = T("自定义或使用已有部署", "自訂設定或使用現有部署", "Customize or use an existing setup");
            string customDesc = T(
                "指定自定义本机端口，或直接连接你自己管理的 CloudCLI 服务。",
                "指定自訂本機連接埠，或直接連線至你自行管理的 CloudCLI 服務。",
                "Use a custom local port or connect directly to a service you manage yourself.");
            string portLabel = T("本机端口", "本機連接埠", "Local port");
            string portHint = T("范围 1 – 65535", "範圍 1 – 65535", "Range 1 – 65535");
            string portErrText = T("请输入 1 到 65535 之间的有效端口号", "請輸入 1 到 65535 之間的有效連接埠號碼", "Enter a valid port number between 1 and 65535");
            string distroLabel = T("WSL 发行版", "WSL 發行版", "WSL distribution");
            string distroHint = T("检测到多个发行版，可指定运行环境", "偵測到多個發行版，可指定執行環境", "Multiple distributions found; choose one");
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

            string distroRowHtml = "";
            if (distros != null && (distros.Count >= 2 || (!string.IsNullOrEmpty(setupDistro) && distros.Count >= 1)))
            {
                var opts = new StringBuilder();
                string defaultText = string.Format(T("默认（{0}）", "預設（{0}）", "Default ({0})"), distros[0]);
                opts.Append("<option value=\"\">").Append(Html(defaultText)).Append("</option>");
                foreach (string d in distros)
                {
                    bool sel = !string.IsNullOrEmpty(setupDistro) && string.Equals(d, setupDistro, StringComparison.OrdinalIgnoreCase);
                    opts.Append("<option value=\"").Append(Html(d)).Append("\"")
                        .Append(sel ? " selected" : "").Append(">")
                        .Append(Html(d)).Append("</option>");
                }
                distroRowHtml = @"
                                <div class=""field-row"" id=""distroRow"">
                                    <div>
                                        <div class=""field-label"">" + Html(distroLabel) + @"</div>
                                        <div class=""field-hint"">" + Html(distroHint) + @"</div>
                                    </div>
                                    <select class=""distro-select"" id=""distroSelect"">" + opts.ToString() + @"</select>
                                </div>";
            }

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
                    margin: auto;
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
                .field-row.disabled { opacity: 0.45; }
                .distro-select {
                    min-width: 150px;
                    max-width: 210px;
                    height: 36px;
                    padding: 0 10px;
                    border-radius: 9px;
                    border: 1px solid var(--border-strong);
                    background: #0d0e12;
                    color: var(--text);
                    font-size: 13px;
                    outline: none;
                    transition: border-color 0.15s ease, box-shadow 0.15s ease;
                }
                .distro-select:focus {
                    border-color: var(--accent);
                    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.18);
                }
                .distro-select option {
                    background: #15161a;
                    color: #f3f4f6;
                }
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
                                <div class=""port-err"" id=""portErr"">" + Html(portErrText) + @"</div>" + distroRowHtml + @"
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
                    var distroSelect = document.getElementById('distroSelect');
                    var distroRow = document.getElementById('distroRow');
                    var submitBtn = document.getElementById('submitBtn');
                    var btnLabel = document.getElementById('btnLabel');

                    function syncDistroState() {
                        if (!distroSelect || !distroRow) return;
                        var en = !!startCheck.checked;
                        distroSelect.disabled = !en;
                        distroRow.classList.toggle('disabled', !en);
                    }

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
                    startCheck.addEventListener('change', syncDistroState);
                    syncDistroState();

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
                        var resolvedDistro = (resolvedMode === 'existing' && distroSelect) ? (distroSelect.value || '') : '';
                        submitting = true;
                        submitBtn.disabled = true;
                        btnLabel.textContent = '" + JsEscape(startingText) + @"';
                        window.chrome.webview.postMessage('submit|' + resolvedMode + '|' + resolvedPort + '|' + resolvedDistro);
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
            ResetProgress();
            ShowProgress("");
            status.Text = initialText;
            await BootAsync(configure);
            if (!connected && !exiting) await ShowSetupAsync(status.Text);
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
                if (setupMode == null) await ShowSetupAsync(null);
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
            var pendingUser = wslUserTcs;
            if (pendingUser != null) pendingUser.TrySetCanceled();
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
            int activeStep = 0;
            try
            {
                PortState state = await Task.Run(delegate { return Probe(); });
                if (exiting) return;
                if (state != PortState.Available && state != PortState.CloudCli) throw PortCheckError(state);

                if (state == PortState.Available)
                {
                    if (setupMode == "connect") throw new InvalidOperationException(T(
                        "请先启动已有的 CloudCLI 服务，然后重试。", "請先啟動現有的 CloudCLI 服務，再試一次。",
                        "Start your CloudCLI service, then try again."));
                    await StartServerAsync(delegate (int step) { activeStep = step; });
                }
                else
                {
                    Log("using existing CloudCLI server");
                    string skipped = T("已跳过（复用现有服务）", "已略過（共用現有服務）", "Skipped (using running service)");
                    UpdateStep(0, "done", skipped);
                    UpdateStep(1, "done", skipped);
                    UpdateStep(2, "done", skipped);
                }
                if (configure && !exiting) SaveSetup();
            }
            catch (Exception e)
            {
                if (exiting) return;
                StopOwnedServer();
                Log("boot failed: " + e);
                UpdateStep(activeStep, "error", e.Message);
                SetStatus(T("启动失败：", "啟動失敗：", "Startup failed: ") + e.Message);
                return;
            }

            if (exiting) return;
            connected = true;
            serviceMonitor.Start();
            await ShowPage();
        }

        async Task StartServerAsync(Action<int> setActiveStep)
        {
            setActiveStep(0);
            UpdateStep(0, "active", T("正在检测已安装的 WSL 发行版…", "正在偵測已安裝的 WSL 發行版…", "Detecting WSL distribution…"));
            var distros = await Task.Run(delegate { return ListDistros(); });
            if (exiting) return;
            string distro = null;
            if (setupMode == "existing" && !string.IsNullOrEmpty(setupDistro))
            {
                foreach (string d in distros)
                {
                    if (string.Equals(d, setupDistro, StringComparison.OrdinalIgnoreCase))
                    {
                        distro = d;
                        break;
                    }
                }
                if (distro == null)
                {
                    throw new InvalidOperationException(string.Format(T(
                        "未找到你选择的 WSL 发行版“{0}”。请重新选择一个可用发行版，或确认该发行版已安装。",
                        "找不到你選擇的 WSL 發行版「{0}」。請重新選擇可用的發行版，或確認該發行版已安裝。",
                        "The selected WSL distribution \"{0}\" was not found. Choose an available distribution or check that it is installed."), setupDistro));
                }
            }
            else
            {
                distro = distros.Count > 0 ? distros[0] : null;
            }
            if (distro == null)
            {
                if (setupMode == "auto")
                {
                    distro = await InstallWslAsync();
                }
                else
                {
                    throw new InvalidOperationException(T(
                        "未找到可用的 WSL 发行版。请先安装并初始化一个发行版，然后重试，或选择“帮我配置并启动”。",
                        "找不到可用的 WSL 發行版。請先安裝發行版並完成初次設定，再試一次，或選擇「自動設定並啟動」。",
                        "No WSL distribution was found. Install a distribution and complete its first-run setup, or choose Set it up for me."));
                }
            }
            else if (setupMode == "auto")
            {
                bool ready = await Task.Run(delegate { return ProbeWslReady(distro); });
                if (exiting) return;
                if (!ready)
                {
                    throw new InvalidOperationException(string.Format(T(
                        "检测到 WSL 发行版 {0}，但尚无法执行 Linux 命令。请先在终端完成该发行版的首次初始化（创建用户名和密码），或重启电脑后再重试。",
                        "偵測到 WSL 發行版 {0}，但尚無法執行 Linux 命令。請先在終端機完成該發行版的初次設定（建立使用者名稱與密碼），或重新啟動電腦後再試。",
                        "Detected WSL distribution {0}, but it is not ready to run Linux commands yet. Complete its first-run user setup in a terminal (or reboot Windows) and try again."), distro));
                }
            }
            Log("distro: " + distro);
            string createdUser = null;
            if (setupMode == "auto")
            {
                createdUser = await EnsureWslUserAsync(distro);
                if (exiting) return;
            }
            UpdateStep(0, "done", createdUser != null
                ? string.Format(T("已就绪（{0} · 用户 {1}）", "已就緒（{0} · 使用者 {1}）", "Ready ({0} · user {1})"), distro, createdUser)
                : string.Format(T("已就绪（{0}）", "已就緒（{0}）", "Ready ({0})"), distro));

            // WSL may have started a service while the distribution was detected.
            PortState state = await Task.Run(delegate { return Probe(); });
            if (exiting || state == PortState.CloudCli) return;
            if (state != PortState.Available) throw PortCheckError(state);

            setActiveStep(1);
            UpdateStep(1, "active", T("正在检查 WSL 中的 Node.js 22+ 与 npm…", "正在檢查 WSL 中的 Node.js 22+ 與 npm…", "Checking Node.js 22+ and npm in WSL…"));
            var discovery = Stopwatch.StartNew();
            string cloudCliPath = null;
            string node = await Task.Run(delegate
            {
                string detectedCli;
                string foundNode = FindNode(distro, setupMode == "auto", out detectedCli);
                cloudCliPath = detectedCli;
                return foundNode;
            });
            if (exiting) return;
            if (node == null)
            {
                node = await InstallManagedNodeAsync(distro);
                if (exiting) return;
            }
            Log("Selected Node.js after " + discovery.ElapsedMilliseconds + " ms: " + node);
            if (!string.IsNullOrEmpty(cloudCliPath)) Log("Detected CloudCLI path: " + cloudCliPath);
            UpdateStep(1, "done", node == "node"
                ? T("已就绪（系统 PATH 中的 Node.js）", "已就緒（系統 PATH 中的 Node.js）", "Ready (Node.js on PATH)")
                : string.Format(T("已就绪（{0}）", "已就緒（{0}）", "Ready ({0})"), node));

            state = await Task.Run(delegate { return Probe(); });
            if (exiting || state == PortState.CloudCli) return;
            if (state != PortState.Available) throw PortCheckError(state);

            setActiveStep(2);
            UpdateStep(2, "active", T("正在检查 WSL 中的 CloudCLI…", "正在檢查 WSL 中的 CloudCLI…", "Checking CloudCLI in WSL…"));
            string launcherArgs = DistroArgument(distro)
                + " --exec " + QuoteArgument(node) + " ./launcher-service.cjs " + setupMode + " " + Port
                + (!string.IsNullOrEmpty(cloudCliPath) ? " " + QuoteArgument(cloudCliPath) : "");
            var processInfo = new ProcessStartInfo("wsl.exe", launcherArgs)
            {
                WorkingDirectory = AppDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            ownedServer = Process.Start(processInfo);
            bool installingCli = false;
            long phaseStartTicks = Stopwatch.GetTimestamp();
            Task progressReader = ReadServerProgressAsync(ownedServer, setActiveStep, delegate (bool isInstalling)
            {
                Volatile.Write(ref installingCli, isInstalling);
                Interlocked.Exchange(ref phaseStartTicks, Stopwatch.GetTimestamp());
            });

            var startup = Stopwatch.StartNew();
            while (startup.Elapsed < TimeSpan.FromSeconds(720))
            {
                TimeSpan phaseLimit = Volatile.Read(ref installingCli) ? TimeSpan.FromSeconds(600) : TimeSpan.FromSeconds(120);
                double phaseElapsedSec = (Stopwatch.GetTimestamp() - Interlocked.Read(ref phaseStartTicks)) / (double)Stopwatch.Frequency;
                if (phaseElapsedSec >= phaseLimit.TotalSeconds) break;

                state = await Task.Run(delegate { return Probe(); });
                if (exiting) return;

                if (ownedServer.HasExited)
                {
                    try { await progressReader; } catch { }
                    int exitCode = ownedServer.ExitCode;
                    StopOwnedServer();
                    if (state == PortState.CloudCli)
                    {
                        UpdateStep(2, "done", T("CloudCLI 已就绪", "CloudCLI 已就緒", "CloudCLI ready"));
                        return;
                    }
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
                if (state == PortState.CloudCli)
                {
                    Log("server ready after " + startup.ElapsedMilliseconds + " ms");
                    UpdateStep(2, "done", T("CloudCLI 已就绪", "CloudCLI 已就緒", "CloudCLI ready"));
                    return;
                }
                await Task.Delay(500);
                if (exiting) return;
            }
            Log("server NOT ready after " + (int)startup.Elapsed.TotalSeconds + "s");
            throw new Exception(T("等待服务启动超时。请查看日志 logs\\launcher.log 与 logs\\server.log", "等候服務啟動逾時。請查看記錄檔 logs\\launcher.log 與 logs\\server.log", "Timed out waiting for CloudCLI to start. Check logs\\launcher.log and logs\\server.log"));
        }

        async Task ReadServerProgressAsync(Process server, Action<int> setActiveStep, Action<bool> setInstallingCli)
        {
            if (server == null) return;
            try
            {
                string line;
                while ((line = await server.StandardOutput.ReadLineAsync()) != null)
                {
                    if (exiting) break;
                    line = line.Trim();
                    if (line == "PROGRESS:checking_cli")
                    {
                        setActiveStep(2);
                        UpdateStep(2, "active", T("正在检查已安装的 CloudCLI…", "正在檢查已安裝的 CloudCLI…", "Checking installed CloudCLI…"));
                    }
                    else if (line == "PROGRESS:installing_cli")
                    {
                        setInstallingCli(true);
                        setActiveStep(2);
                        UpdateStep(2, "active", T("正在通过 npm 安装 CloudCLI 到 WSL 独立目录（首次可能需要 1–2 分钟）…", "正在透過 npm 安裝 CloudCLI 到 WSL 獨立目錄（首次可能需要 1–2 分鐘）…", "Installing CloudCLI via npm into WSL isolated folder (may take 1–2 minutes)…"));
                        UpdateStepProgress(2, -2);
                    }
                    else if (line == "PROGRESS:verifying_cli")
                    {
                        setInstallingCli(false);
                        setActiveStep(2);
                        UpdateStep(2, "active", T("正在校验 CloudCLI 可执行文件…", "正在驗證 CloudCLI 執行檔…", "Verifying CloudCLI executable…"));
                    }
                    else if (line == "PROGRESS:starting_server")
                    {
                        setInstallingCli(false);
                        UpdateStep(2, "done", T("CloudCLI 已就绪", "CloudCLI 已就緒", "CloudCLI ready"));
                        setActiveStep(3);
                        UpdateStep(3, "active", T("正在启动 CloudCLI 服务…", "正在啟動 CloudCLI 服務…", "Starting CloudCLI server…"));
                    }
                }
            }
            catch { }
        }

        static bool ProbeWslReady(string distro)
        {
            return CaptureWsl(DistroArgument(distro) + " --exec /bin/sh -c \"echo ok\"") == "ok";
        }

        static bool NeedsLinuxUserSetup(string distro)
        {
            string res = CaptureWsl(DistroArgument(distro)
                + " --exec /bin/sh -c \"id -un; awk -F: '\\$3>=1000 && \\$3<65534 {print \\$1}' /etc/passwd | head -n 1\"");
            string[] lines = res.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;
            return lines[0].Trim() == "root" && lines.Length == 1;
        }

        async Task<string> EnsureWslUserAsync(string distro)
        {
            bool needsUser = await Task.Run(delegate { return NeedsLinuxUserSetup(distro); });
            if (!needsUser || exiting) return null;

            string lastError = null;
            while (!exiting)
            {
                wslUserTcs = new TaskCompletionSource<WslUserChoice>();
                ShowWslUserSetup(distro, lastError);
                WslUserChoice choice;
                try { choice = await wslUserTcs.Task; }
                catch (OperationCanceledException) { return null; }
                finally { wslUserTcs = null; }

                if (exiting || choice == null || choice.Skip)
                {
                    ShowProgress(T("跳过创建普通用户，继续部署…", "略過建立一般使用者，繼續部署…", "Skipping Linux user setup, continuing…"));
                    return null;
                }

                string msg = string.Format(T("正在为 {0} 创建 Linux 用户 {1}…", "正在為 {0} 建立 Linux 使用者 {1}…", "Creating Linux user {1} in {0}…"), distro, choice.Username);
                UpdateStep(0, "active", msg);
                ShowProgress(msg);

                try
                {
                    await CreateWslUserAsync(distro, choice.Username, choice.Password);
                    return choice.Username;
                }
                catch (Exception ex)
                {
                    Log("create wsl user failed: " + ex.Message);
                    lastError = ex.Message;
                }
            }
            return null;
        }

        async Task CreateWslUserAsync(string distro, string username, string password)
        {
            if (!Regex.IsMatch(username, "^[a-z_][a-z0-9_-]{0,31}$") || username == "root")
                throw new InvalidOperationException(T("无效的 Linux 用户名。", "無效的 Linux 使用者名稱。", "Invalid Linux username."));

            string shScript = "set -eu; u=\"$1\"; IFS= read -r p; "
                + "if ! id -u \"$u\" >/dev/null 2>&1; then useradd -m -s /bin/bash \"$u\"; fi; "
                + "if getent group sudo >/dev/null 2>&1; then usermod -aG sudo \"$u\"; fi; "
                + "printf '%s:%s\\n' \"$u\" \"$p\" | chpasswd; "
                + "if [ -f /etc/wsl.conf ]; then "
                + "awk -v u=\"$u\" 'BEGIN{in_u=0;done=0} /^\\[/{if(in_u && !done){print \"default=\" u; done=1} in_u=($0==\"[user]\")} "
                + "{if(in_u && $0 ~ /^default[[:space:]]*=/){if(!done){print \"default=\" u; done=1} next} print} "
                + "END{if(!done){if(!in_u) print \"[user]\"; print \"default=\" u}}' /etc/wsl.conf > /etc/wsl.conf.tmp "
                + "&& mv /etc/wsl.conf.tmp /etc/wsl.conf; "
                + "else printf '[user]\\ndefault=%s\\n' \"$u\" > /etc/wsl.conf; fi";

            var psi = new ProcessStartInfo("wsl.exe", DistroArgument(distro)
                + " -u root --exec /bin/sh -c \"" + shScript.Replace("\"", "\\\"") + "\" -- " + QuoteArgument(username))
            {
                WorkingDirectory = AppDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            string errDetail = "";
            int exitCode = await Task.Run(delegate
            {
                using (var p = Process.Start(psi))
                {
                    byte[] passBytes = new UTF8Encoding(false).GetBytes(password + "\n");
                    p.StandardInput.BaseStream.Write(passBytes, 0, passBytes.Length);
                    p.StandardInput.Close();
                    var outTask = ReadAllBytesAsync(p.StandardOutput.BaseStream);
                    var errTask = ReadAllBytesAsync(p.StandardError.BaseStream);
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch { }
                        return -2;
                    }
                    errDetail = DecodeProcessOutput(errTask.Result);
                    if (errDetail.Length == 0) errDetail = DecodeProcessOutput(outTask.Result);
                    return p.ExitCode;
                }
            });

            if (exitCode != 0)
                throw new InvalidOperationException(T("创建 Linux 用户失败", "建立 Linux 使用者失敗", "Failed to create Linux user")
                    + (errDetail.Length > 0 ? ": " + errDetail : " (exit " + exitCode + ")"));

            await Task.Run(delegate { CaptureWsl("--terminate " + QuoteArgument(distro)); });
            string activeUser = await Task.Run(delegate { return CaptureWsl(DistroArgument(distro) + " --exec /usr/bin/id -un"); });
            if (activeUser != username)
                throw new InvalidOperationException(T(
                    "用户已创建但未能切换为默认用户，请手动检查 /etc/wsl.conf。",
                    "使用者已建立但無法切換為預設使用者，請手動檢查 /etc/wsl.conf。",
                    "User was created, but failed to become the default WSL user."));
            Log("created and activated WSL default user: " + username);
        }

        sealed class WslInstallResult
        {
            public int ExitCode;
            public string Output;
        }

        static string ReadLatestLogLine(string logPath, out string fullText)
        {
            fullText = "";
            if (!File.Exists(logPath)) return "";
            try
            {
                byte[] bytes;
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                fullText = DecodeProcessOutput(bytes);
                if (string.IsNullOrEmpty(fullText)) return "";
                string[] lines = fullText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length > 0)
                        return line.Length > 140 ? line.Substring(0, 140) + "…" : line;
                }
            }
            catch { }
            return "";
        }

        async Task<WslInstallResult> RunElevatedWslInstallAsync(string args)
        {
            string batchPath = Path.Combine(Path.GetTempPath(), "cloudcli-wsl-" + Guid.NewGuid().ToString("N") + ".cmd");
            string logPath = Path.ChangeExtension(batchPath, ".log");
            File.WriteAllText(batchPath, "@echo off\r\nwsl.exe " + args + " > \"%~dpn0.log\" 2>&1\r\nexit /b %ERRORLEVEL%\r\n", Encoding.Default);

            Process installer;
            try
            {
                installer = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c \"" + batchPath + "\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Win32Exception ex)
            {
                try { File.Delete(batchPath); } catch { }
                if (ex.NativeErrorCode == 1223)
                    throw new InvalidOperationException(T(
                        "你取消了安装 WSL 的管理员权限请求。请点击“继续”并允许权限提示，或在终端手动运行 wsl --install。",
                        "你取消了安裝 WSL 的系統管理員權限要求。請點擊「繼續」並允許權限提示，或在終端機手動執行 wsl --install。",
                        "Administrator permission for installing WSL was canceled. Click Continue and approve the prompt, or run wsl --install manually."));
                throw new InvalidOperationException(T(
                    "无法启动 WSL 安装程序（wsl --install）。请确认 Windows 10/11 已开启并支持 WSL 功能：",
                    "無法啟動 WSL 安裝程式（wsl --install）。請確認 Windows 10/11 已支援 WSL 功能：",
                    "Could not start the WSL installer (wsl --install): ") + ex.Message);
            }

            var result = new WslInstallResult();
            string defaultStatus = T(
                "正在后台下载并安装 WSL 与 Ubuntu（镜像较大，可能需要几分钟，请耐心等待）…",
                "正在背景下載並安裝 WSL 與 Ubuntu（映像較大，可能需要幾分鐘，請耐心等候）…",
                "Downloading and installing WSL & Ubuntu in the background (may take several minutes)…");
            string lastShown = defaultStatus;
            UpdateStep(0, "active", defaultStatus);
            UpdateStepProgress(0, -2);

            try
            {
                if (installer != null)
                {
                    using (installer)
                    {
                        while (!installer.HasExited)
                        {
                            if (exiting) break;
                            string _;
                            string latest = ReadLatestLogLine(logPath, out _);
                            if (!string.IsNullOrEmpty(latest))
                            {
                                Match pctMatch = Regex.Match(latest, @"(\d{1,3})(?:\.\d+)?%");
                                int pct;
                                if (pctMatch.Success && int.TryParse(pctMatch.Groups[1].Value, out pct))
                                    UpdateStepProgress(0, Math.Max(0, Math.Min(100, pct)));
                                else
                                    UpdateStepProgress(0, -2);

                                string display = pctMatch.Success ? latest : latest + T("（镜像较大，请耐心等待）", "（映像較大，請耐心等候）", " (this may take a few minutes)");
                                if (display != lastShown)
                                {
                                    lastShown = display;
                                    UpdateStep(0, "active", display);
                                }
                            }
                            await Task.Delay(400);
                        }
                        if (!exiting)
                        {
                            installer.WaitForExit();
                            result.ExitCode = installer.ExitCode;
                        }
                    }
                }

                string fullLog;
                ReadLatestLogLine(logPath, out fullLog);
                result.Output = fullLog;
                Log("wsl " + args + " exited with code " + result.ExitCode
                    + (string.IsNullOrEmpty(fullLog) ? "" : " output=" + fullLog.Replace("\r", " ").Replace("\n", " | ")));
            }
            finally
            {
                try { File.Delete(batchPath); } catch { }
                try { File.Delete(logPath); } catch { }
            }

            return result;
        }

        async Task<string> InstallWslAsync()
        {
            UpdateStep(0, "active", T(
                "未检测到 WSL 发行版，正在请求管理员权限静默安装 WSL（Ubuntu）…",
                "未偵測到 WSL 發行版，正在要求系統管理員權限以背景安裝 WSL（Ubuntu）…",
                "No WSL distribution found. Requesting administrator permission to install WSL (Ubuntu)…"));
            Log("starting hidden elevated wsl --install -d Ubuntu --no-launch");

            await RunElevatedWslInstallAsync("--install -d Ubuntu --no-launch");
            if (exiting) return null;

            string distro = await Task.Run(delegate { return DetectDistro(); });
            if (distro != null)
            {
                bool ready = await Task.Run(delegate { return ProbeWslReady(distro); });
                if (ready) return distro;
                throw new InvalidOperationException(string.Format(T(
                    "WSL 发行版 {0} 已安装，但暂无法执行 Linux 命令。请确认已开启 BIOS/虚拟机嵌套虚拟化（VT-x/AMD-V），如果是首次开启 WSL 功能请重启 Windows 后再点击“继续”。",
                    "WSL 發行版 {0} 已安裝，但暫時無法執行 Linux 命令。請確認已開啟 BIOS／虛擬機巢狀虛擬化（VT-x/AMD-V），若是首次啟用 WSL 功能請重新啟動 Windows 後再點擊「繼續」。",
                    "WSL distribution {0} is installed, but cannot run Linux commands yet. Ensure hardware/nested virtualization is enabled and reboot Windows if WSL was just enabled, then click Continue."), distro));
            }

            throw new InvalidOperationException(T(
                "已运行 WSL 安装程序，但尚未检测到可用的 WSL 发行版。请确认已开启虚拟化支持（虚拟机需开启嵌套虚拟化），若首次开启 WSL 功能请先重启 Windows 后再点“继续”。",
                "已執行 WSL 安裝程式，但尚未偵測到可用的 WSL 發行版。請確認已開啟虛擬化支援（虛擬機需開啟巢狀虛擬化），若首次啟用 WSL 功能請先重新啟動 Windows 後再點擊「繼續」。",
                "The WSL installer finished, but no usable distribution is ready yet. Ensure virtualization is enabled (nested virtualization for VMs), reboot Windows if WSL was enabled for the first time, and click Continue."));
        }

        async Task<string> InstallManagedNodeAsync(string distro)
        {
            UpdateStep(1, "active", T(
                "未检测到 Node.js 22+，正在下载并安装独立版 Node.js 22 LTS…",
                "未偵測到 Node.js 22+，正在下載並安裝獨立版 Node.js 22 LTS…",
                "Node.js 22+ not found. Downloading isolated Node.js 22 LTS…"));
            UpdateStepProgress(1, -2);
            Log("installing managed Node.js 22 LTS into ~/.local/share/cloudcli-launcher/node in " + distro);

            string script = string.Join("\n", new[]
            {
                "set -eu",
                "base=\"$HOME/.local/share/cloudcli-launcher\"",
                "target=\"$base/node\"",
                "tmp=\"$base/tmp\"",
                "mkdir -p \"$tmp\"",
                "arch=\"$(uname -m)\"",
                "case \"$arch\" in",
                "  x86_64|amd64) narch=x64 ;;",
                "  aarch64|arm64) narch=arm64 ;;",
                "  *) echo \"Unsupported CPU architecture: $arch\" >&2; exit 2 ;;",
                "esac",
                "fetch() {",
                "  if command -v curl >/dev/null 2>&1; then",
                "    curl -fsSL \"$1\" -o \"$2\"",
                "  elif command -v wget >/dev/null 2>&1; then",
                "    wget -qO \"$2\" \"$1\"",
                "  else",
                "    echo \"Neither curl nor wget is available in WSL.\" >&2",
                "    exit 3",
                "  fi",
                "}",
                "ext=tar.gz",
                "if command -v xz >/dev/null 2>&1; then ext=tar.xz; fi",
                "echo \"PROGRESS:node_resolving\"",
                "shasums=\"$tmp/SHASUMS256.txt\"",
                "fetch \"https://nodejs.org/dist/latest-v22.x/SHASUMS256.txt\" \"$shasums\"",
                "line=$(grep \"node-v22\\.[0-9.]*-linux-$narch\\.$ext$\" \"$shasums\" | head -n 1 || true)",
                "if [ -z \"$line\" ]; then echo \"Could not find Node.js v22 LTS archive for linux-$narch.$ext\" >&2; exit 4; fi",
                "hash=$(printf '%s' \"$line\" | awk '{print $1}')",
                "file=$(printf '%s' \"$line\" | awk '{print $2}')",
                "ver=$(printf '%s' \"$file\" | sed -E 's/^node-(v[0-9.]+)-linux-.*/\\1/')",
                "url=\"https://nodejs.org/dist/latest-v22.x/$file\"",
                "archive=\"$tmp/$file\"",
                "total=0",
                "if command -v curl >/dev/null 2>&1; then",
                "  total=$(curl -fsSLI \"$url\" 2>/dev/null | awk 'tolower($1)==\"content-length:\"{v=$2} END{gsub(/\\r/,\"\",v); print v+0}' || echo 0)",
                "fi",
                "rm -f \"$archive\"",
                "echo \"PROGRESS:node_dl:$ver:0:0:$total\"",
                "fetch \"$url\" \"$archive\" &",
                "fp=$!",
                "while kill -0 \"$fp\" 2>/dev/null; do",
                "  cur=$(stat -c%s \"$archive\" 2>/dev/null || echo 0)",
                "  if [ \"$total\" -gt 0 ] 2>/dev/null; then",
                "    pct=$((cur * 100 / total))",
                "    [ \"$pct\" -gt 100 ] && pct=100",
                "  else",
                "    pct=-2",
                "  fi",
                "  echo \"PROGRESS:node_dl:$ver:$pct:$cur:$total\"",
                "  sleep 0.5",
                "done",
                "wait \"$fp\"",
                "cur=$(stat -c%s \"$archive\" 2>/dev/null || echo 0)",
                "if [ \"$total\" -gt 0 ] 2>/dev/null; then echo \"PROGRESS:node_dl:$ver:100:$cur:$total\"; fi",
                "echo \"PROGRESS:node_verifying:$ver\"",
                "printf '%s  %s\\n' \"$hash\" \"$archive\" | sha256sum -c - >/dev/null",
                "echo \"PROGRESS:node_extracting:$ver\"",
                "stage=$(mktemp -d \"$tmp/node-XXXXXX\")",
                "tar -xf \"$archive\" -C \"$stage\" --strip-components=1",
                "PATH=\"$stage/bin:$PATH\" \"$stage/bin/node\" --version >/dev/null",
                "PATH=\"$stage/bin:$PATH\" \"$stage/bin/npm\" --version >/dev/null",
                "rm -rf \"$target\"",
                "mv \"$stage\" \"$target\"",
                "rm -f \"$archive\" \"$shasums\"",
                "echo \"PROGRESS:node_done:$ver\"",
                ""
            });

            var psi = new ProcessStartInfo("wsl.exe", DistroArgument(distro) + " --exec /bin/sh -s")
            {
                WorkingDirectory = AppDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var stderrBuffer = new StringBuilder();
            using (var proc = Process.Start(psi))
            {
                byte[] scriptBytes = new UTF8Encoding(false).GetBytes(script);
                proc.StandardInput.BaseStream.Write(scriptBytes, 0, scriptBytes.Length);
                proc.StandardInput.Close();

                Task errTask = Task.Run(async delegate
                {
                    try
                    {
                        string errLine;
                        while ((errLine = await proc.StandardError.ReadLineAsync()) != null)
                        {
                            if (stderrBuffer.Length < 4096) stderrBuffer.AppendLine(errLine);
                        }
                    }
                    catch { }
                });

                Task outTask = Task.Run(async delegate
                {
                    try
                    {
                        string outLine;
                        while ((outLine = await proc.StandardOutput.ReadLineAsync()) != null)
                        {
                            if (exiting) break;
                            outLine = outLine.Trim();
                            if (outLine == "PROGRESS:node_resolving")
                            {
                                UpdateStepProgress(1, -2);
                                UpdateStep(1, "active", T("正在获取 Node.js 22 LTS 最新版本信息…", "正在取得 Node.js 22 LTS 最新版本資訊…", "Fetching Node.js 22 LTS release info…"));
                            }
                            else if (outLine.StartsWith("PROGRESS:node_dl:"))
                            {
                                string[] p = outLine.Substring("PROGRESS:node_dl:".Length).Split(':');
                                if (p.Length == 4)
                                {
                                    string ver = p[0];
                                    int pct;
                                    long curBytes, totalBytes;
                                    if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pct)) pct = -2;
                                    long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out curBytes);
                                    long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out totalBytes);
                                    UpdateStepProgress(1, pct);
                                    if (totalBytes > 0)
                                    {
                                        string curMb = (curBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
                                        string totMb = (totalBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
                                        UpdateStep(1, "active", string.Format(T("正在下载 Node.js {0} · {1} / {2} MB", "正在下載 Node.js {0} · {1} / {2} MB", "Downloading Node.js {0} · {1} / {2} MB"), ver, curMb, totMb));
                                    }
                                    else if (curBytes > 0)
                                    {
                                        string curMb = (curBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
                                        UpdateStep(1, "active", string.Format(T("正在下载 Node.js {0} · {1} MB", "正在下載 Node.js {0} · {1} MB", "Downloading Node.js {0} · {1} MB"), ver, curMb));
                                    }
                                    else
                                    {
                                        UpdateStep(1, "active", string.Format(T("正在下载 Node.js {0}…", "正在下載 Node.js {0}…", "Downloading Node.js {0}…"), ver));
                                    }
                                }
                            }
                            else if (outLine.StartsWith("PROGRESS:node_verifying:"))
                            {
                                string ver = outLine.Substring("PROGRESS:node_verifying:".Length);
                                UpdateStepProgress(1, -2);
                                UpdateStep(1, "active", string.Format(T("正在校验 Node.js {0} SHA-256…", "正在驗證 Node.js {0} SHA-256…", "Verifying Node.js {0} SHA-256…"), ver));
                            }
                            else if (outLine.StartsWith("PROGRESS:node_extracting:"))
                            {
                                string ver = outLine.Substring("PROGRESS:node_extracting:".Length);
                                UpdateStepProgress(1, -2);
                                UpdateStep(1, "active", string.Format(T("正在解压并配置 Node.js {0}…", "正在解壓並設定 Node.js {0}…", "Extracting Node.js {0}…"), ver));
                            }
                        }
                    }
                    catch { }
                });

                bool exited = await Task.Run(delegate { return proc.WaitForExit(300000); });
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    throw new InvalidOperationException(T(
                        "安装 Node.js 22 LTS 超时，请检查 WSL 网络后重试。",
                        "安裝 Node.js 22 LTS 逾時，請檢查 WSL 網路後再試。",
                        "Timed out installing Node.js 22 LTS. Check your WSL network and try again."));
                }
                await Task.WhenAll(outTask, errTask);
                if (exiting) return null;

                if (proc.ExitCode != 0)
                {
                    string errText = stderrBuffer.ToString().Trim();
                    Log("managed node install failed (exit " + proc.ExitCode + "): " + errText);
                    throw new InvalidOperationException(T(
                        "自动安装 Node.js 22 LTS 失败",
                        "自動安裝 Node.js 22 LTS 失敗",
                        "Failed to install Node.js 22 LTS automatically")
                        + (errText.Length > 0 ? ": " + errText : " (exit " + proc.ExitCode + ")"));
                }
            }

            string home = await Task.Run(delegate
            {
                return CaptureWsl(DistroArgument(distro) + " --exec /usr/bin/printenv HOME");
            });
            string nodePath = (home.StartsWith("/") ? home.TrimEnd('/') : "/root")
                + "/.local/share/cloudcli-launcher/node/bin/node";
            string installedVersion = await Task.Run(delegate
            {
                return CaptureWsl(DistroArgument(distro) + " --exec " + QuoteArgument(nodePath) + " --version");
            });
            Version parsed;
            if (!Version.TryParse(installedVersion.TrimStart('v'), out parsed) || parsed.Major < 22)
            {
                throw new InvalidOperationException(T(
                    "已解压 Node.js，但未能通过版本校验。请查看 logs/launcher.log。",
                    "已解壓 Node.js，但無法通過版本驗證。請查看 logs/launcher.log。",
                    "Node.js was extracted but failed verification. Check logs/launcher.log."));
            }
            Log("installed managed Node.js " + installedVersion + " at " + nodePath);
            return nodePath;
        }

        async Task ShowPage()
        {
            UpdateStep(3, "active", T("正在加载 CloudCLI 界面…", "正在載入 CloudCLI 介面…", "Loading CloudCLI…"));
            if (!await EnsureWebViewAsync() || exiting) return;
            showingInternalPage = false;
            showingProgressPage = false;
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

        static bool IsValidDistroName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name.StartsWith("-")) return false;
            foreach (char c in name)
            {
                if (c > 127 || !(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ' '))
                    return false;
            }
            return true;
        }

        static bool IsSystemContainerDistro(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return string.Equals(name, "docker-desktop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "docker-desktop-data", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "rancher-desktop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "rancher-desktop-data", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("podman-machine-", StringComparison.OrdinalIgnoreCase);
        }

        static System.Collections.Generic.List<string> ListDistros(int timeoutMs = 20000)
        {
            var result = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string raw = CaptureWsl("-l -q", null, timeoutMs).Replace("\uFEFF", "").Replace("\0", "").Trim();
            foreach (string line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (IsValidDistroName(t) && !IsSystemContainerDistro(t) && seen.Add(t))
                    result.Add(t);
            }
            return result;
        }

        static string DetectDistro()
        {
            var distros = ListDistros();
            return distros.Count > 0 ? distros[0] : null;
        }

        static string QuoteArgument(string value)
        {
            if (value.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("Invalid executable path");
            return value.Contains(" ") ? "\"" + value + "\"" : value;
        }

        static bool IsSafeUnixPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith("/")
                && path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) < 0;
        }

        static void AddVersionedCandidates(string root, string baseUnixDir, string binSuffix, System.Collections.Generic.List<string> candidates, ref bool managerFound)
        {
            string directory = root + baseUnixDir.Replace('/', '\\');
            if (!Directory.Exists(directory)) return;
            managerFound = true;
            string[] entries;
            try { entries = Directory.GetDirectories(directory); }
            catch { return; }
            Array.Sort(entries, delegate(string left, string right)
            {
                Version a, b;
                Version.TryParse(Path.GetFileName(left).TrimStart('v'), out a);
                Version.TryParse(Path.GetFileName(right).TrimStart('v'), out b);
                return System.Collections.Generic.Comparer<Version>.Default.Compare(b, a);
            });
            foreach (string entry in entries)
                candidates.Add(baseUnixDir + "/" + Path.GetFileName(entry) + binSuffix);
        }

        static string FindNode(string distro, bool allowMissing, out string cloudCliPath)
        {
            cloudCliPath = null;
            string prefix = DistroArgument(distro) + " --exec ";

            // Tier 1: usable Node 22+ and cloudcli already on PATH.
            string versionOnPath = CaptureWsl(prefix + "/usr/bin/env node --version", expectedMissing: "node");
            Version parsed;
            bool compatible = Version.TryParse(versionOnPath.TrimStart('v'), out parsed) && parsed.Major >= 22;
            if (compatible && CaptureWsl(prefix + "/usr/bin/env cloudcli --version", expectedMissing: "cloudcli").Length > 0)
                return "node";

            // Tier 2: known version managers & system-level Node locations via \\wsl.localhost.
            string home = CaptureWsl(prefix + "/usr/bin/printenv HOME");
            string homeTrim = home.StartsWith("/") ? home.TrimEnd('/') : "";
            var candidates = new System.Collections.Generic.List<string>();
            bool managerFound = false;
            string root = @"\\wsl.localhost\" + distro;
            if (homeTrim.Length > 0)
            {
                AddVersionedCandidates(root, homeTrim + "/.nvm/versions/node", "/bin/node", candidates, ref managerFound);
                AddVersionedCandidates(root, homeTrim + "/.local/share/fnm/node-versions", "/installation/bin/node", candidates, ref managerFound);
                AddVersionedCandidates(root, homeTrim + "/.fnm/node-versions", "/installation/bin/node", candidates, ref managerFound);
                AddVersionedCandidates(root, homeTrim + "/.volta/tools/image/node", "/bin/node", candidates, ref managerFound);
                AddVersionedCandidates(root, homeTrim + "/.asdf/installs/nodejs", "/bin/node", candidates, ref managerFound);
                AddVersionedCandidates(root, homeTrim + "/.local/share/mise/installs/node", "/bin/node", candidates, ref managerFound);
                AddVersionedCandidates(root, homeTrim + "/.nodenv/versions", "/bin/node", candidates, ref managerFound);
                AddVersionedCandidates(root, homeTrim + "/.local/share/pnpm/nodejs", "/bin/node", candidates, ref managerFound);
                AddVersionedCandidates(root, homeTrim + "/.proto/tools/node", "/bin/node", candidates, ref managerFound);

                foreach (string marker in new[] { "/.nvm/nvm.sh", "/.volta", "/.asdf", "/.local/share/mise",
                    "/.nodenv", "/.local/share/fnm", "/.fnm", "/.proto", "/.local/share/pnpm" })
                {
                    string markerWin = root + (homeTrim + marker).Replace('/', '\\');
                    if (File.Exists(markerWin) || Directory.Exists(markerWin))
                    {
                        managerFound = true;
                        break;
                    }
                }
            }
            AddVersionedCandidates(root, "/usr/local/n/versions/node", "/bin/node", candidates, ref managerFound);

            var singleBins = new System.Collections.Generic.List<string> { "/home/linuxbrew/.linuxbrew/bin/node" };
            if (homeTrim.Length > 0) singleBins.Add(homeTrim + "/.nix-profile/bin/node");
            foreach (string singleBin in singleBins)
            {
                if (File.Exists(root + singleBin.Replace('/', '\\')))
                    candidates.Add(singleBin);
            }

            string fallback = compatible ? "node" : null;
            var seenCandidates = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (string candidate in candidates)
            {
                if (!seenCandidates.Add(candidate)) continue;
                string version = CaptureWsl(prefix + QuoteArgument(candidate) + " --version");
                if (!Version.TryParse(version.TrimStart('v'), out parsed) || parsed.Major < 22) continue;
                if (fallback == null) fallback = candidate;
                string sibling = candidate.Substring(0, candidate.LastIndexOf('/') + 1) + "cloudcli";
                if (File.Exists(root + sibling.Replace('/', '\\')))
                {
                    cloudCliPath = sibling;
                    return candidate;
                }
            }

            var globalCliCandidates = new System.Collections.Generic.List<string>();
            if (homeTrim.Length > 0)
            {
                foreach (string relCli in new[] { "/.volta/bin/cloudcli", "/.local/share/pnpm/cloudcli",
                    "/.asdf/shims/cloudcli", "/.local/share/mise/shims/cloudcli", "/.proto/shims/cloudcli",
                    "/.proto/bin/cloudcli", "/.npm-global/bin/cloudcli", "/.nix-profile/bin/cloudcli" })
                {
                    globalCliCandidates.Add(homeTrim + relCli);
                }
            }
            globalCliCandidates.Add("/home/linuxbrew/.linuxbrew/bin/cloudcli");

            if (fallback != null)
            {
                foreach (string cliCandidate in globalCliCandidates)
                {
                    if (File.Exists(root + cliCandidate.Replace('/', '\\')))
                    {
                        cloudCliPath = cliCandidate;
                        return fallback;
                    }
                }
            }

            // Tier 3: login interactive shell fallback for custom/non-standard managers or PATH overrides.
            string shellOut = CaptureWsl(
                prefix + "/bin/sh -c \"s=\\\"${SHELL:-/bin/bash}\\\"; [ -x \\\"$s\\\" ] || s=/bin/sh; \\\"$s\\\" -lic 'echo __CCLI_START__; command -v cloudcli 2>/dev/null; echo __CCLI_SEP__; command -v node 2>/dev/null; echo __CCLI_END__' </dev/null\"",
                null, 6000);
            string shellCli = null;
            string shellNode = null;
            if (!string.IsNullOrEmpty(shellOut))
            {
                int startIdx = shellOut.IndexOf("__CCLI_START__", StringComparison.Ordinal);
                int sepIdx = startIdx >= 0 ? shellOut.IndexOf("__CCLI_SEP__", startIdx + "__CCLI_START__".Length, StringComparison.Ordinal) : -1;
                int endIdx = sepIdx >= 0 ? shellOut.IndexOf("__CCLI_END__", sepIdx + "__CCLI_SEP__".Length, StringComparison.Ordinal) : -1;
                if (startIdx >= 0 && sepIdx > startIdx && endIdx > sepIdx)
                {
                    string cliSection = shellOut.Substring(startIdx + "__CCLI_START__".Length, sepIdx - startIdx - "__CCLI_START__".Length);
                    foreach (string line in cliSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = line.Trim();
                        if (IsSafeUnixPath(t)) { shellCli = t; break; }
                    }
                    string nodeSection = shellOut.Substring(sepIdx + "__CCLI_SEP__".Length, endIdx - sepIdx - "__CCLI_SEP__".Length);
                    foreach (string line in nodeSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = line.Trim();
                        if (IsSafeUnixPath(t)) { shellNode = t; break; }
                    }
                }
            }
            if (!string.IsNullOrEmpty(shellNode))
            {
                string shellNodeVer = CaptureWsl(prefix + QuoteArgument(shellNode) + " --version", null, 5000);
                if (Version.TryParse(shellNodeVer.TrimStart('v'), out parsed) && parsed.Major >= 22)
                {
                    if (!string.IsNullOrEmpty(shellCli) && File.Exists(root + shellCli.Replace('/', '\\')))
                    {
                        cloudCliPath = shellCli;
                        return shellNode;
                    }
                    int lastSlash = shellNode.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        string sibling = shellNode.Substring(0, lastSlash + 1) + "cloudcli";
                        if (File.Exists(root + sibling.Replace('/', '\\')))
                        {
                            cloudCliPath = sibling;
                            return shellNode;
                        }
                    }
                    if (fallback == null) fallback = shellNode;
                }
            }
            if (fallback != null && !string.IsNullOrEmpty(shellCli) && File.Exists(root + shellCli.Replace('/', '\\')))
            {
                cloudCliPath = shellCli;
                return fallback;
            }
            if (fallback != null) return fallback;

            // Tier 4: launcher-managed isolated Node.js runtime (native ext4 first, then legacy NTFS).
            if (homeTrim.Length > 0)
            {
                string ext4Node = homeTrim + "/.local/share/cloudcli-launcher/node/bin/node";
                string ext4Ver = CaptureWsl(prefix + QuoteArgument(ext4Node) + " --version");
                if (Version.TryParse(ext4Ver.TrimStart('v'), out parsed) && parsed.Major >= 22)
                    return ext4Node;
            }

            string managedWin = Path.Combine(AppDir, "data", "runtime", distro, "node", "bin", "node");
            if (File.Exists(managedWin))
            {
                string managedRel = "./data/runtime/" + distro + "/node/bin/node";
                string managedVer = CaptureWsl(prefix + QuoteArgument(managedRel) + " --version");
                if (Version.TryParse(managedVer.TrimStart('v'), out parsed) && parsed.Major >= 22)
                    return managedRel;
            }

            if (allowMissing) return null;
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

        static async Task<byte[]> ReadAllBytesAsync(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                return ms.ToArray();
            }
        }

        static string DecodeProcessOutput(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (Array.IndexOf(bytes, (byte)0) >= 0)
                text = Encoding.Unicode.GetString(bytes);
            else
                text = Encoding.UTF8.GetString(bytes);
            return text.Replace("\uFEFF", "").Replace("\0", "").Trim();
        }

        static string CaptureWsl(string args, string expectedMissing = null, int timeoutMs = 20000)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var psi = new ProcessStartInfo("wsl.exe", args);
                psi.WorkingDirectory = AppDir;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    var output = ReadAllBytesAsync(p.StandardOutput.BaseStream);
                    var errors = ReadAllBytesAsync(p.StandardError.BaseStream);
                    if (!p.WaitForExit(timeoutMs))
                    {
                        Log("WSL probe timed out after " + timer.ElapsedMilliseconds + " ms: " + args);
                        p.Kill();
                        return "";
                    }
                    string outText = DecodeProcessOutput(output.Result);
                    string detail = DecodeProcessOutput(errors.Result);
                    string context = args + " (exit " + p.ExitCode + ", " + timer.ElapsedMilliseconds + " ms)";
                    if (expectedMissing != null && p.ExitCode == 127)
                    {
                        Log(expectedMissing + " unavailable on PATH; checking managed installations. " + context);
                        return "";
                    }
                    if (p.ExitCode != 0)
                    {
                        if (detail.Length == 0) detail = outText;
                        Log("WSL probe failed: " + context + (detail.Length == 0 ? "" : " - " + detail));
                        return "";
                    }
                    Log("WSL probe completed: " + context);
                    return outText;
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