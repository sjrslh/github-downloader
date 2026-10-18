using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
                File.AppendAllText(Path.Combine(dir, "app.log"),
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

        public static float Scale = 1f;

        public static int S(int value)
        {
            return (int)Math.Round(value * Scale);
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0 || r.Width <= 1 || r.Height <= 1) { path.AddRectangle(r); return path; }
            int d = Math.Min(Math.Min(radius * 2, r.Width), r.Height);
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
        public static void Main()
        {
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
                IntPtr existing = FindWindow(null, "GitHub 下载器");
                if (existing != IntPtr.Zero)
                {
                    ShowWindow(existing, 9);
                    SetForegroundWindow(existing);
                }
                return;
            }

            CloseStaleWindows();
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
            foreach (var other in Process.GetProcessesByName("GitHub下载器"))
            {
                if ((uint)other.Id == self) continue;
                try { other.Kill(); } catch { }
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
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
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

    internal class RoundInput : Panel
    {
        public readonly TextBox Box;
        public Color Surface = App.Bg;
        private bool focused;

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
            Resize += delegate { LayoutBox(); };
        }

        private void LayoutBox()
        {
            int pad = App.S(14);
            Box.SetBounds(pad, Math.Max(0, (Height - Box.PreferredHeight) / 2), Width - pad * 2, Box.PreferredHeight);
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
            var titleRect = new Rectangle(pad, App.S(7), Width - App.S(160), App.S(20));
            TextRenderer.DrawText(e.Graphics, TitleText, titleFont, titleRect, App.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var metaRect = new Rectangle(pad, App.S(27), Width - App.S(160), App.S(18));
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

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(Color.FromArgb(26, 31, 42)))
                e.Graphics.FillRectangle(track, 0, 0, Width, Height);

            int w = Math.Max(App.S(70), Width / 5);
            int x = (int)((Width + w) * offset) - w;
            var seg = new Rectangle(x, 0, w, Height);
            using (var path = App.Round(seg, Height / 2))
            using (var b = new SolidBrush(App.Accent))
                e.Graphics.FillPath(b, path);
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

        public MainForm()
        {
            scriptPath = ResolveScriptPath();
            App.LogLine("script=" + scriptPath);
            defaultOut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GitHub");

            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            Text = "GitHub 下载器 · v1.4";
            BackColor = App.Bg;
            ForeColor = App.Text;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            AllowDrop = true;
            ClientSize = new Size(App.S(900), App.S(792));
            MinimumSize = new Size(App.S(760), App.S(660));


            BuildUi();
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
                string owner, repo, tag;
                string text = urlInput.Box.Text.Trim();
                if (text.Length > 0 && TryParseRepo(text, out owner, out repo, out tag))
                    ResolveRelease();
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

            var logo = new Label();
            logo.Text = "●";
            logo.ForeColor = App.Accent;
            logo.BackColor = App.Bg;
            logo.Font = new Font("Segoe UI", 17f, FontStyle.Bold);
            logo.SetBounds(App.S(24), App.S(20), App.S(26), App.S(32));
            logo.MouseDown += OnHeaderMouseDown;
            headerPanel.Controls.Add(logo);

            var title = new Label();
            title.Text = "GitHub 下载器";
            title.Font = new Font("Microsoft YaHei UI", 14.5f, FontStyle.Bold);
            title.ForeColor = App.Text;
            title.BackColor = App.Bg;
            title.SetBounds(App.S(54), App.S(18), App.S(360), App.S(30));
            title.MouseDown += OnHeaderMouseDown;
            headerPanel.Controls.Add(title);

            var version = new Label();
            version.Text = "v1.4";
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
            subtitle.SetBounds(App.S(56), App.S(46), App.S(360), App.S(20));
            subtitle.MouseDown += OnHeaderMouseDown;
            headerPanel.Controls.Add(subtitle);

            envLabel = new Label();
            envLabel.Text = DetectEnvironment();
            envLabel.TextAlign = ContentAlignment.MiddleCenter;
            envLabel.ForeColor = App.Muted;
            envLabel.BackColor = App.Bg;
            envLabel.Font = new Font("Consolas", 8.8f);
            envLabel.SetBounds(App.S(560), App.S(26), App.S(196), App.S(28));
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

            var close = new PillButton();
            close.Text = "×";
            close.Danger = true;
            close.Font = new Font("Segoe UI", 12f, FontStyle.Regular);
            close.SetBounds(ClientSize.Width - App.S(48), App.S(22), App.S(28), App.S(28));
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Click += delegate { Close(); };
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
                    if (dialog.ShowDialog(this) == DialogResult.OK) outInput.Box.Text = dialog.SelectedPath;
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

            var logHost = new RoundPanel();
            logHost.Fill = App.LogBg;
            logHost.Radius = 10;
            logHost.SetBounds(App.S(10), App.S(10), logCard.Width - App.S(20), logCard.Height - App.S(20));
            logHost.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            logCard.Controls.Add(logHost);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = App.LogBg;
            logBox.ForeColor = Color.FromArgb(196, 204, 220);
            logBox.BorderStyle = BorderStyle.None;
            logBox.Font = new Font("Consolas", 8.8f);
            logBox.SetBounds(App.S(6), App.S(6), logHost.Width - App.S(12), logHost.Height - App.S(12));
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            logHost.Controls.Add(logBox);

            statusLabel = new Label();
            statusLabel.Text = "就绪";
            statusLabel.ForeColor = App.Muted;
            statusLabel.BackColor = App.Bg;
            statusLabel.SetBounds(App.S(24), ClientSize.Height - App.S(34), ClientSize.Width - App.S(48), App.S(22));
            statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(statusLabel);

            string clip = FirstLine(SafeClipboard());
            if (LooksLikeTarget(clip)) urlInput.Box.Text = clip;
            outInput.Box.Text = defaultOut;
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

        private void SetStatus(string text, Color color)
        {
            if (IsDisposed) return;
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
        }

        private static void Log(string line)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHub下载器");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "app.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        private void UpdateDownloadState()
        {
            downloadButton.Enabled = running == null && !string.IsNullOrWhiteSpace(urlInput.Box.Text);
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

            string api = "https://api.github.com/repos/" + owner + "/" + repo +
                (string.IsNullOrEmpty(tag) ? "/releases/latest" : "/releases/tags/" + tag);
            int token = ++resolveToken;
            Log("resolve " + api);

            var thread = new Thread(delegate()
            {
                string data = null;
                string error = null;
                try
                {
                    string script =
                        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                        "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; " +
                        "$ProgressPreference='SilentlyContinue'; " +
                        "$r=Invoke-RestMethod -Uri '" + api +
                        "' -Headers @{'User-Agent'='GitHubDownloader/2.0';'Accept'='application/vnd.github+json'} -TimeoutSec 25; " +
                        "$t=[char]9; foreach($a in $r.assets){ $a.name + $t + $a.browser_download_url + $t + $a.size + $t + $a.download_count }";
                    string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

                    var psi = new ProcessStartInfo("powershell.exe");
                    psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;

                    Log("GET " + api);
                    using (var process = Process.Start(psi))
                    {
                        data = process.StandardOutput.ReadToEnd();
                        string errText = process.StandardError.ReadToEnd();
                        process.WaitForExit();
                        if (process.ExitCode != 0)
                        {
                            string detail = string.IsNullOrWhiteSpace(errText) ? ("退出码 " + process.ExitCode) : errText.Trim();
                            if (detail.Length > 300) detail = detail.Substring(0, 300);
                            data = null;
                            error = "读取失败：" + detail;
                            Log("GET err " + detail);
                        }
                        else
                        {
                            Log("GET ok " + (data == null ? 0 : data.Length));
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = "读取失败：" + ex.Message;
                    Log("GET err " + ex.GetType().Name + " " + ex.Message);
                }

                if (IsDisposed || token != resolveToken) return;
                try
                {
                    BeginInvoke(new Action<string, string>(delegate(string body, string err)
                    {
                        if (token != resolveToken) return;
                        if (err != null) { assetTitle.Text = "Release 附件 · 读取失败"; SetStatus(err, App.Err); return; }
                        ShowAssets(body);
                    }), data, error);
                }
                catch (InvalidOperationException) { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void ClearAssets()
        {
            foreach (var row in rows) row.Dispose();
            rows.Clear();
            selectedRow = null;
            assetPanel.Controls.Clear();
        }

        private void ShowAssets(string data)
        {
            ClearAssets();
            try
            {
                var parsed = new List<AssetInfo>();
                if (!string.IsNullOrEmpty(data))
                {
                    foreach (string raw in data.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        if (line.Length == 0) continue;
                        string[] parts = line.Split('\t');
                        if (parts.Length < 4) continue;
                        var info = new AssetInfo();
                        info.Name = parts[0].Trim();
                        info.Url = parts[1].Trim();
                        long size;
                        long.TryParse(parts[2].Trim(), out size);
                        info.Size = size;
                        int downloads;
                        int.TryParse(parts[3].Trim(), out downloads);
                        info.Downloads = downloads;
                        if (info.Name.Length == 0 || info.Url.Length == 0) continue;
                        info.Score = ScoreAsset(info.Name);
                        parsed.Add(info);
                    }
                }

                if (parsed.Count == 0)
                {
                    assetTitle.Text = "Release 附件 · 没有附件";
                    SetStatus("这个 Release 没有附件。", App.Muted);
                    return;
                }

                parsed.Sort(delegate(AssetInfo a, AssetInfo b) { return b.Score.CompareTo(a.Score); });
                Log("assets " + parsed.Count);

                int y = 0;
                int best = parsed[0].Score;
                bool marked = false;
                foreach (var info in parsed)
                {
                    var row = new AssetRow();
                    row.TitleText = info.Name;
                    row.MetaText = FormatSize(info.Size) + "   ·   下载 " + info.Downloads + " 次";
                    row.Url = info.Url;
                    bool isBest = !marked && best >= 60 && info.Score == best;
                    row.Recommended = isBest;
                    if (isBest) marked = true;
                    row.SetBounds(0, y, assetPanel.ClientSize.Width - App.S(20), App.S(52));
                    row.Click += delegate(object s, EventArgs e) { SelectRow((AssetRow)s); };
                    row.DoubleClick += delegate(object s, EventArgs e)
                    {
                        SelectRow((AssetRow)s);
                        StartDownload();
                    };
                    assetPanel.Controls.Add(row);
                    rows.Add(row);
                    y += App.S(58);
                }

                assetTitle.Text = "Release 附件 · " + parsed.Count + " 个";
                var recommended = rows.Find(delegate(AssetRow r) { return r.Recommended; });
                SelectRow(recommended ?? rows[0]);
                SetStatus("挑好了，推荐项已经按这台电脑标出来了。", App.Ok);
            }
            catch (Exception ex)
            {
                assetTitle.Text = "Release 附件 · 解析失败";
                SetStatus("解析失败：" + ex.Message, App.Err);
                Log("show err " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void SelectRow(AssetRow row)
        {
            if (row == null) return;
            foreach (var r in rows) { r.Selected = r == row; r.Invalidate(); }
            selectedRow = row;
            urlInput.Box.Text = row.Url;
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

            string target = urlInput.Box.Text.Trim();
            string outDir = outInput.Box.Text.Trim();
            if (target.Length == 0) { SetStatus("先贴一个链接。", App.Err); return; }
            if (outDir.Length == 0) outDir = defaultOut;

            if (!File.Exists(scriptPath))
            {
                SetStatus("找不到下载脚本：" + scriptPath, App.Err);
                AppendLog("[错误] 找不到脚本：" + scriptPath);
                return;
            }

            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { SetStatus("创建目录失败：" + ex.Message, App.Err); return; }

            logBox.Clear();
            AppendLog("目标：" + target);
            AppendLog("保存到：" + outDir);
            AppendLog("");

            try
            {
                var psi = new ProcessStartInfo("powershell.exe");
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -Target \"" +
                    target.Replace("\"", "") + "\" -OutDir \"" + outDir + "\"";
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
                    if (e.Data != null) PostLog(e.Data);
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) PostLog("[stderr] " + e.Data);
                };
                process.EnableRaisingEvents = true;
                process.Exited += delegate
                {
                    int code = 0;
                    try { code = process.ExitCode; } catch { }
                    if (!IsDisposed) BeginInvoke(new Action<int>(OnFinished), code);
                };

                running = process;
                SetRunning(true);
                SetStatus("下载中……", App.Muted);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                running = null;
                SetRunning(false);
                SetStatus("启动失败：" + ex.Message, App.Err);
                AppendLog("[错误] " + ex.Message);
            }
        }

        private void OnFinished(int exitCode)
        {
            running = null;
            SetRunning(false);
            if (exitCode == 0)
            {
                SetStatus("完成了，去目标文件夹验收。", App.Ok);
                AppendLog("");
                AppendLog("=== 完成 ===");
            }
            else
            {
                SetStatus("结束了，退出码 " + exitCode + "，看日志。", App.Err);
                AppendLog("");
                AppendLog("=== 退出码 " + exitCode + " ===");
            }
        }

        private void CancelDownload()
        {
            Process process = running;
            if (process == null) return;
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
            string dir = outInput.Box.Text.Trim();
            if (dir.Length == 0) dir = defaultOut;
            try
            {
                if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
                else SetStatus("这个目录还不存在：" + dir, App.Err);
            }
            catch (Exception ex) { SetStatus("打不开：" + ex.Message, App.Err); }
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

        private void PostLog(string line)
        {
            if (IsDisposed) return;
            try { BeginInvoke(new Action<string>(AppendLog), line); }
            catch (InvalidOperationException) { }
        }

        private void AppendLog(string line)
        {
            if (logBox.IsDisposed) return;
            logBox.AppendText(line + Environment.NewLine);
        }
    }
}
