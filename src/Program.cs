using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WindowSizeKeeper
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);
    }

    class Track { public IntPtr Mon; public double Wmm; public double Hmm; }
    class Pending { public IntPtr H; public double Wmm; public double Hmm; public DateTime Due; }

    class HotkeyWindow : NativeWindow
    {
        public const int WM_HOTKEY = 0x0312;
        public Action<int> OnHotkey;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && OnHotkey != null)
                OnHotkey(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    class Tray : ApplicationContext
    {
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_NOREPEAT = 0x4000;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const double DEFAULT_W_MM = 240.0;
        const double DEFAULT_H_MM = 150.0;

        readonly string _dir;
        readonly string _cfg;
        readonly string _log;

        NotifyIcon _icon;
        HotkeyWindow _win;
        Timer _timer;
        bool _auto = true;
        Dictionary<IntPtr, Track> _tracks = new Dictionary<IntPtr, Track>();
        Dictionary<string, double[]> _sizes = new Dictionary<string, double[]>();
        List<Pending> _pending = new List<Pending>();
        ToolStripMenuItem _autoItem;

        public Tray()
        {
            _dir = Path.GetDirectoryName(Application.ExecutablePath);
            _cfg = Path.Combine(_dir, "sizes.txt");
            _log = Path.Combine(_dir, "log.txt");
            try { File.WriteAllText(_log, ""); } catch { }

            LoadSizes();
            BuildTray();
            RegisterAll();
            RefreshStartupPath();

            _timer = new Timer();
            _timer.Interval = 250;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        void Log(string s)
        {
            try { File.AppendAllText(_log, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            _autoItem = new ToolStripMenuItem("跨屏自动保持尺寸  (Ctrl+Alt+K)", null, delegate { ToggleAuto(); });
            _autoItem.Checked = _auto;
            menu.Items.Add(_autoItem);
            menu.Items.Add(new ToolStripMenuItem("立即把当前窗口设为记录尺寸  (Ctrl+Alt+S)", null, delegate { Snap(); }));
            menu.Items.Add(new ToolStripMenuItem("把当前尺寸记为该软件的基准  (Ctrl+Alt+D)", null, delegate { Define(); }));
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem startup = new ToolStripMenuItem("开机自动启动", null, delegate { ToggleStartup(); });
            startup.Checked = IsStartupEnabled();
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripMenuItem("热键说明", null, delegate { Info(); }));
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { Exit(); }));

            _icon = new NotifyIcon();
            try
            {
                Icon own = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                _icon.Icon = own != null ? own : SystemIcons.Application;
            }
            catch { _icon.Icon = SystemIcons.Application; }
            _icon.Text = "窗口尺寸助手";
            _icon.ContextMenuStrip = menu;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { Info(); };
        }

        void RegisterAll()
        {
            _win = new HotkeyWindow();
            _win.OnHotkey = OnHotkey;
            bool a = Native.RegisterHotKey(_win.Handle, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x53); // S
            bool b = Native.RegisterHotKey(_win.Handle, 2, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x44); // D
            bool c = Native.RegisterHotKey(_win.Handle, 3, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4B); // K
            bool d = Native.RegisterHotKey(_win.Handle, 4, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x51); // Q
            Log("hotkeys S=" + a + " D=" + b + " K=" + c + " Q=" + d);
            if (!a || !b || !c || !d)
            {
                _icon.ShowBalloonTip(5000, "窗口尺寸助手",
                    "有热键被其他程序占用（常见：QQ 的截图热键）。\n请查看 log.txt 或右键托盘图标使用菜单操作。",
                    ToolTipIcon.Warning);
            }
        }

        void OnHotkey(int id)
        {
            if (id == 1) Snap();
            else if (id == 2) Define();
            else if (id == 3) ToggleAuto();
            else if (id == 4) Exit();
        }

        void ToggleAuto()
        {
            _auto = !_auto;
            if (_autoItem != null) _autoItem.Checked = _auto;
            Log("auto=" + _auto);
            _icon.ShowBalloonTip(1500, "窗口尺寸助手", _auto ? "跨屏自动保持尺寸：开" : "跨屏自动保持尺寸：关", ToolTipIcon.Info);
        }

        void Info()
        {
            _icon.ShowBalloonTip(6000, "窗口尺寸助手",
                "Ctrl+Alt+S  设为记录尺寸\nCtrl+Alt+D  记录当前尺寸\nCtrl+Alt+K  开关自动保持\nCtrl+Alt+Q  退出",
                ToolTipIcon.Info);
        }

        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RUN_NAME = "WindowSizeKeeper";

        static bool IsStartupEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RUN_NAME) != null;
                }
            }
            catch { return false; }
        }

        void ToggleStartup()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (k == null) return;
                    if (IsStartupEnabled()) k.DeleteValue(RUN_NAME, false);
                    else k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"");
                }
                _icon.ShowBalloonTip(1500, "窗口尺寸助手", IsStartupEnabled() ? "已加入开机启动" : "已取消开机启动", ToolTipIcon.Info);
            }
            catch (Exception ex) { Log("startup toggle failed: " + ex.Message); }
        }

        // keep the autostart entry pointing at this file even if the folder was moved
        void RefreshStartupPath()
        {
            try
            {
                if (!IsStartupEnabled()) return;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (k == null) return;
                    string want = "\"" + Application.ExecutablePath + "\"";
                    object cur = k.GetValue(RUN_NAME);
                    if (cur == null || cur.ToString() != want) k.SetValue(RUN_NAME, want);
                }
            }
            catch { }
        }

        // ---------- helpers ----------

        static string ProcName(IntPtr h)
        {
            try
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        static IntPtr Target()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = Native.GetAncestor(h, 2); // GA_ROOT
            if (root != IntPtr.Zero) h = root;
            if (Native.IsIconic(h) || Native.IsZoomed(h)) return IntPtr.Zero;
            return h;
        }

        void ApplySize(IntPtr h, double wmm, double hmm, bool onlyIfNeeded)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            uint dpi = Native.GetDpiForWindow(h);
            if (dpi == 0) dpi = 96;

            int w = (int)Math.Round(wmm * dpi / 25.4);
            int hh = (int)Math.Round(hmm * dpi / 25.4);
            if (w < 120 || hh < 80) return;

            int curW = r.Right - r.Left;
            int curH = r.Bottom - r.Top;
            if (onlyIfNeeded)
            {
                int tolW = Math.Max(4, w / 40);
                int tolH = Math.Max(4, hh / 40);
                if (Math.Abs(curW - w) <= tolW && Math.Abs(curH - hh) <= tolH) return;
            }
            Native.SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, w, hh, SWP_NOZORDER | SWP_NOACTIVATE);
            Log("apply " + ProcName(h) + " -> " + w + "x" + hh + "px @dpi" + dpi);
        }

        void Snap()
        {
            IntPtr h = Target();
            if (h == IntPtr.Zero) return;
            double[] s;
            if (!_sizes.TryGetValue(ProcName(h), out s)) { s = new double[] { DEFAULT_W_MM, DEFAULT_H_MM }; }
            ApplySize(h, s[0], s[1], false);
        }

        void Define()
        {
            IntPtr h = Target();
            if (h == IntPtr.Zero) return;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            uint dpi = Native.GetDpiForWindow(h);
            if (dpi == 0) dpi = 96;
            double wmm = (r.Right - r.Left) * 25.4 / dpi;
            double hmm = (r.Bottom - r.Top) * 25.4 / dpi;
            string p = ProcName(h);
            _sizes[p] = new double[] { wmm, hmm };
            SaveSizes();
            Log("define " + p + " = " + Math.Round(wmm) + "x" + Math.Round(hmm) + "mm");
            _icon.ShowBalloonTip(1500, "已记录", p + "  " + Math.Round(wmm) + " × " + Math.Round(hmm) + " mm", ToolTipIcon.Info);
        }

        void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now >= _pending[i].Due)
                {
                    ApplySize(_pending[i].H, _pending[i].Wmm, _pending[i].Hmm, true);
                    _pending.RemoveAt(i);
                }
            }

            List<IntPtr> wins = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (h == _win.Handle) return true;
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, 4) != IntPtr.Zero) return true;                 // skip owned windows
                if ((Native.GetWindowLong(h, -20) & 0x80) != 0) return true;            // skip WS_EX_TOOLWINDOW
                if (Native.GetWindowTextLength(h) == 0) return true;                    // skip untitled windows
                if (Native.IsIconic(h) || Native.IsZoomed(h)) return true;
                wins.Add(h);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr h in wins) TrackWindow(h, now);

            if (_tracks.Count > 400) _tracks.Clear();
        }

        void TrackWindow(IntPtr h, DateTime now)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            int w = r.Right - r.Left;
            int hh = r.Bottom - r.Top;
            if (w < 120 || hh < 80) return;

            uint dpi = Native.GetDpiForWindow(h);
            if (dpi == 0) dpi = 96;
            IntPtr mon = Native.MonitorFromWindow(h, 2);
            double wmm = w * 25.4 / dpi;
            double hmm = hh * 25.4 / dpi;

            Track t;
            if (_tracks.TryGetValue(h, out t))
            {
                if (t.Mon != mon)
                {
                    if (_auto)
                    {
                        // the app already re-laid-out at the new DPI; put the physical size back
                        _pending.Add(new Pending { H = h, Wmm = t.Wmm, Hmm = t.Hmm, Due = now.AddMilliseconds(350) });
                        _pending.Add(new Pending { H = h, Wmm = t.Wmm, Hmm = t.Hmm, Due = now.AddMilliseconds(900) });
                    }
                    t.Mon = mon; t.Wmm = wmm; t.Hmm = hmm;
                }
                else
                {
                    t.Wmm = wmm; t.Hmm = hmm;
                }
            }
            else
            {
                _tracks[h] = new Track { Mon = mon, Wmm = wmm, Hmm = hmm };
            }
        }

        // ---------- config ----------

        void LoadSizes()
        {
            try
            {
                if (!File.Exists(_cfg)) return;
                foreach (string line in File.ReadAllLines(_cfg, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split('=');
                    if (p.Length != 2) continue;
                    string[] v = p[1].Split(',');
                    if (v.Length != 2) continue;
                    double a, b;
                    if (!double.TryParse(v[0], out a)) continue;
                    if (!double.TryParse(v[1], out b)) continue;
                    _sizes[p[0].Trim().ToLowerInvariant()] = new double[] { a, b };
                }
            }
            catch { }
        }

        void SaveSizes()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# process=width_mm,height_mm");
                foreach (KeyValuePair<string, double[]> kv in _sizes)
                    lines.Add(kv.Key + "=" + Math.Round(kv.Value[0], 1) + "," + Math.Round(kv.Value[1], 1));
                File.WriteAllLines(_cfg, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        void Exit()
        {
            try { _timer.Stop(); } catch { }
            try { Native.UnregisterHotKey(_win.Handle, 1); Native.UnregisterHotKey(_win.Handle, 2); Native.UnregisterHotKey(_win.Handle, 3); Native.UnregisterHotKey(_win.Handle, 4); } catch { }
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            ExitThread();
        }
    }

    static class Program
    {
        static System.Threading.Mutex _singleInstance;

        [STAThread]
        static void Main()
        {
            bool created;
            _singleInstance = new System.Threading.Mutex(true, @"Local\WindowSizeKeeper.SingleInstance", out created);
            if (!created) return; // already running, stay silent

            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            Application.EnableVisualStyles();
            Application.Run(new Tray());
        }
    }
}
