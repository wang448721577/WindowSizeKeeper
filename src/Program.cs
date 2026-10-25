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

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
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
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    }

    // A monitor plus its work area and effective DPI, cached briefly.
    class Mon
    {
        public IntPtr Handle;
        public Native.RECT Work;
        public uint Dpi;
    }

    static class Mons
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;

        public static Mon At(Native.POINT pt)
        {
            IntPtr h = Native.MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            return Of(h);
        }

        public static Mon Of(IntPtr handle)
        {
            Mon m = new Mon();
            m.Handle = handle;
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (Native.GetMonitorInfo(handle, ref mi)) m.Work = mi.rcWork;
            else { m.Work.Left = 0; m.Work.Top = 0; m.Work.Right = 1024; m.Work.Bottom = 768; }

            uint dx, dy;
            uint dpi = 96;
            try
            {
                if (Native.GetDpiForMonitor(handle, 0, out dx, out dy) == 0 && dx > 0) dpi = dx;
            }
            catch { }
            m.Dpi = dpi;
            return m;
        }

        // monitors ordered left to right, so index-1 is "the screen on the left"
        public static List<Mon> Ordered()
        {
            List<Mon> list = new List<Mon>(4);
            Dictionary<IntPtr, bool> seen = new Dictionary<IntPtr, bool>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate(IntPtr hmon, IntPtr hdc, ref Native.RECT rc, IntPtr data)
                {
                    if (!seen.ContainsKey(hmon))
                    {
                        seen[hmon] = true;
                        list.Add(Of(hmon));
                    }
                    return true;
                }, IntPtr.Zero);

            list.Sort(delegate(Mon a, Mon b) { return a.Work.Left.CompareTo(b.Work.Left); });
            return list;
        }

        public static int IndexOf(List<Mon> list, IntPtr handle)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Handle == handle) return i;
            return -1;
        }
    }

    class Track
    {
        public IntPtr Mon;
        public double Wmm;          // the size the user wants, in millimetres
        public double Hmm;
        public int AppliedW;        // last pixel size this tool wrote (0 = unknown)
        public int AppliedH;
        public DateTime Seen;
    }

    class Pending
    {
        public IntPtr H;
        public double Wmm;
        public double Hmm;
        public DateTime Due;
    }

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
            if (m.Msg == WM_HOTKEY && OnHotkey != null) OnHotkey(m.WParam.ToInt32());
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
        const uint SWP_ASYNCWINDOWPOS = 0x4000;   // never block on a busy app
        const double DEFAULT_W_MM = 240.0;
        const double DEFAULT_H_MM = 150.0;

        readonly string _dir;
        readonly string _cfg;
        readonly string _log;
        readonly uint _ownPid;

        NotifyIcon _icon;
        Icon _ownIcon;
        HotkeyWindow _win;
        Timer _timer;
        bool _auto = true;
        Dictionary<IntPtr, Track> _tracks = new Dictionary<IntPtr, Track>();
        Dictionary<string, double[]> _sizes = new Dictionary<string, double[]>();
        List<Pending> _pending = new List<Pending>();
        List<IntPtr> _buf = new List<IntPtr>(256);
        Native.EnumWindowsProc _enumProc;
        ToolStripMenuItem _autoItem;
        int _tick;

        public Tray()
        {
            using (System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess()) _ownPid = (uint)me.Id;
            _dir = Path.GetDirectoryName(Application.ExecutablePath);
            _cfg = Path.Combine(_dir, "sizes.txt");
            _log = Path.Combine(_dir, "log.txt");
            try { File.WriteAllText(_log, ""); } catch { }

            _enumProc = CollectWindow;
            LoadSizes();
            BuildTray();
            RegisterAll();
            RefreshStartupPath();

            _timer = new Timer();
            _timer.Interval = 400;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        void Log(string s)
        {
            try { File.AppendAllText(_log, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        // ---------- tray ----------
        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            _autoItem = new ToolStripMenuItem("跨屏自动保持尺寸  (Ctrl+Alt+K)", null, delegate { ToggleAuto(); });
            _autoItem.Checked = _auto;
            menu.Items.Add(_autoItem);
            menu.Items.Add(new ToolStripMenuItem("立即把当前窗口设为记录尺寸  (Ctrl+Alt+S)", null, delegate { Snap(); }));
            menu.Items.Add(new ToolStripMenuItem("把当前尺寸记为该软件的基准  (Ctrl+Alt+D)", null, delegate { Define(); }));

            ToolStripMenuItem tools = new ToolStripMenuItem("窗口工具（当前窗口）");
            tools.DropDownItems.Add(new ToolStripMenuItem("在本屏居中  (Ctrl+Alt+C)", null, delegate { CenterOn(Target(), 0); }));
            tools.DropDownItems.Add(new ToolStripMenuItem("移到左屏并居中", null, delegate { CenterOn(Target(), -1); }));
            tools.DropDownItems.Add(new ToolStripMenuItem("移到右屏并居中", null, delegate { CenterOn(Target(), 1); }));
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(new ToolStripMenuItem("适应本屏大小", null, delegate { FitTo(Target()); }));
            menu.Items.Add(tools);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem startup = new ToolStripMenuItem("开机自动启动", null, delegate { ToggleStartup(); });
            startup.Checked = IsStartupEnabled();
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripMenuItem("热键说明", null, delegate { Info(); }));
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { Exit(); }));

            _icon = new NotifyIcon();
            try
            {
                _ownIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                _icon.Icon = _ownIcon != null ? _ownIcon : SystemIcons.Application;
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
            bool e = Native.RegisterHotKey(_win.Handle, 5, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x43); // C
            Log("hotkeys S=" + a + " D=" + b + " K=" + c + " Q=" + d + " C=" + e);
            if (!a || !b || !c || !d || !e)
            {
                _icon.ShowBalloonTip(5000, "窗口尺寸助手",
                    "有热键被其他程序占用（常见：QQ 的截图热键）。\n请查看 log.txt 或右键托盘图标使用菜单操作。",
                    ToolTipIcon.Warning);
            }
        }

        // ---------- window actions ----------

        static IntPtr Target()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = Native.GetAncestor(h, 2);
            if (root != IntPtr.Zero) h = root;
            if (Native.IsIconic(h) || Native.IsZoomed(h)) return IntPtr.Zero;
            return h;
        }

        // The anchor is near the left end of the title bar. It does not move when
        // we change the width, so the monitor decision cannot flip-flop the way
        // "largest intersecting area" does when a window straddles two screens.
        static Native.POINT Anchor(Native.RECT r)
        {
            Native.POINT p;
            p.X = r.Left + 24;
            p.Y = r.Top + 8;
            return p;
        }

        static int Px(double mm, uint dpi)
        {
            return (int)Math.Round(mm * dpi / 25.4);
        }

        static int Tol(int v) { return Math.Max(4, v / 40); }

        // shrink to the work area, never below a usable minimum
        static bool ClampToWork(ref int w, ref int h, Native.RECT work)
        {
            bool changed = false;
            int maxW = Math.Max(200, work.Right - work.Left);
            int maxH = Math.Max(150, work.Bottom - work.Top);
            if (w > maxW) { w = maxW; changed = true; }
            if (h > maxH) { h = maxH; changed = true; }
            if (w < 120) { w = 120; changed = true; }
            if (h < 80) { h = 80; changed = true; }
            return changed;
        }

        static void MoveInside(ref int x, ref int y, int w, int h, Native.RECT work)
        {
            if (x + w > work.Right) x = work.Right - w;
            if (y + h > work.Bottom) y = work.Bottom - h;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;
        }

        bool ApplySize(IntPtr h, double wmm, double hmm, bool onlyIfNeeded, bool clamp)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return false;

            Mon mon = Mons.At(Anchor(r));
            uint dpi = mon.Dpi;

            int w = Px(wmm, dpi);
            int hh = Px(hmm, dpi);
            if (w < 120 || hh < 80) return false;

            bool fitted = clamp && ClampToWork(ref w, ref hh, mon.Work);

            int curW = r.Right - r.Left;
            int curH = r.Bottom - r.Top;
            if (onlyIfNeeded && Math.Abs(curW - w) <= Tol(w) && Math.Abs(curH - hh) <= Tol(hh)) return false;

            int x = r.Left;
            int y = r.Top;
            if (fitted) MoveInside(ref x, ref y, w, hh, mon.Work);

            Native.SetWindowPos(h, IntPtr.Zero, x, y, w, hh, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

            Track t;
            if (_tracks.TryGetValue(h, out t))
            {
                t.AppliedW = w;
                t.AppliedH = hh;
                t.Mon = mon.Handle;
            }
            Log("apply " + ProcName(h) + " -> " + w + "x" + hh + "px @dpi" + dpi + (fitted ? " fit" : ""));
            return true;
        }

        void CenterOn(IntPtr h, int offset)
        {
            if (h == IntPtr.Zero) return;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;

            List<Mon> all = Mons.Ordered();
            if (all.Count == 0) return;

            Mon cur = Mons.At(Anchor(r));
            int idx = Mons.IndexOf(all, cur.Handle);
            if (idx < 0) idx = 0;
            idx += offset;
            if (idx < 0) idx = 0;
            if (idx >= all.Count) idx = all.Count - 1;

            Mon target = all[idx];
            int w = r.Right - r.Left;
            int hh = r.Bottom - r.Top;
            ClampToWork(ref w, ref hh, target.Work);

            int x = target.Work.Left + ((target.Work.Right - target.Work.Left) - w) / 2;
            int y = target.Work.Top + ((target.Work.Bottom - target.Work.Top) - hh) / 2;

            Native.SetWindowPos(h, IntPtr.Zero, x, y, w, hh, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

            double wmm = w * 25.4 / target.Dpi;
            double hmm = hh * 25.4 / target.Dpi;
            _tracks[h] = new Track { Mon = target.Handle, Wmm = wmm, Hmm = hmm, AppliedW = w, AppliedH = hh, Seen = DateTime.Now };
            Log("center " + ProcName(h) + " -> monitor#" + idx + " " + x + "," + y + " " + w + "x" + hh);
        }

        void FitTo(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            Mon mon = Mons.At(Anchor(r));
            int w = r.Right - r.Left;
            int hh = r.Bottom - r.Top;
            ClampToWork(ref w, ref hh, mon.Work);
            int x = r.Left, y = r.Top;
            MoveInside(ref x, ref y, w, hh, mon.Work);
            Native.SetWindowPos(h, IntPtr.Zero, x, y, w, hh, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            _tracks[h] = new Track
            {
                Mon = mon.Handle,
                Wmm = w * 25.4 / mon.Dpi,
                Hmm = hh * 25.4 / mon.Dpi,
                AppliedW = w,
                AppliedH = hh,
                Seen = DateTime.Now
            };
            Log("fit " + ProcName(h) + " -> " + w + "x" + hh);
        }

        void Snap()
        {
            IntPtr h = Target();
            if (h == IntPtr.Zero) return;
            double[] s;
            if (!_sizes.TryGetValue(ProcName(h), out s)) { s = new double[] { DEFAULT_W_MM, DEFAULT_H_MM }; }
            ApplySize(h, s[0], s[1], false, true);
        }

        void Define() { DefineFor(Target()); }

        void DefineFor(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            Mon mon = Mons.At(Anchor(r));
            double wmm = (r.Right - r.Left) * 25.4 / mon.Dpi;
            double hmm = (r.Bottom - r.Top) * 25.4 / mon.Dpi;
            string p = ProcName(h);
            _sizes[p] = new double[] { wmm, hmm };
            SaveSizes();
            _tracks[h] = new Track
            {
                Mon = mon.Handle,
                Wmm = wmm,
                Hmm = hmm,
                AppliedW = r.Right - r.Left,
                AppliedH = r.Bottom - r.Top,
                Seen = DateTime.Now
            };
            Log("define " + p + " = " + Math.Round(wmm) + "x" + Math.Round(hmm) + "mm");
            _icon.ShowBalloonTip(1500, "已记录", p + "  " + Math.Round(wmm) + " × " + Math.Round(hmm) + " mm", ToolTipIcon.Info);
        }

        void OnHotkey(int id)
        {
            if (id == 1) Snap();
            else if (id == 2) Define();
            else if (id == 3) ToggleAuto();
            else if (id == 4) Exit();
            else if (id == 5) CenterOn(Target(), 0);
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
            _icon.ShowBalloonTip(7000, "窗口尺寸助手",
                "Ctrl+Alt+S  设为记录尺寸\nCtrl+Alt+D  记录当前尺寸\nCtrl+Alt+C  本屏居中\nCtrl+Alt+K  开关自动保持\nCtrl+Alt+Q  退出\n\n右键托盘图标可打开完整菜单",
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

        // ---------- tracking ----------

        static string ProcName(IntPtr h)
        {
            try
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid))
                    return p.ProcessName.ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        bool CollectWindow(IntPtr h, IntPtr l)
        {
            if (h == _win.Handle) return true;
            if (!Native.IsWindowVisible(h)) return true;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == _ownPid) return true;                                        // never track our own windows/menus
            if (Native.GetWindow(h, 4) != IntPtr.Zero) return true;                 // owned windows
            if ((Native.GetWindowLong(h, -20) & 0x80) != 0) return true;            // WS_EX_TOOLWINDOW
            if (Native.GetWindowTextLength(h) == 0) return true;
            if (Native.IsIconic(h) || Native.IsZoomed(h)) return true;
            _buf.Add(h);
            return true;
        }

        void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            _tick++;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now >= _pending[i].Due)
                {
                    ApplySize(_pending[i].H, _pending[i].Wmm, _pending[i].Hmm, true, true);
                    _pending.RemoveAt(i);
                }
            }

            if (!_auto && _pending.Count == 0) return;

            _buf.Clear();
            Native.EnumWindows(_enumProc, IntPtr.Zero);

            for (int i = 0; i < _buf.Count; i++) TrackWindow(_buf[i], now);

            // drop windows that closed, every 10 ticks (~4 s)
            if (_tick % 10 == 0)
            {
                List<IntPtr> dead = null;
                foreach (KeyValuePair<IntPtr, Track> kv in _tracks)
                {
                    if ((now - kv.Value.Seen).TotalSeconds > 3)
                    {
                        if (dead == null) dead = new List<IntPtr>();
                        dead.Add(kv.Key);
                    }
                }
                if (dead != null) for (int i = 0; i < dead.Count; i++) _tracks.Remove(dead[i]);
            }
        }

        void TrackWindow(IntPtr h, DateTime now)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            int w = r.Right - r.Left;
            int hh = r.Bottom - r.Top;
            if (w < 120 || hh < 80) return;

            Mon mon = Mons.At(Anchor(r));
            uint dpi = mon.Dpi;
            double wmm = w * 25.4 / dpi;
            double hmm = hh * 25.4 / dpi;

            Track t;
            if (!_tracks.TryGetValue(h, out t))
            {
                _tracks[h] = new Track { Mon = mon.Handle, Wmm = wmm, Hmm = hmm, Seen = now };
                return;
            }
            t.Seen = now;

            if (t.Mon != mon.Handle)
            {
                if (_auto)
                {
                    _pending.Add(new Pending { H = h, Wmm = t.Wmm, Hmm = t.Hmm, Due = now.AddMilliseconds(250) });
                    _pending.Add(new Pending { H = h, Wmm = t.Wmm, Hmm = t.Hmm, Due = now.AddMilliseconds(800) });
                    t.AppliedW = -1;
                    t.AppliedH = -1;
                }
                else
                {
                    t.Wmm = wmm;
                    t.Hmm = hmm;
                }
                t.Mon = mon.Handle;
                return;
            }

            bool ours = t.AppliedW > 0
                        && Math.Abs(w - t.AppliedW) <= Tol(t.AppliedW)
                        && Math.Abs(hh - t.AppliedH) <= Tol(t.AppliedH);

            if (!ours)
            {
                // the user resized it by hand, so that becomes the new baseline
                t.Wmm = wmm;
                t.Hmm = hmm;
                t.AppliedW = 0;
                t.AppliedH = 0;
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
            try
            {
                Native.UnregisterHotKey(_win.Handle, 1); Native.UnregisterHotKey(_win.Handle, 2);
                Native.UnregisterHotKey(_win.Handle, 3); Native.UnregisterHotKey(_win.Handle, 4);
                Native.UnregisterHotKey(_win.Handle, 5);
            }
            catch { }

            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            try { if (_ownIcon != null) _ownIcon.Dispose(); } catch { }
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
