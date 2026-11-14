using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GitHubDownloader
{
    internal static class App
    {
        public static void LogLine(string line)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHub下载器");
                Directory.CreateDirectory(dir);
                string logFile = Path.Combine(dir, "app.log");
                try { if (File.Exists(logFile) && new FileInfo(logFile).Length > 256 * 1024) File.Delete(logFile); } catch { }
                File.AppendAllText(logFile,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public static readonly Color Bg = Color.FromArgb(11, 13, 18);
        public static readonly Color Card = Color.FromArgb(20, 24, 33);
        public static readonly Color CardHover = Color.FromArgb(27, 32, 44);
        public static readonly Color Line = Color.FromArgb(38, 45, 60);
        public static readonly Color Text = Color.FromArgb(233, 237, 245);
        public static readonly Color Muted = Color.FromArgb(138, 147, 166);
        public static readonly Color Accent = Color.FromArgb(108, 123, 255);
        public static readonly Color Accent2 = Color.FromArgb(150, 120, 255);
        public static readonly Color Ok = Color.FromArgb(78, 214, 161);
        public static readonly Color Err = Color.FromArgb(255, 110, 120);
        public static readonly Color LogBg = Color.FromArgb(14, 17, 24);
        public static readonly Color CloseHover = Color.FromArgb(232, 17, 35);

        /// <summary>版本号只在这里改，标题和界面都引用它。</summary>
        public static readonly string Version = "1.6";

        public static float Scale = 1f;
        public static string StartUrl;
        public static string StartOut;
        public static bool AutoStart;
        public static bool AutoExit;

        public static int S(int value)
        {
            return (int)Math.Round(value * Scale);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

        private static readonly Guid FolderIdDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        /// <summary>
        /// 系统的「下载」文件夹。不能硬拼 %USERPROFILE%\Downloads —— 用户可能把它
        /// 重定向到 D 盘或 OneDrive，只有问 Shell 才是准的。
        /// </summary>
        public static string GetDownloadsFolder()
        {
            try
            {
                Guid id = FolderIdDownloads;
                IntPtr ptr;
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out ptr) == 0 && ptr != IntPtr.Zero)
                {
                    string path = Marshal.PtrToStringUni(ptr);
                    Marshal.FreeCoTaskMem(ptr);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch { }

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}");
                        if (value != null)
                        {
                            string path = Environment.ExpandEnvironmentVariables(Convert.ToString(value));
                            if (!string.IsNullOrEmpty(path)) return path;
                        }
                    }
                }
            }
            catch { }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0 || r.Width <= 1 || r.Height <= 1) { path.AddRectangle(r); return path; }

            // 圆角半径不能超过短边的一半：否则左右（上下）两个角的弧会相互重叠，
            // 那条边会被画成畸形——「胶囊」高度本来就等于两倍圆角时最容易踩到。
            int maxRadius = Math.Min(r.Width, r.Height) / 2;
            int rad = Math.Min(radius, maxRadius);
            int d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [STAThread]
        public static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--url" && i + 1 < args.Length) StartUrl = args[++i];
                else if (a == "--out" && i + 1 < args.Length) StartOut = args[++i];
                else if (a == "--auto") AutoStart = true;
                else if (a == "--exit") AutoExit = true;
            }
            // .NET Framework 默认 TLS 版本偏旧，遇到只认 TLS1.2 的代理/CDN 会报 SecureChannelFailure
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            try
            {
                float dpi = GetDpiForSystem();
                Scale = dpi > 0 ? dpi / 96f : 1f;
            }
            catch
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            }

            bool created;
            var mutex = new Mutex(true, "Local\\GitHubDownloader.SingleInstance", out created);
            if (!created)
            {
                // 窗口标题是「GitHub 下载器 · v1.5」，用 FindWindow 精确匹配根本找不到，
                // 结果是第二次双击毫无反应。改成直接找同名进程的主窗口。
                foreach (var other in Process.GetProcessesByName("GitHubDownloader"))
                {
                    if (other.Id == Process.GetCurrentProcess().Id) continue;
                    IntPtr hwnd = other.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    ShowWindow(hwnd, 9);          // SW_RESTORE
                    SetForegroundWindow(hwnd);
                    break;
                }
                return;
            }

            CloseStaleWindows();
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { App.LogLine("fatal " + e.ExceptionObject); } catch { }
            };
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try { App.LogLine("ui " + e.Exception); } catch { }
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            GC.KeepAlive(mutex);
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void CloseStaleWindows()
        {
            uint self = (uint)Process.GetCurrentProcess().Id;
            foreach (string name in new string[] { "GitHub下载器", "GitHubDownloader" })
            {
                foreach (var other in Process.GetProcessesByName(name))
                {
                    if ((uint)other.Id == self) continue;
                    try { other.Kill(); } catch { }
                }
            }
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == self) return true;
                var title = new System.Text.StringBuilder(256);
                GetWindowText(hWnd, title, title.Capacity);
                if (title.ToString().StartsWith("GitHub 下载器")) PostMessage(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        /// <summary>
        /// 让原生滚动条跟随深色主题。不处理的话，深蓝界面里会插一条刺眼的浅色滚动条。
        /// Windows 11 / Win10 1809+ 支持 DarkMode_Explorer。
        /// </summary>
        public static void ApplyDarkScrollbar(Control control)
        {
            try
            {
                if (control != null && control.IsHandleCreated)
                    SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            }
            catch { }
        }
    }

    internal static class Settings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHub下载器", "settings.ini");

        public static string DownloadDir;
        public static bool ReadClipboard;
        public static string GitHubToken = "";
        public static bool Exists;
        /// <summary>旧默认值是「下载\GitHub」，现在用系统「下载」根目录，需要静默迁移一次。</summary>
        public static bool MigratedLegacyDir;

        static Settings()
        {
            string downloads = App.GetDownloadsFolder();
            DownloadDir = downloads;
            ReadClipboard = true;
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
            try
            {
                if (File.Exists(FilePath))
                {
                    Exists = true;
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim();
                        if (key == "downloadDir" && value.Length > 0) DownloadDir = value;
                        else if (key == "readClipboard") ReadClipboard = value != "0";
                        else if (key == "githubToken") GitHubToken = value;
                    }
                }
            }
            catch { }

            // settings.ini 里如果还存着旧默认值（...\Downloads\GitHub），迁到系统「下载」根目录
            try
            {
                if (!string.IsNullOrEmpty(DownloadDir))
                {
                    string legacy = Path.Combine(downloads, "GitHub");
                    string cur = DownloadDir.TrimEnd('\\', '/');
                    if (string.Equals(cur, legacy.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        DownloadDir = downloads;
                        MigratedLegacyDir = true;
                    }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                string text = "downloadDir=" + DownloadDir + Environment.NewLine +
                              "readClipboard=" + (ReadClipboard ? "1" : "0") + Environment.NewLine +
                              "githubToken=" + (GitHubToken ?? "") + Environment.NewLine;
                File.WriteAllText(FilePath, text, new UTF8Encoding(false));
                Exists = true;
            }
            catch { }
        }
    }

    /// <summary>
    /// 找一台能用的本地代理。国内加速器端口五花八门，很多只开 socks
    /// （比如 sing-box 的 10808 只认 socks5，不认 HTTP CONNECT），
    /// 而 .NET 的 HttpWebRequest 根本不支持 socks —— 所以探出来的地址主要给 curl 用。
    /// </summary>
    internal static class ProxyFinder
    {
        private static string cached;
        private static bool probed;
        private static readonly object gate = new object();

        private static readonly int[] CommonPorts = new int[]
        {
            10808, 10809, 10810, 1080, 1081, 7890, 7891, 7897, 2080, 20171, 8889, 8118, 8080
        };

        public static string Find(bool refresh)
        {
            lock (gate)
            {
                if (!refresh && probed) return cached;
                probed = true;
                cached = Probe();
                App.LogLine("proxy=" + (string.IsNullOrEmpty(cached) ? "(none)" : cached));
                return cached;
            }
        }

        private static string Probe()
        {
            var candidates = new List<string>();
            string sys = ReadSystemProxy();
            if (!string.IsNullOrEmpty(sys)) candidates.Add("http://" + sys);

            foreach (int port in CommonPorts) candidates.Add("socks5h://127.0.0.1:" + port);
            foreach (int port in CommonPorts) candidates.Add("http://127.0.0.1:" + port);

            var seen = new HashSet<string>();
            foreach (string candidate in candidates)
            {
                if (!seen.Add(candidate)) continue;
                if (!PortAlive(candidate)) continue;
                if (TestProxy(candidate)) return candidate;
            }
            return "";
        }

        private static string ReadSystemProxy()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key == null) return null;
                    object enabled = key.GetValue("ProxyEnable");
                    object server = key.GetValue("ProxyServer");
                    if (enabled == null || Convert.ToInt32(enabled) != 1 || server == null) return null;
                    string srv = Convert.ToString(server);
                    if (srv.IndexOf('=') >= 0)
                    {
                        foreach (string part in srv.Split(';'))
                        {
                            int eq = part.IndexOf('=');
                            if (eq > 0 && part.Substring(0, eq).ToLowerInvariant().StartsWith("http"))
                                return part.Substring(eq + 1).Trim();
                        }
                        return null;
                    }
                    return srv.Trim();
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 失败时给出可操作的现场信息，而不是一句「没找到代理」。
        /// 最常见的根因就是 hosts 把 github 指到了本机，而代理又没开。
        /// </summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();

            try
            {
                string hostsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                if (File.Exists(hostsPath))
                {
                    int blocked = 0;
                    foreach (string raw in File.ReadAllLines(hostsPath))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        if (line.IndexOf("github", StringComparison.OrdinalIgnoreCase) >= 0) blocked++;
                    }
                    if (blocked > 0)
                        sb.Append("hosts 里有 ").Append(blocked).Append(" 行 github 记录指向本机（会挡住直连）；");
                }
            }
            catch { }

            string sys = ReadSystemProxy();
            if (!string.IsNullOrEmpty(sys)) sb.Append("系统代理设为 ").Append(sys).Append("，但连不通；");
            else sb.Append("系统代理没开；");

            var alive = new List<string>();
            foreach (int port in CommonPorts)
            {
                if (PortAlive("socks5h://127.0.0.1:" + port)) alive.Add(port.ToString());
            }
            if (alive.Count > 0) sb.Append("探测到的监听端口：").Append(string.Join("/", alive.ToArray())).Append("（但都连不通 GitHub）；");
            else sb.Append("常见代理端口（10808/10809/7890 等）都没有服务在听；");

            sb.Append("先确认加速器已启动。");
            return sb.ToString();
        }

        private static bool PortAlive(string proxyUrl)
        {
            try
            {
                var uri = new Uri(proxyUrl.Replace("socks5h://", "http://"));
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var task = client.ConnectAsync(uri.Host, uri.Port);
                    if (!task.Wait(150)) return false;
                    return client.Connected;
                }
            }
            catch { return false; }
        }

        private static bool TestProxy(string proxyUrl)
        {
            try
            {
                var psi = new ProcessStartInfo("curl.exe");
                psi.Arguments = "-s -o NUL -w \"%{http_code}\" --max-time 3 --proxy \"" + proxyUrl +
                    "\" https://api.github.com/rate_limit";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var proc = Process.Start(psi))
                {
                    string code = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(4000);
                    return code == "200";
                }
            }
            catch { return false; }
        }
    }

    internal class RoundPanel : Panel
    {
        public Color Fill = App.Card;
        public int Radius = 10;

        public RoundPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = App.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var surface = new SolidBrush(App.Card)) e.Graphics.FillRectangle(surface, ClientRectangle);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = App.Round(rect, App.S(Radius)))
            using (var b = new SolidBrush(Fill))
                e.Graphics.FillPath(b, path);
        }
    }

    internal class Card : Panel
    {
        public int Radius = 16;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = App.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = App.Round(rect, App.S(Radius)))
            {
                using (var fill = new SolidBrush(App.Card)) e.Graphics.FillPath(fill, path);
                using (var pen = new Pen(App.Line)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal class PillButton : Control
    {
        public bool Primary;
        public bool Subtle;
        public bool Danger;
        public Color Surface = App.Bg;
        private bool hover;
        private bool pressed;

        public PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = App.Bg;
            ForeColor = App.Text;
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasPressed = pressed;
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
            if (wasPressed && Enabled && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
                OnClick(EventArgs.Empty);
        }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var surface = new SolidBrush(Surface)) e.Graphics.FillRectangle(surface, ClientRectangle);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill;
            Color border = Color.Empty;

            if (Primary)
            {
                fill = !Enabled ? Color.FromArgb(50, 56, 80)
                    : pressed ? Color.FromArgb(92, 106, 224)
                    : hover ? App.Accent2 : App.Accent;
            }
            else
            {
                fill = !Enabled ? Color.FromArgb(22, 26, 35)
                    : pressed ? Color.FromArgb(31, 36, 48)
                    : hover ? App.CardHover : Color.FromArgb(24, 28, 38);
                border = App.Line;
                if (Danger && hover) fill = Color.FromArgb(42, 26, 32);
            }

            using (var path = App.Round(rect, App.S(12)))
            {
                using (var b = new SolidBrush(fill)) e.Graphics.FillPath(b, path);
                if (border != Color.Empty)
                {
                    using (var pen = new Pen(Focused ? App.Accent : border)) e.Graphics.DrawPath(pen, path);
                }
            }

            Color textColor = Enabled ? (Primary ? Color.White : App.Text) : App.Muted;
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// 日志区用的细滚动条。原生 WinForms 滚动条在深色界面上是一条白色竖条，
    /// 非常突兀；系统深色主题（DarkMode_Explorer）又暗到几乎看不见。
    /// 所以自己画：深色轨道 + 灰色圆角滑块。
    /// </summary>
    internal class SlimScroll : Control
    {
        public Action<int> ScrollTo;

        private int total;
        private int first;
        private int visible;
        private bool hover;
        private bool dragging;
        private int dragStartY;
        private int dragStartFirst;

        public SlimScroll()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(14, 17, 24);
            Visible = false;
        }

        public void Sync(int totalLines, int firstVisible, int visibleLines)
        {
            total = totalLines;
            first = firstVisible;
            visible = visibleLines;

            bool needed = total > visible && visible > 0;
            if (Visible != needed) Visible = needed;
            if (needed) Invalidate();
        }

        private int ThumbHeight()
        {
            if (total <= 0) return Height;
            double ratio = (double)visible / total;
            return Math.Max(App.S(24), (int)(Height * ratio));
        }

        private int ThumbTop()
        {
            int travel = Height - ThumbHeight();
            int maxFirst = Math.Max(1, total - visible);
            return (int)((double)first / maxFirst * travel);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || ScrollTo == null) return;
            int top = ThumbTop();
            if (e.Y >= top && e.Y <= top + ThumbHeight())
            {
                dragging = true;
                dragStartY = e.Y;
                dragStartFirst = first;
                Invalidate();
            }
            else
            {
                // 点轨道：整页跳
                int target = e.Y < top ? first - visible : first + visible;
                ScrollTo(Math.Max(0, Math.Min(total - visible, target)));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging || ScrollTo == null) return;
            int travel = Math.Max(1, Height - ThumbHeight());
            int maxFirst = Math.Max(1, total - visible);
            int deltaLines = (int)((double)(e.Y - dragStartY) / travel * maxFirst);
            ScrollTo(Math.Max(0, Math.Min(maxFirst, dragStartFirst + deltaLines)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Color.FromArgb(14, 17, 24)))
                e.Graphics.FillRectangle(bg, ClientRectangle);

            if (total <= visible || total <= 0) return;

            int th = ThumbHeight();
            int tt = ThumbTop();
            var thumb = new Rectangle(0, tt, Width - 1, th);

            Color fill = dragging ? Color.FromArgb(150, 158, 178)
                       : hover ? Color.FromArgb(118, 126, 146)
                       : Color.FromArgb(84, 92, 110);
            using (var path = App.Round(thumb, Math.Max(2, Width / 2)))
            using (var b = new SolidBrush(fill))
                e.Graphics.FillPath(b, path);
        }
    }

    /// <summary>输入框右侧的圆形清除按钮，有内容时才显示。</summary>
    internal class ClearDot : Control
    {
        public Color Surface = Color.FromArgb(17, 21, 29);
        private bool hover;

        public ClearDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Surface;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // 先铺一层和输入框一样的内底色，圆之外不能露出别的颜色
            using (var bg = new SolidBrush(Surface)) e.Graphics.FillRectangle(bg, ClientRectangle);

            var circle = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = hover ? Color.FromArgb(56, 62, 80) : Color.FromArgb(32, 38, 52);
            using (var b = new SolidBrush(fill)) e.Graphics.FillEllipse(b, circle);

            int inset = Math.Max(App.S(5), Width / 3);
            using (var pen = new Pen(hover ? App.Text : App.Muted, Math.Max(1.4f, App.Scale * 1.6f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                e.Graphics.DrawLine(pen, inset, inset, Width - 1 - inset, Height - 1 - inset);
                e.Graphics.DrawLine(pen, Width - 1 - inset, inset, inset, Height - 1 - inset);
            }
        }
    }

    internal class RoundInput : Panel
    {
        public readonly TextBox Box;
        public Color Surface = App.Bg;
        private bool focused;
        private readonly ClearDot clear;

        public RoundInput()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = App.Bg;
            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Color.FromArgb(17, 21, 29);
            Box.ForeColor = App.Text;
            Box.Font = new Font("Consolas", 10f);
            Box.GotFocus += delegate { focused = true; Invalidate(); };
            Box.LostFocus += delegate { focused = false; Invalidate(); };
            Controls.Add(Box);

            clear = new ClearDot();
            clear.Surface = Box.BackColor;
            clear.Visible = false;                 // 空输入框不显示
            clear.Click += delegate
            {
                Box.Text = "";
                Box.Focus();
            };
            Controls.Add(clear);

            Box.TextChanged += delegate { SyncClearButton(); };
            Click += delegate { Box.Focus(); };    // 点输入框空白处也能聚焦
            Resize += delegate { LayoutBox(); };
        }

        private void SyncClearButton()
        {
            bool show = Box.TextLength > 0;
            if (clear.Visible != show) clear.Visible = show;
        }

        private void LayoutBox()
        {
            int pad = App.S(14);
            int dot = App.S(20);
            // 恒定留出右侧空间：清除按钮显示/隐藏时文字不会来回跳
            int rightReserve = dot + App.S(10);
            int boxWidth = Math.Max(App.S(40), Width - pad - rightReserve);
            Box.SetBounds(pad, Math.Max(0, (Height - Box.PreferredHeight) / 2), boxWidth, Box.PreferredHeight);
            clear.SetBounds(Width - pad + App.S(4) - dot, (Height - dot) / 2, dot, dot);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var surface = new SolidBrush(Surface)) e.Graphics.FillRectangle(surface, ClientRectangle);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = App.Round(rect, App.S(12)))
            {
                using (var b = new SolidBrush(Box.BackColor)) e.Graphics.FillPath(b, path);
                using (var pen = new Pen(focused ? App.Accent : App.Line)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal class AssetRow : Control
    {
        public string TitleText = "";
        public string MetaText = "";
        public bool Recommended;
        public bool Selected;
        public bool Checked;
        public string Url = "";
        public bool Hover;
        private readonly Font titleFont;
        private readonly Font metaFont;
        private readonly Font badgeFont;

        public AssetRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = App.Card;
            Cursor = Cursors.Hand;
            titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            metaFont = new Font("Consolas", 8.5f);
            badgeFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }

        private DateTime lastClickUtc = DateTime.MinValue;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Enabled || e.Button != MouseButtons.Left || !ClientRectangle.Contains(e.Location)) return;
            DateTime now = DateTime.UtcNow;
            if ((now - lastClickUtc).TotalMilliseconds < 450)
            {
                lastClickUtc = DateTime.MinValue;
                OnDoubleClick(EventArgs.Empty);
            }
            else
            {
                lastClickUtc = now;
                OnClick(EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = App.Round(rect, App.S(10)))
            {
                Color fill = Selected ? Color.FromArgb(34, 40, 60) : (Hover ? App.CardHover : Color.FromArgb(17, 21, 29));
                using (var b = new SolidBrush(fill)) e.Graphics.FillPath(b, path);
                using (var pen = new Pen(Selected ? App.Accent : App.Line)) e.Graphics.DrawPath(pen, path);
            }

            int pad = App.S(14);

            // 勾选框：多选批量下载用
            int box = App.S(16);
            var checkRect = new Rectangle(pad, (Height - box) / 2, box, box);
            if (Checked)
            {
                using (var path = App.Round(checkRect, App.S(4)))
                using (var b = new SolidBrush(App.Accent))
                    e.Graphics.FillPath(b, path);
                using (var pen = new Pen(Color.White, Math.Max(1.5f, App.S(2))))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    e.Graphics.DrawLines(pen, new Point[]
                    {
                        new Point(checkRect.Left + box / 5, checkRect.Top + box / 2),
                        new Point(checkRect.Left + box * 2 / 5, checkRect.Top + box * 7 / 10),
                        new Point(checkRect.Right - box / 5, checkRect.Top + box / 3)
                    });
                }
            }
            else
            {
                using (var path = App.Round(checkRect, App.S(4)))
                using (var pen = new Pen(Hover ? App.Muted : App.Line))
                    e.Graphics.DrawPath(pen, path);
            }

            int left = pad + box + App.S(10);
            // 右侧给「推荐」徽标留位；窗口很窄时不能算成负数（TextRenderer 会炸）
            int textWidth = Math.Max(App.S(40), Width - left - App.S(160));
            var titleRect = new Rectangle(left, App.S(7), textWidth, App.S(20));
            TextRenderer.DrawText(e.Graphics, TitleText, titleFont, titleRect, App.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var metaRect = new Rectangle(left, App.S(27), textWidth, App.S(18));
            TextRenderer.DrawText(e.Graphics, MetaText, metaFont, metaRect, App.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            if (Recommended)
            {
                var badge = new Rectangle(Width - App.S(136), App.S(13), App.S(68), App.S(26));
                using (var path = App.Round(badge, App.S(13)))
                using (var b = new SolidBrush(Color.FromArgb(38, 68, 58)))
                    e.Graphics.FillPath(b, path);
                TextRenderer.DrawText(e.Graphics, "推荐", badgeFont, badge, App.Ok,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    internal class ThinProgress : Control
    {
        private readonly System.Windows.Forms.Timer timer;
        private float offset;
        private long done;
        private long total;
        private long speed;

        public ThinProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = App.Bg;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 16;
            timer.Tick += delegate
            {
                offset += 0.018f;
                if (offset > 1.25f) offset = 0f;
                Invalidate();
            };
            VisibleChanged += delegate
            {
                if (Visible) timer.Start(); else timer.Stop();
            };
        }

        /// <summary>总字节数；0 表示还不知道，退回不确定式跑马灯。</summary>
        public void SetProgress(long doneBytes, long totalBytes, long speedBytes)
        {
            done = doneBytes;
            total = totalBytes;
            speed = speedBytes;
            if (total > 0) timer.Stop(); else if (Visible) timer.Start();
            Invalidate();
        }

        public long Speed { get { return speed; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(Color.FromArgb(26, 31, 42)))
                e.Graphics.FillRectangle(track, 0, 0, Width, Height);

            if (total > 0)
            {
                double pct = Math.Max(0, Math.Min(1, (double)done / total));
                int filled = (int)Math.Round(Width * pct);
                if (filled > 0)
                {
                    using (var path = App.Round(new Rectangle(0, 0, Math.Max(filled, Height), Height), Height / 2))
                    using (var b = new SolidBrush(App.Accent))
                        e.Graphics.FillPath(b, path);
                }
                return;
            }

            int w = Math.Max(App.S(70), Width / 5);
            int x = (int)((Width + w) * offset) - w;
            var seg = new Rectangle(x, 0, w, Height);
            using (var path = App.Round(seg, Height / 2))
            using (var b = new SolidBrush(App.Accent))
                e.Graphics.FillPath(b, path);
        }
    }


    internal sealed class SettingsDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public string DownloadDir;
        public bool ReadClipboard;
        public string Token;
        private readonly bool firstRun;
        private readonly RoundInput dirInput;
        private readonly RoundInput tokenInput;
        private readonly CheckBox clipCheck;

        public SettingsDialog(bool firstRun)
        {
            this.firstRun = firstRun;

            FormBorderStyle = FormBorderStyle.None;
            Text = firstRun ? "第一次使用" : "设置";
            BackColor = App.Bg;
            ForeColor = App.Text;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(App.S(660), App.S(430));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;

            var title = new Label();
            title.Text = firstRun ? "第一次使用" : "设置";
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.ForeColor = App.Text;
            title.BackColor = App.Bg;
            title.SetBounds(App.S(24), App.S(18), App.S(320), App.S(30));
            title.MouseDown += DragWindow;
            Controls.Add(title);

            var close = new PillButton();
            close.Text = "×";
            close.Danger = true;
            close.Font = new Font("Segoe UI", 12f, FontStyle.Regular);
            close.SetBounds(ClientSize.Width - App.S(48), App.S(20), App.S(28), App.S(28));
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(close);

            var hint = new Label();
            hint.Text = firstRun
                ? "先选一个默认下载目录，之后随时点右上角 ⚙ 改。"
                : "改完点保存，下次启动会用这里的设置。";
            hint.ForeColor = App.Muted;
            hint.BackColor = App.Bg;
            hint.SetBounds(App.S(24), App.S(52), ClientSize.Width - App.S(48), App.S(20));
            hint.MouseDown += DragWindow;
            Controls.Add(hint);

            var dirLabel = new Label();
            dirLabel.Text = "下载目录";
            dirLabel.ForeColor = App.Muted;
            dirLabel.BackColor = App.Bg;
            dirLabel.SetBounds(App.S(24), App.S(94), App.S(72), App.S(24));
            Controls.Add(dirLabel);

            dirInput = new RoundInput();
            dirInput.SetBounds(App.S(100), App.S(88), ClientSize.Width - App.S(220), App.S(36));
            dirInput.Box.Font = new Font("Consolas", 9.5f);
            Controls.Add(dirInput);

            var browse = new PillButton();
            browse.Text = "浏览";
            browse.SetBounds(ClientSize.Width - App.S(112), App.S(88), App.S(88), App.S(36));
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择默认下载目录";
                    dialog.SelectedPath = dirInput.Box.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) dirInput.Box.Text = dialog.SelectedPath;
                }
            };
            Controls.Add(browse);

            clipCheck = new CheckBox();
            clipCheck.Text = "启动时从剪贴板读取链接";
            clipCheck.ForeColor = App.Text;
            clipCheck.BackColor = App.Bg;
            clipCheck.FlatStyle = FlatStyle.Flat;
            clipCheck.SetBounds(App.S(24), App.S(140), App.S(300), App.S(26));
            Controls.Add(clipCheck);

            var tokenLabel = new Label();
            tokenLabel.Text = "GitHub 令牌";
            tokenLabel.ForeColor = App.Muted;
            tokenLabel.BackColor = App.Bg;
            tokenLabel.SetBounds(App.S(24), App.S(184), App.S(116), App.S(24));
            Controls.Add(tokenLabel);

            tokenInput = new RoundInput();
            tokenInput.SetBounds(App.S(144), App.S(178), ClientSize.Width - App.S(168), App.S(36));
            tokenInput.Box.Font = new Font("Consolas", 9f);
            tokenInput.Box.UseSystemPasswordChar = true;
            Controls.Add(tokenInput);

            var tokenHint = new Label();
            tokenHint.Text = "可选，不填也能正常用。\n" +
                "这是 GitHub 网站的 API 令牌，不是 AI 模型的 token。\n" +
                "唯一作用：解析次数上限从「每小时 60 次」提到「每小时 5000 次」。\n" +
                "生成方式：GitHub 头像 → Settings → Developer settings →\n" +
                "Personal access tokens，勾只读即可。只存在你本机。";
            tokenHint.ForeColor = App.Muted;
            tokenHint.BackColor = App.Bg;
            tokenHint.Font = new Font("Microsoft YaHei UI", 8.2f);
            tokenHint.SetBounds(App.S(24), App.S(218), ClientSize.Width - App.S(48), App.S(100));
            Controls.Add(tokenHint);

            var save = new PillButton();
            save.Primary = true;
            save.Text = firstRun ? "开始使用" : "保存";
            save.SetBounds(App.S(24), ClientSize.Height - App.S(64), App.S(140), App.S(44));
            save.Click += delegate { SaveSettings(); };
            Controls.Add(save);

            var cancel = new PillButton();
            cancel.Text = firstRun ? "用默认目录" : "取消";
            cancel.SetBounds(App.S(176), ClientSize.Height - App.S(64), App.S(140), App.S(44));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);


            Shown += delegate
            {
                dirInput.Box.Text = DownloadDir;
                clipCheck.Checked = ReadClipboard;
                tokenInput.Box.Text = Token;
                dirInput.Box.Focus();
                dirInput.Box.SelectionStart = dirInput.Box.TextLength;
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) { SaveSettings(); return true; }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
        }

        private void SaveSettings()
        {
            string dir = dirInput.Box.Text.Trim().Trim('"');
            if (dir.Length == 0)
            {
                MessageBox.Show(this, "下载目录不能为空。", "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DownloadDir = dir;
            ReadClipboard = clipCheck.Checked;
            Token = tokenInput.Box.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(App.Line))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class MainForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private readonly string scriptPath;
        private readonly string defaultOut;

        private RoundInput urlInput;
        private RoundInput outInput;
        private PillButton pasteButton;
        private PillButton resolveButton;
        private PillButton browseButton;
        private PillButton downloadButton;
        private PillButton cancelButton;
        private PillButton openButton;
        private Panel headerPanel;
        private ThinProgress progress;
        private Label statusLabel;
        private TextBox logBox;
        private Panel assetPanel;
        private Label assetTitle;
        private Label envLabel;
        private Process running;
        private List<AssetRow> rows = new List<AssetRow>();
        private AssetRow selectedRow;
        private int resolveToken;
        // 每项是 { 下载地址, 保存文件名 } —— 文件名必须来自 release 附件名
        private readonly Queue<string[]> pendingTargets = new Queue<string[]>();
        private string pendingOutDir = "";
        private int pendingCount;
        private int doneCount;
        private int failedCount;
        private DateTime lastOpenUtc = DateTime.MinValue;
        private RoundPanel logHost;
        private SlimScroll logScroll;
        private System.Windows.Forms.Timer logSync;
        private NotifyIcon tray;


        public MainForm()
        {
            scriptPath = ResolveScriptPath();
            App.LogLine("script=" + scriptPath);
            defaultOut = App.GetDownloadsFolder();

            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            Text = "GitHub 下载器 · v" + App.Version;
            BackColor = App.Bg;
            ForeColor = App.Text;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            AllowDrop = true;
            ClientSize = new Size(App.S(900), App.S(792));
            MinimumSize = new Size(App.S(760), App.S(660));

            // 高 DPI 下 CenterScreen 会把窗口摆到屏幕外，状态栏和日志区直接看不见，自己算位置
            StartPosition = FormStartPosition.Manual;
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int w = Math.Min(Width, wa.Width);
                int h = Math.Min(Height, wa.Height - App.S(24));
                if (w != Width || h != Height) Size = new Size(w, h);
                int left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                int top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                Location = new Point(left, top);
            }
            catch { StartPosition = FormStartPosition.CenterScreen; }


            BuildUi();

            // 窗口/任务栏图标：直接用 exe 内嵌的那份，和资源管理器里看到的一致
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            SetupTray();

            if (Settings.MigratedLegacyDir)
            {
                Settings.Save();
                App.LogLine("settings migrated -> " + Settings.DownloadDir);
            }
            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.Text)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                string text = e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrEmpty(text)) { urlInput.Box.Text = FirstLine(text); ResolveRelease(); }
            };

            Shown += delegate
            {
                ClampToWorkingArea();
                // CenterScreen 之后系统还会再摆一次，消息循环空转一圈再钳一次
                BeginInvoke(new Action(delegate { ClampToWorkingArea(); }));

                if (!Settings.Exists && string.IsNullOrEmpty(App.StartOut))
                {
                    using (var dialog = new SettingsDialog(true))
                    {
                        dialog.DownloadDir = Settings.DownloadDir;
                        dialog.ReadClipboard = Settings.ReadClipboard;
                        dialog.Token = Settings.GitHubToken;
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                            ApplySettings(dialog.DownloadDir, dialog.ReadClipboard, dialog.Token);
                        else
                        {
                            Settings.DownloadDir = dialog.DownloadDir;
                            Settings.ReadClipboard = dialog.ReadClipboard;
                            Settings.Save();
                            outInput.Box.Text = Settings.DownloadDir;
                        }
                    }
                }

                string owner, repo, tag;
                string text = urlInput.Box.Text.Trim();
                if (text.Length > 0 && TryParseRepo(text, out owner, out repo, out tag))
                    ResolveRelease();
                else if (App.AutoStart)
                    StartDownload();   // 直链没法解析，直接下
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Pure popup: no WS_CAPTION, no WS_THICKFRAME. A sizing frame is what
                // lets Windows paint a native title bar again on some state changes,
                // so the chrome is kept out of the window style completely.
                cp.Style &= ~0x00C00000; // WS_CAPTION
                cp.Style &= ~0x00040000; // WS_THICKFRAME
                cp.Style &= ~0x00080000; // WS_SYSMENU
                cp.Style &= ~0x00400000; // WS_DLGFRAME
                cp.Style &= ~0x00800000; // WS_BORDER
                cp.Style &= ~0x00030000; // WS_MINIMIZEBOX | WS_MAXIMIZEBOX
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW keeps a soft shadow without a frame
                return cp;
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private bool applyingChrome;
        private System.Windows.Forms.Timer chromeTimer;
        private int lastStyle = -1;

        private void ApplyChrome()
        {
            if (applyingChrome) return;
            applyingChrome = true;
            try
            {
                const int GWL_STYLE = -16;
                const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;
                int style = GetWindowLong(Handle, GWL_STYLE);
                int cleaned = style & ~0x00C00000 & ~0x00040000 & ~0x00080000 & ~0x00400000 & ~0x00800000 & ~0x00030000;
                if (cleaned != style)
                {
                    SetWindowLong(Handle, GWL_STYLE, cleaned);
                    SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                }
            }
            catch { }
            finally { applyingChrome = false; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            }
            catch { }
            base.OnFormClosing(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyChrome();
            chromeTimer = new System.Windows.Forms.Timer();
            chromeTimer.Interval = 400;
            chromeTimer.Tick += delegate
            {
                ApplyChrome();
                int s = GetWindowLong(Handle, -16);
                if (s != lastStyle) { lastStyle = s; App.LogLine("style=0x" + s.ToString("X8")); }
            };
            chromeTimer.Start();
            try
            {
                int dark = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero; // drop the native frame, keep the resize behaviour
                return;
            }
            if (m.Msg == 0x0086 || m.Msg == 0x0085)
            {
                // WM_NCACTIVATE / WM_NCPAINT: never let Windows paint a non-client frame
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1)
                {
                    int screenX = (short)(m.LParam.ToInt32() & 0xFFFF);
                    int screenY = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                    Point p = PointToClient(new Point(screenX, screenY));
                    int grip = App.S(7);
                    bool left = p.X <= grip;
                    bool right = p.X >= ClientSize.Width - grip;
                    bool top = p.Y <= grip;
                    bool bottom = p.Y >= ClientSize.Height - grip;
                    if (left && top) m.Result = (IntPtr)HTTOPLEFT;
                    else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (left) m.Result = (IntPtr)HTLEFT;
                    else if (right) m.Result = (IntPtr)HTRIGHT;
                    else if (top) m.Result = (IntPtr)HTTOP;
                    else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x0005 || m.Msg == 0x0006 || m.Msg == 0x02E0 || m.Msg == 0x0018 || m.Msg == 0x0086 || m.Msg == 0x0047)
                ApplyChrome();
        }

        private static string ResolveScriptPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "scripts", "gh.ps1"),
                Path.Combine(baseDir, "gh.ps1"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHub下载器", "gh.ps1")
            };
            foreach (string candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            return candidates[candidates.Length - 1];
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : null;
        }

        private static string SafeClipboard()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null; }
            catch { return null; }
        }

        private static bool LooksLikeTarget(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return System.Text.RegularExpressions.Regex.IsMatch(text, @"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$");
        }

        private void BuildUi()
        {
            headerPanel = new Panel();
            headerPanel.BackColor = App.Bg;
            headerPanel.SetBounds(0, 0, ClientSize.Width, App.S(74));
            headerPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            headerPanel.MouseDown += OnHeaderMouseDown;
            Controls.Add(headerPanel);

            // 左上角品牌位：用嵌入的 icon.png（高分辨率，缩下来比从 ico 取 32px 清晰）
            var logo = new PictureBox();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = App.Bg;
            logo.SetBounds(App.S(22), App.S(17), App.S(32), App.S(32));
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("GitHubDownloader.icon.png"))
                {
                    if (stream != null) logo.Image = Image.FromStream(stream);
                }
            }
            catch { }
            logo.MouseDown += OnHeaderMouseDown;
            headerPanel.Controls.Add(logo);

            var title = new Label();
            title.Text = "GitHub 下载器";
            title.Font = new Font("Microsoft YaHei UI", 14.5f, FontStyle.Bold);
            title.ForeColor = App.Text;
            title.BackColor = App.Bg;
            title.SetBounds(App.S(54), App.S(18), App.S(250), App.S(30));
            title.MouseDown += OnHeaderMouseDown;
            headerPanel.Controls.Add(title);

            var version = new Label();
            version.Text = "v" + App.Version;
            version.Font = new Font("Consolas", 8.5f);
            version.ForeColor = App.Accent;
            version.BackColor = App.Bg;
            version.SetBounds(App.S(300), App.S(24), App.S(60), App.S(20));
            version.MouseDown += OnHeaderMouseDown;
            headerPanel.Controls.Add(version);
            version.BringToFront();

            var subtitle = new Label();
            subtitle.Text = "贴链接，挑附件，拉下来。";
            subtitle.ForeColor = App.Muted;
            subtitle.BackColor = App.Bg;
            subtitle.SetBounds(App.S(56), App.S(46), App.S(250), App.S(20));
            subtitle.MouseDown += OnHeaderMouseDown;
            headerPanel.Controls.Add(subtitle);

            envLabel = new Label();
            envLabel.Text = DetectEnvironment();
            envLabel.TextAlign = ContentAlignment.MiddleCenter;
            envLabel.ForeColor = App.Muted;
            envLabel.BackColor = App.Bg;
            envLabel.Font = new Font("Consolas", 8.8f);
            // 靠左固定，紧跟在版本号后面。
            // 以前放在 x=560 且 Anchor 默认（Top|Left），窗口缩小时右侧的 ⚙ / – / ×
            // 会一路左移压到它身上；挪到标题区就不会再撞。
            envLabel.SetBounds(App.S(372), App.S(20), App.S(196), App.S(28));
            envLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            envLabel.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, envLabel.Width - 1, envLabel.Height - 1);
                using (var path = App.Round(rect, App.S(14)))
                {
                    using (var b = new SolidBrush(Color.FromArgb(22, 27, 37))) e.Graphics.FillPath(b, path);
                    using (var pen = new Pen(App.Line)) e.Graphics.DrawPath(pen, path);
                }
                TextRenderer.DrawText(e.Graphics, envLabel.Text, envLabel.Font, rect, App.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            headerPanel.Controls.Add(envLabel);
            // 置顶：Label 的背景是不透明的，若被 title/subtitle 压住会「缺一块」
            envLabel.BringToFront();

            var gear = new PillButton();
            gear.Text = "⚙";
            gear.Font = new Font("Segoe UI Symbol", 11f);
            gear.SetBounds(ClientSize.Width - App.S(120), App.S(22), App.S(28), App.S(28));
            gear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            gear.Click += delegate { OpenSettings(); };
            headerPanel.Controls.Add(gear);

            var close = new PillButton();
            close.Text = "×";
            close.Danger = true;
            close.Font = new Font("Segoe UI", 12f, FontStyle.Regular);
            close.SetBounds(ClientSize.Width - App.S(48), App.S(22), App.S(28), App.S(28));
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Click += delegate { TryClose(); };
            headerPanel.Controls.Add(close);

            var minimize = new PillButton();
            minimize.Text = "–";
            minimize.Font = new Font("Segoe UI", 11f, FontStyle.Regular);
            minimize.SetBounds(ClientSize.Width - App.S(84), App.S(22), App.S(28), App.S(28));
            minimize.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            headerPanel.Controls.Add(minimize);

            var urlCard = new Card();
            urlCard.SetBounds(App.S(24), App.S(88), ClientSize.Width - App.S(48), App.S(66));
            urlCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(urlCard);

            urlInput = new RoundInput();
            urlInput.Surface = App.Card;
            urlInput.SetBounds(App.S(12), App.S(15), urlCard.Width - App.S(312), App.S(36));
            urlInput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            urlInput.Box.TextChanged += delegate { UpdateDownloadState(); };
            urlInput.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ResolveRelease(); }
            };
            urlCard.Controls.Add(urlInput);

            pasteButton = new PillButton();
            pasteButton.Surface = App.Card;
            pasteButton.Text = "粘贴";
            pasteButton.SetBounds(urlCard.Width - App.S(288), App.S(15), App.S(80), App.S(36));
            pasteButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            pasteButton.Click += delegate
            {
                string clipText = FirstLine(SafeClipboard());
                if (!string.IsNullOrEmpty(clipText)) { urlInput.Box.Text = clipText; ResolveRelease(); }
            };
            urlCard.Controls.Add(pasteButton);

            resolveButton = new PillButton();
            resolveButton.Surface = App.Card;
            resolveButton.Primary = true;
            resolveButton.Text = "解析附件";
            resolveButton.SetBounds(urlCard.Width - App.S(196), App.S(15), App.S(184), App.S(36));
            resolveButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            resolveButton.Click += delegate { ResolveRelease(); };
            urlCard.Controls.Add(resolveButton);

            var assetCard = new Card();
            assetCard.SetBounds(App.S(24), App.S(170), ClientSize.Width - App.S(48), App.S(286));
            assetCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(assetCard);

            assetTitle = new Label();
            assetTitle.Text = "Release 附件";
            assetTitle.ForeColor = App.Muted;
            assetTitle.BackColor = App.Card;
            assetTitle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            assetTitle.SetBounds(App.S(18), App.S(10), App.S(600), App.S(20));
            assetCard.Controls.Add(assetTitle);

            assetPanel = new Panel();
            assetPanel.SetBounds(App.S(10), App.S(36), assetCard.Width - App.S(20), assetCard.Height - App.S(46));
            assetPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            assetPanel.AutoScroll = true;
            assetPanel.BackColor = App.Card;
            assetPanel.Layout += delegate
            {
                int width = assetPanel.ClientSize.Width - App.S(22);
                foreach (var r in rows) if (r.Width != width) r.Width = width;
            };
            assetCard.Controls.Add(assetPanel);

            var outCard = new Card();
            outCard.SetBounds(App.S(24), App.S(470), ClientSize.Width - App.S(48), App.S(56));
            outCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(outCard);

            var outLabel = new Label();
            outLabel.Text = "保存到";
            outLabel.ForeColor = App.Muted;
            outLabel.BackColor = App.Card;
            outLabel.SetBounds(App.S(18), App.S(18), App.S(56), App.S(20));
            outCard.Controls.Add(outLabel);

            outInput = new RoundInput();
            outInput.Surface = App.Card;
            outInput.SetBounds(App.S(74), App.S(10), outCard.Width - App.S(192), App.S(36));
            outInput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            outInput.Box.Font = new Font("Consolas", 9.5f);
            outCard.Controls.Add(outInput);

            browseButton = new PillButton();
            browseButton.Surface = App.Card;
            browseButton.Text = "浏览";
            browseButton.SetBounds(outCard.Width - App.S(104), App.S(10), App.S(92), App.S(36));
            browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browseButton.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择下载保存位置";
                    dialog.SelectedPath = outInput.Box.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        outInput.Box.Text = dialog.SelectedPath;
                        Settings.DownloadDir = dialog.SelectedPath;
                        Settings.Save();
                    }
                }
            };
            outCard.Controls.Add(browseButton);

            downloadButton = new PillButton();
            downloadButton.Primary = true;
            downloadButton.Text = "开始下载";
            downloadButton.SetBounds(App.S(24), App.S(542), App.S(176), App.S(44));
            downloadButton.Click += delegate { StartDownload(); };
            Controls.Add(downloadButton);

            cancelButton = new PillButton();
            cancelButton.Text = "取消";
            cancelButton.Enabled = false;
            cancelButton.SetBounds(App.S(212), App.S(542), App.S(104), App.S(44));
            cancelButton.Click += delegate { CancelDownload(); };
            Controls.Add(cancelButton);

            openButton = new PillButton();
            openButton.Text = "打开文件夹";
            openButton.SetBounds(ClientSize.Width - App.S(168), App.S(542), App.S(144), App.S(44));
            openButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            openButton.Click += delegate { OpenOutput(); };
            Controls.Add(openButton);

            progress = new ThinProgress();
            progress.SetBounds(App.S(24), App.S(596), ClientSize.Width - App.S(48), App.S(4));
            progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progress.Visible = false;
            Controls.Add(progress);

            var logCard = new Card();
            logCard.SetBounds(App.S(24), App.S(610), ClientSize.Width - App.S(48), App.S(116));
            logCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(logCard);

            logHost = new RoundPanel();
            logHost.Fill = App.LogBg;
            logHost.Radius = 10;
            logHost.SetBounds(App.S(10), App.S(10), logCard.Width - App.S(20), logCard.Height - App.S(20));
            logHost.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            logCard.Controls.Add(logHost);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            // 原生滚动条在深色界面上是刺眼的白条，改用自绘的灰色细条
            logBox.ScrollBars = ScrollBars.None;
            logBox.BackColor = App.LogBg;
            logBox.ForeColor = Color.FromArgb(196, 204, 220);
            logBox.BorderStyle = BorderStyle.None;
            logBox.Font = new Font("Consolas", 8.8f);
            logBox.SetBounds(App.S(6), App.S(6), logHost.Width - App.S(12), logHost.Height - App.S(12));
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            logHost.Controls.Add(logBox);

            logScroll = new SlimScroll();
            logScroll.ScrollTo = delegate(int target) { ScrollLogTo(target); };
            logHost.Controls.Add(logScroll);
            logHost.Resize += delegate { LayoutLogArea(); };
            LayoutLogArea();

            logSync = new System.Windows.Forms.Timer();
            logSync.Interval = 120;
            logSync.Tick += delegate { SyncLogScroll(); };
            logSync.Start();

            statusLabel = new Label();
            statusLabel.Text = "就绪";
            statusLabel.ForeColor = App.Muted;
            statusLabel.BackColor = App.Bg;
            statusLabel.SetBounds(App.S(24), ClientSize.Height - App.S(34), ClientSize.Width - App.S(48), App.S(22));
            statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(statusLabel);

            string clip = Settings.ReadClipboard ? FirstLine(SafeClipboard()) : null;
            if (LooksLikeTarget(clip)) urlInput.Box.Text = clip;
            if (!string.IsNullOrEmpty(App.StartUrl)) urlInput.Box.Text = App.StartUrl;
            if (!string.IsNullOrEmpty(App.StartOut))
            {
                outInput.Box.Text = App.StartOut;
                Settings.DownloadDir = App.StartOut;
            }
            else
            {
                outInput.Box.Text = Settings.DownloadDir;
            }
            UpdateDownloadState();
        }

        private void OnHeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        private static string DetectEnvironment()
        {
            string os = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
            string arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            return os + " · " + arch;
        }

        private void OpenSettings()
        {
            using (var dialog = new SettingsDialog(false))
            {
                dialog.DownloadDir = Settings.DownloadDir;
                dialog.ReadClipboard = Settings.ReadClipboard;
                dialog.Token = Settings.GitHubToken;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    ApplySettings(dialog.DownloadDir, dialog.ReadClipboard, dialog.Token);
            }
        }

        private void ApplySettings(string dir, bool readClipboard, string token)
        {
            Settings.DownloadDir = dir;
            Settings.ReadClipboard = readClipboard;
            Settings.GitHubToken = token ?? "";
            Settings.Save();
            outInput.Box.Text = dir;
            SetStatus(string.IsNullOrEmpty(Settings.GitHubToken)
                ? "设置已保存（匿名访问，API 限额 60 次/小时）。"
                : "设置已保存（已配置 Token，API 限额 5000 次/小时）。", App.Ok);
        }

        private void SetStatus(string text, Color color)
        {
            if (IsDisposed) return;
            // 状态栏只有一行，长错误会撑出去被硬截断，这里主动省略
            if (!string.IsNullOrEmpty(text) && text.Length > 110)
                text = text.Substring(0, 110) + "…（完整信息看下面日志）";
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
        }

        private void UpdateDownloadState()
        {
            bool busy = running != null;
            int checkedCount = 0;
            foreach (var r in rows) if (r.Checked) checkedCount++;

            downloadButton.Enabled = !busy &&
                (checkedCount > 0 || !string.IsNullOrWhiteSpace(urlInput.Box.Text));
            if (!busy) downloadButton.Text = "开始下载";
        }

        private static string FormatDuration(long seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return seconds + " 秒";
            if (seconds < 3600) return (seconds / 60) + " 分 " + (seconds % 60) + " 秒";
            return (seconds / 3600) + " 时 " + ((seconds % 3600) / 60) + " 分";
        }

        private void UpdateProgressUi(long done, long total, long speed)
        {
            progress.SetProgress(done, total, speed);

            var sb = new StringBuilder();
            if (pendingCount > 1)
                sb.Append('[').Append(Math.Min(doneCount + 1, pendingCount)).Append('/').Append(pendingCount).Append("]  ");
            sb.Append(FormatSize(done));
            if (total > 0)
            {
                sb.Append(" / ").Append(FormatSize(total));
                sb.Append("  ").Append((done * 100.0 / total).ToString("0.0")).Append('%');
                if (speed > 0)
                {
                    sb.Append("  ").Append(FormatSize(speed)).Append("/s");
                    sb.Append("  剩余 ").Append(FormatDuration((total - done) / speed));
                }
            }
            SetStatus(sb.ToString(), App.Muted);
        }

        /// <summary>
        /// 系统托盘：窗口是无边框 popup，最小化后不容易找回来，托盘是最稳的入口。
        /// </summary>
        private void SetupTray()
        {
            try
            {
                tray = new NotifyIcon();
                tray.Icon = Icon ?? SystemIcons.Application;
                tray.Text = "GitHub 下载器";
                tray.Visible = true;

                var menu = new ContextMenuStrip();
                var showItem = new ToolStripMenuItem("显示窗口");
                showItem.Click += delegate { RestoreWindow(); };
                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += delegate { TryClose(); };
                menu.Items.Add(showItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(exitItem);
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { RestoreWindow(); };
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void RestoreWindow()
        {
            try
            {
                Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                SetForegroundWindow(Handle);
            }
            catch { }
        }

        /// <summary>把窗口钳进当前显示器工作区（高 DPI 下 CenterScreen 会把它推出屏幕）。</summary>
        private void ClampToWorkingArea()
        {
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                int w = Math.Min(Width, wa.Width - App.S(8));
                int h = Math.Min(Height, wa.Height - App.S(8));
                if (w != Width || h != Height) Size = new Size(w, h);

                int left = Math.Max(wa.Left, Math.Min(Left, wa.Right - w));
                int top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - h));
                Location = new Point(left, top);
            }
            catch { }
        }

        private void ResolveRelease()
        {
            string target = urlInput.Box.Text.Trim();
            if (target.Length == 0) { SetStatus("先贴一个链接。", App.Err); return; }

            string owner, repo, tag;
            if (!TryParseRepo(target, out owner, out repo, out tag))
            {
                SetStatus("不是仓库或 Release 页面，直接下载就行。", App.Muted);
                ClearAssets();
                assetTitle.Text = "Release 附件 · 未解析";
                return;
            }

            assetTitle.Text = "Release 附件 · 读取中…";
            ClearAssets();
            SetStatus("正在读取 Release…", App.Muted);
            AppendLog("读取 " + owner + "/" + repo +
                (string.IsNullOrEmpty(tag) ? " 的最新 Release …" : " 的 Release @" + tag + " …"));

            string apiBase = Environment.GetEnvironmentVariable("GHD_API_BASE");
            if (string.IsNullOrEmpty(apiBase)) apiBase = "https://api.github.com";
            string api = apiBase.TrimEnd('/') + "/repos/" + owner + "/" + repo +
                (string.IsNullOrEmpty(tag) ? "/releases/latest" : "/releases/tags/" + tag);
            int token = ++resolveToken;
            App.LogLine("resolve " + api);

            var thread = new Thread(delegate()
            {
                string body = null;
                string error = null;
                try
                {
                    body = FetchReleaseJson(api);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    App.LogLine("resolve err " + ex.GetType().Name + " " + ex.Message);
                }

                if (IsDisposed || token != resolveToken) return;
                try
                {
                    BeginInvoke(new Action<string, string>(delegate(string json, string err)
                    {
                        if (token != resolveToken) return;
                        if (err != null)
                        {
                            assetTitle.Text = "Release 附件 · 读取失败";
                            SetStatus(err, App.Err);
                            AppendLog("[解析失败] " + err);
                            return;
                        }
                        ShowAssets(json);
                    }), body, error);
                }
                catch (InvalidOperationException) { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// 拿 GitHub API：直连 → 系统代理 → curl 兜底。
        /// 最后一级是关键：.NET 的 HttpWebRequest 不支持 socks，
        /// 而国内加速器很多只开 socks 端口（sing-box 的 10808 就是这样）。
        /// </summary>
        private static string FetchReleaseJson(string api)
        {
            var errors = new List<string>();

            try { return FetchOnce(api, false); }
            catch (Exception ex) { errors.Add("直连：" + ex.Message); }

            try { return FetchOnce(api, true); }
            catch (Exception ex) { errors.Add("系统代理：" + ex.Message); }

            try { return FetchViaCurl(api); }
            catch (Exception ex) { errors.Add("curl：" + ex.Message); }

            throw new Exception(string.Join("；", errors.ToArray()));
        }

        private static string FetchViaCurl(string api)
        {
            string proxy = ProxyFinder.Find(false);
            if (proxy == null || proxy.Length == 0)
                throw new Exception("找不到可用代理。" + ProxyFinder.Diagnose());

            var psi = new ProcessStartInfo("curl.exe");
            var sb = new StringBuilder();
            sb.Append("-s --max-time 12 -A \"GitHubDownloader/2.0\" -H \"Accept: application/vnd.github+json\"");
            sb.Append(" --proxy \"").Append(proxy).Append("\"");
            if (!string.IsNullOrEmpty(Settings.GitHubToken))
                sb.Append(" -H \"Authorization: Bearer ").Append(Settings.GitHubToken.Replace("\"", "")).Append("\"");
            sb.Append(" \"").Append(api).Append("\"");
            psi.Arguments = sb.ToString();
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (var proc = Process.Start(psi))
            {
                string body = proc.StandardOutput.ReadToEnd();
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit(15000);
                if (proc.ExitCode != 0)
                {
                    ProxyFinder.Find(true);
                    throw new Exception("curl 退出码 " + proc.ExitCode +
                        (string.IsNullOrWhiteSpace(err) ? "" : "：" + err.Trim()));
                }
                if (string.IsNullOrWhiteSpace(body)) throw new Exception("curl 没返回内容");
                return body;
            }
        }

        private static string FetchOnce(string api, bool useProxy)
        {
            var request = (HttpWebRequest)WebRequest.Create(api);
            request.UserAgent = "GitHubDownloader/2.0";
            request.Accept = "application/vnd.github+json";
            // 超时压短：网络不通时最烦的就是干等，宁可快点失败
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            // 本地调试地址 / 显式直连：都不走系统代理
            if (!useProxy || api.StartsWith("http://127.0.0.1") || api.StartsWith("http://localhost"))
                request.Proxy = null;
            if (!string.IsNullOrEmpty(Settings.GitHubToken))
                request.Headers["Authorization"] = "Bearer " + Settings.GitHubToken;

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException we)
            {
                var http = we.Response as HttpWebResponse;
                if (http == null)
                {
                    if (useProxy)
                        throw new Exception("系统代理连不上（" + we.Status + NumberedProxy() + "）");
                    throw new Exception("直连不通（" + we.Status + "，可能是网络或 hosts 挡住了 github）");
                }

                string remaining = http.Headers["X-RateLimit-Remaining"];
                if (http.StatusCode == (HttpStatusCode)403 || http.StatusCode == (HttpStatusCode)429)
                {
                    if (remaining == "0")
                        throw new Exception("GitHub API 限流了（匿名每小时 60 次）。到 ⚙ 里填一个 Token 可以提到 5000 次。");
                    throw new Exception("GitHub 拒绝了这次请求（HTTP " + (int)http.StatusCode + "）。");
                }
                if (http.StatusCode == HttpStatusCode.NotFound)
                    throw new Exception("仓库或 Release 不存在（私有仓库需要 Token）。");
                throw new Exception("HTTP " + (int)http.StatusCode + " " + http.StatusDescription);
            }
        }

        /// <summary>把注册表里的代理地址带进报错，用户一眼能看出是哪个代理挂了。</summary>
        private static string NumberedProxy()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key == null) return "";
                    object enabled = key.GetValue("ProxyEnable");
                    object server = key.GetValue("ProxyServer");
                    if (enabled == null || Convert.ToInt32(enabled) != 1) return "";
                    if (server == null) return "";
                    return "，系统代理是 " + Convert.ToString(server) + "，加速器可能没开";
                }
            }
            catch { return ""; }
        }

        private void ClearAssets()
        {
            foreach (var row in rows) row.Dispose();
            rows.Clear();
            selectedRow = null;
            assetPanel.Controls.Clear();
        }

        private void ShowAssets(string json)
        {
            ClearAssets();
            try
            {
                var parsed = new List<AssetInfo>();
                if (!string.IsNullOrEmpty(json))
                {
                    var ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = 8 * 1024 * 1024;
                    var root = ser.Deserialize<Dictionary<string, object>>(json);
                    if (root == null) throw new Exception("返回内容为空。");
                    if (root.ContainsKey("message"))
                        throw new Exception("GitHub API: " + Convert.ToString(root["message"]));

                    // 注意：JavaScriptSerializer 把 JSON 数组解成 ArrayList，不是 object[]，
                    // 按 object[] 转会得到 null，附件列表永远是空的。
                    System.Collections.IEnumerable rawAssets = null;
                    if (root.ContainsKey("assets")) rawAssets = root["assets"] as System.Collections.IEnumerable;
                    if (rawAssets != null)
                    {
                        foreach (object item in rawAssets)
                        {
                            var map = item as Dictionary<string, object>;
                            if (map == null) continue;
                            var info = new AssetInfo();
                            info.Name = map.ContainsKey("name") ? Convert.ToString(map["name"]) : "";
                            info.Url = map.ContainsKey("browser_download_url") ? Convert.ToString(map["browser_download_url"]) : "";
                            info.Size = map.ContainsKey("size") ? Convert.ToInt64(map["size"]) : 0L;
                            info.Downloads = map.ContainsKey("download_count") ? Convert.ToInt32(map["download_count"]) : 0;
                            if (string.IsNullOrEmpty(info.Name) || string.IsNullOrEmpty(info.Url)) continue;
                            info.Score = ScoreAsset(info.Name);
                            parsed.Add(info);
                        }
                    }
                }

                if (parsed.Count == 0)
                {
                    assetTitle.Text = "Release 附件 · 没有附件";
                    SetStatus("这个 Release 没有附件。想拿源码就贴 owner/repo，会打包下载仓库。", App.Muted);
                    return;
                }

                parsed.Sort(delegate(AssetInfo a, AssetInfo b) { return b.Score.CompareTo(a.Score); });
                App.LogLine("assets " + parsed.Count);

                int y = 0;
                int best = parsed[0].Score;
                bool marked = false;
                int rowWidth = Math.Max(App.S(120), assetPanel.ClientSize.Width - App.S(20));
                foreach (var info in parsed)
                {
                    var row = new AssetRow();
                    row.TitleText = info.Name;
                    row.MetaText = FormatSize(info.Size) + "   ·   下载 " + info.Downloads + " 次";
                    row.Url = info.Url;
                    bool isBest = !marked && best >= 60 && info.Score == best;
                    row.Recommended = isBest;
                    if (isBest) marked = true;
                    row.SetBounds(0, y, rowWidth, App.S(52));
                    row.Click += delegate(object s, EventArgs e)
                    {
                        var target = (AssetRow)s;
                        target.Checked = !target.Checked;
                        SelectRow(target);
                        UpdateDownloadState();
                    };
                    row.DoubleClick += delegate(object s, EventArgs e)
                    {
                        var target = (AssetRow)s;
                        target.Checked = true;
                        SelectRow(target);
                        StartDownload();
                    };
                    assetPanel.Controls.Add(row);
                    rows.Add(row);
                    y += App.S(58);
                }

                assetTitle.Text = "Release 附件 · " + parsed.Count + " 个";

                // 只有最高分那一个算推荐（和列表里的徽标保持一致），
                // 不能写成「分数≥60 就标」，否则 Windows 系的两个附件都会被标上。
                int topScore = parsed[0].Score;
                string topName = topScore >= 60 ? parsed[0].Name : null;
                AppendLog("发现 " + parsed.Count + " 个附件：");
                foreach (var info in parsed)
                    AppendLog("  · " + info.Name + "  " + FormatSize(info.Size) +
                        "  下载 " + info.Downloads + " 次" +
                        (topName != null && info.Name == topName ? "  [推荐]" : ""));
                var recommended = rows.Find(delegate(AssetRow r) { return r.Recommended; });
                SelectRow(recommended ?? rows[0]);
                var pre = recommended ?? rows[0];
                pre.Checked = true;
                pre.Invalidate();
                UpdateDownloadState();
                SetStatus("已按这台电脑标出推荐项，可以勾选多个一起下。", App.Ok);

                // --auto：解析出附件后立刻按推荐项开下
                if (App.AutoStart)
                {
                    App.AutoStart = false;
                    StartDownload();
                }
            }
            catch (Exception ex)
            {
                assetTitle.Text = "Release 附件 · 解析失败";
                SetStatus("解析失败：" + ex.Message, App.Err);
                AppendLog("[解析失败] " + ex.Message);
                App.LogLine("show err " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void SelectRow(AssetRow row)
        {
            if (row == null) return;
            foreach (var r in rows) { r.Selected = r == row; r.Invalidate(); }
            selectedRow = row;
            // 以前这里会把输入框改写成资产直链，导致想换个 tag 重解析时输入框已被污染。
            UpdateDownloadState();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        private static int ScoreAsset(string name)
        {
            if (string.IsNullOrEmpty(name)) return -100;
            string n = name.ToLowerInvariant();
            int score = 0;

            if (n.Contains("windows") || n.Contains("win32") || n.Contains("win64") || n.Contains("win-x") ||
                n.EndsWith(".exe") || n.EndsWith(".msi")) score += 45;
            if (n.EndsWith(".exe") || n.EndsWith(".msi")) score += 20;
            if (n.Contains("setup") || n.Contains("installer") || n.Contains("install")) score += 8;
            if (n.Contains("portable")) score += 6;
            if (n.Contains("x64") || n.Contains("amd64") || n.Contains("x86_64") || n.Contains("64-bit") || n.Contains("64bit")) score += 26;
            if (n.Contains("arm64") || n.Contains("aarch64") || n.Contains("armv")) score -= 45;
            if (n.Contains("linux") || n.Contains("ubuntu") || n.Contains("debian") || n.Contains("appimage") ||
                n.EndsWith(".deb") || n.EndsWith(".rpm") || n.Contains(".tar.gz") || n.Contains(".tar.xz")) score -= 55;
            if (n.Contains("macos") || n.Contains("mac-os") || n.Contains("darwin") || n.EndsWith(".dmg") || n.EndsWith(".pkg")) score -= 55;
            if (n.Contains("source") || n.Contains("src")) score -= 30;
            if (n.EndsWith(".sha256") || n.EndsWith(".sig") || n.EndsWith(".asc") || n.EndsWith(".txt") || n.Contains("checksum")) score -= 35;
            if (n.Contains("universal")) score += 4;
            if (n.Contains("32-bit") || n.Contains("32bit") || n.Contains("i386") || n.Contains("i686")) score -= 18;
            if (n.EndsWith(".zip") && score > 0) score += 4;
            return score;
        }

        private sealed class AssetInfo
        {
            public string Name;
            public string Url;
            public long Size;
            public int Downloads;
            public int Score;
        }

        private static bool TryParseRepo(string text, out string owner, out string repo, out string tag)
        {
            owner = null; repo = null; tag = null;
            string t = text.Trim();
            var bare = System.Text.RegularExpressions.Regex.Match(t, @"^([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+)$");
            if (bare.Success) { owner = bare.Groups[1].Value; repo = bare.Groups[2].Value; return true; }

            var m = System.Text.RegularExpressions.Regex.Match(t,
                @"^https?://github\.com/([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+)(?:/releases/tag/([^/?#]+))?(?:[/?#].*)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                owner = m.Groups[1].Value;
                repo = m.Groups[2].Value;
                tag = m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? m.Groups[3].Value : null;
                return true;
            }
            return false;
        }

        private void StartDownload()
        {
            if (running != null) return;

            string outDir = outInput.Box.Text.Trim().Replace("\"", "");
            if (outDir.Length == 0) outDir = Settings.DownloadDir;

            if (!File.Exists(scriptPath))
            {
                SetStatus("找不到下载脚本：" + scriptPath, App.Err);
                AppendLog("[错误] 找不到脚本：" + scriptPath);
                return;
            }

            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { SetStatus("创建目录失败：" + ex.Message, App.Err); return; }

            // 勾选了附件就批量下，没勾就用输入框里那一条
            var targets = new List<string[]>();
            foreach (var r in rows)
                if (r.Checked && !string.IsNullOrEmpty(r.Url)) targets.Add(new string[] { r.Url, r.TitleText });
            if (targets.Count == 0)
            {
                string typed = urlInput.Box.Text.Trim();
                if (typed.Length == 0) { SetStatus("先贴一个链接。", App.Err); return; }
                targets.Add(new string[] { typed, "" });
            }

            pendingTargets.Clear();
            foreach (string[] t in targets) pendingTargets.Enqueue(t);
            pendingOutDir = outDir;
            pendingCount = targets.Count;
            doneCount = 0;
            failedCount = 0;

            logBox.Clear();
            App.LogLine("download x" + pendingCount + " -> " + outDir);
            AppendLog("保存到：" + outDir);
            AppendLog("");
            progress.SetProgress(0, 0, 0);

            RunNext();
        }

        private void RunNext()
        {
            if (pendingTargets.Count == 0) { FinishQueue(); return; }

            string[] item = pendingTargets.Dequeue();
            string target = item[0];
            string fileName = item.Length > 1 ? item[1] : "";
            SetRunning(true);
            SetStatus("启动中…", App.Muted);
            AppendLog("目标：" + (string.IsNullOrEmpty(fileName) ? target : fileName));

            try
            {
                var psi = new ProcessStartInfo("powershell.exe");
                // -Quiet 只要 ## 行；-NonInteractive 防止脚本 Read-Host 把界面挂死；-NoOpen 不弹资源管理器
                string nameArg = string.IsNullOrEmpty(fileName)
                    ? "" : " -Name \"" + fileName.Replace("\"", "").Trim() + "\"";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"" +
                    " -Target \"" + target.Replace("\"", "") + "\"" +
                    " -OutDir \"" + pendingOutDir + "\"" + nameArg +
                    " -Quiet -NonInteractive -NoOpen";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                var process = new Process();
                process.StartInfo = psi;
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) PostLine(e.Data, false);
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) PostLine(e.Data, true);
                };
                process.EnableRaisingEvents = true;
                process.Exited += delegate
                {
                    int code = 0;
                    try { code = process.ExitCode; } catch { }
                    if (!IsDisposed) BeginInvoke(new Action<int>(OnFinished), code);
                };

                running = process;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                running = null;
                failedCount++;
                SetStatus("启动失败：" + ex.Message, App.Err);
                AppendLog("[错误] " + ex.Message);
                if (pendingTargets.Count > 0) RunNext(); else FinishQueue();
            }
        }

        private void OnFinished(int exitCode)
        {
            App.LogLine("finish code=" + exitCode);
            running = null;
            if (exitCode == 0) doneCount++; else failedCount++;

            if (pendingTargets.Count > 0)
            {
                AppendLog("");
                RunNext();
                return;
            }
            FinishQueue();
        }

        private void FinishQueue()
        {
            SetRunning(false);
            progress.SetProgress(0, 0, 0);

            if (failedCount == 0)
            {
                SetStatus(pendingCount > 1
                    ? ("全部完成：" + doneCount + " 个文件，去目标文件夹验收。")
                    : "完成了，去目标文件夹验收。", App.Ok);
                AppendLog("");
                AppendLog("=== 完成 ===");
            }
            else
            {
                SetStatus("完成 " + doneCount + " 个，失败 " + failedCount + " 个，看日志。", App.Err);
                AppendLog("");
                AppendLog("=== 完成 " + doneCount + " 个，失败 " + failedCount + " 个 ===");
            }

            if (App.AutoExit)
            {
                System.Windows.Forms.Timer closer = new System.Windows.Forms.Timer();
                closer.Interval = 800;
                closer.Tick += delegate { closer.Stop(); Close(); };
                closer.Start();
            }
        }

        /// <summary>正在下载时退出会中断，先问一句。</summary>
        private void TryClose()
        {
            if (running != null)
            {
                var answer = MessageBox.Show(this,
                    "正在下载，退出会中断当前文件。确定要退出吗？",
                    "GitHub 下载器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
                CancelDownload();
            }
            Close();
        }

        private void CancelDownload()
        {
            Process process = running;
            pendingTargets.Clear();
            if (process == null) { SetRunning(false); return; }
            try
            {
                if (!process.HasExited)
                    Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + process.Id)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
            }
            catch { }
            SetStatus("正在取消……", App.Muted);
        }

        private void OpenOutput()
        {
            string dir = outInput.Box.Text.Trim().Trim('"');
            if (dir.Length == 0) dir = defaultOut;
            if (dir.Length > 3) dir = dir.TrimEnd('\\', '/');
            try
            {
                // 防手抖：800ms 内的重复触发只算一次
                if ((DateTime.UtcNow - lastOpenUtc).TotalMilliseconds < 800)
                {
                    App.LogLine("open skipped (debounce)");
                    return;
                }
                lastOpenUtc = DateTime.UtcNow;

                if (!Directory.Exists(dir))
                {
                    SetStatus("这个目录还不存在：" + dir, App.Err);
                    return;
                }

                App.LogLine("open " + dir);
                // 不要用 Process.Start("explorer.exe", "\"路径\"")：
                // 那是「启动 explorer 进程」+「让它导航」，某些系统上会开出两个窗口。
                var psi = new ProcessStartInfo(dir);
                psi.UseShellExecute = true;
                psi.Verb = "open";
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                App.LogLine("open err " + ex.Message);
                SetStatus("打不开：" + ex.Message, App.Err);
            }
        }

        private void SetRunning(bool busy)
        {
            pasteButton.Enabled = !busy;
            resolveButton.Enabled = !busy;
            browseButton.Enabled = !busy;
            urlInput.Box.Enabled = !busy;
            outInput.Box.Enabled = !busy;
            cancelButton.Enabled = busy;
            progress.Visible = busy;
            downloadButton.Text = busy ? "下载中…" : "开始下载";
            UpdateDownloadState();
        }

        private void PostLine(string line, bool isError)
        {
            if (IsDisposed) return;
            try { BeginInvoke(new Action<string, bool>(OnScriptLine), line, isError); }
            catch (InvalidOperationException) { }
        }

        private void OnScriptLine(string line, bool isError)
        {
            if (IsDisposed) return;

            // 脚本 -> 界面的机器可读协议
            if (line.StartsWith("##PROGRESS "))
            {
                string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                long done, total, speed;
                if (parts.Length >= 4 &&
                    long.TryParse(parts[1], out done) &&
                    long.TryParse(parts[2], out total) &&
                    long.TryParse(parts[3], out speed))
                {
                    UpdateProgressUi(done, total, speed);
                }
                return;
            }
            if (line.StartsWith("##DONE "))
            {
                AppendLog("[完成] " + line.Substring(7));
                return;
            }
            if (line.StartsWith("##FAIL "))
            {
                AppendLog("[失败] " + line.Substring(7));
                return;
            }

            AppendLog(isError ? ("[stderr] " + line) : line);
        }

        /// <summary>日志框和自绘滚动条的位置分配（右侧留出一条给滚动条）。</summary>
        private void LayoutLogArea()
        {
            if (logHost == null || logBox == null || logScroll == null) return;
            int pad = App.S(6);
            int sw = App.S(8);
            int gap = App.S(4);
            int boxW = Math.Max(App.S(40), logHost.Width - pad * 2 - sw - gap);
            int boxH = Math.Max(App.S(20), logHost.Height - pad * 2);
            logBox.SetBounds(pad, pad, boxW, boxH);
            logScroll.SetBounds(logHost.Width - pad - sw, pad, sw, boxH);
        }

        private void SyncLogScroll()
        {
            if (logBox == null || logBox.IsDisposed || !logBox.IsHandleCreated || logScroll == null) return;
            int total = (int)SendMessage(logBox.Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero);   // EM_GETLINECOUNT
            int first = (int)SendMessage(logBox.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero);   // EM_GETFIRSTVISIBLELINE
            int lineHeight = Math.Max(App.S(10), logBox.Font.Height);
            int visible = Math.Max(1, logBox.ClientSize.Height / lineHeight);
            logScroll.Sync(total, first, visible);

        }

        private void ScrollLogTo(int targetLine)
        {
            if (logBox == null || !logBox.IsHandleCreated) return;
            int current = (int)SendMessage(logBox.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
            SendMessage(logBox.Handle, 0x00B6, IntPtr.Zero, (IntPtr)(targetLine - current));  // EM_LINESCROLL
        }

        private void AppendLog(string line)
        {
            if (logBox.IsDisposed) return;
            logBox.AppendText(line + Environment.NewLine);
            // 追加之后必须把插入点移到末尾并滚过去，否则视图停在最上面那几行，
            // 用户会以为日志只有一条。
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }
    }
}
