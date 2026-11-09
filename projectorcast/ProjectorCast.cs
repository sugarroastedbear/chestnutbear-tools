// ProjectorCast —— 投屏到投影仪（中英双语，见 class L）
// 推送视频：通过 DLNA 把本地视频 / 网络链接推送到局域网里的投影仪播放（极米等）。
// 屏幕投屏：选择一个应用窗口或整个桌面（某个显示器），全屏输出到投影仪所在的显示器。
// 应用窗口：使用 DWM 缩略图（硬件合成，零拷贝，窗口被遮挡时也能正常投屏）。
// 整个桌面：使用 GDI 抓屏 + 缩放输出，可选绘制鼠标指针。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("ProjectorCast")]
[assembly: System.Reflection.AssemblyProduct("ProjectorCast")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace ProjectorCast
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 必须在创建任何窗口前设置为每显示器 DPI 感知，保证坐标与抓屏都是物理像素
            bool ok = false;
            try { ok = Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            if (!ok) { try { Native.SetProcessDPIAware(); } catch { } }

            L.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    /// <summary>
    /// 界面语言：默认跟随 Windows 显示语言（中文系统显示中文，其他显示英文），
    /// 用户可在状态栏右下角手动切换，选择保存在 %APPDATA%\ProjectorCast\lang.txt。
    /// </summary>
    static class L
    {
        public const string Auto = "auto", Chinese = "zh", English = "en";

        /// <summary>当前是否显示中文</summary>
        public static bool Zh;
        /// <summary>用户的选择：auto / zh / en</summary>
        public static string Preference = Auto;

        static string SettingsFile
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectorCast", "lang.txt");
            }
        }

        public static bool SystemIsChinese
        {
            get
            {
                try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"; }
                catch { return false; }
            }
        }

        public static void Init()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string v = File.ReadAllText(SettingsFile).Trim().ToLowerInvariant();
                    if (v == Chinese || v == English || v == Auto) Preference = v;
                }
            }
            catch { }
            Zh = Preference == Chinese || (Preference == Auto && SystemIsChinese);
        }

        public static void Save(string preference)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, preference);
            }
            catch { }
        }

        /// <summary>按当前语言返回中文或英文文字</summary>
        public static string T(string zh, string en) { return Zh ? zh : en; }

        /// <summary>按当前语言返回不同的数值（用于英文更长时的排版）</summary>
        public static int P(int zh, int en) { return Zh ? zh : en; }

        public static string FontName { get { return Zh ? "Microsoft YaHei UI" : "Segoe UI"; } }
    }

    #region Native

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
            public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        public const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4,
                         DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        public const uint SRCCOPY = 0x00CC0020;

        // ---- Display config (显示器友好名称 / 接口类型) ----
        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
            public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen;
            public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags; public uint outputTechnology;
            public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
        [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numMode, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
        [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
        [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);

        // ---- user32 ----
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLong")] static extern uint GetClassLong32(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")] static extern IntPtr GetClassLong64(IntPtr h, int idx);
        public static IntPtr GetClassLongPtr(IntPtr h, int idx)
        {
            return IntPtr.Size == 8 ? GetClassLong64(h, idx) : new IntPtr((int)GetClassLong32(h, idx));
        }
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        // ---- gdi32 ----
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);

        // ---- dwmapi ----
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

        // ---- winmm ----
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint p);
    }

    #endregion

    #region 显示器 / 窗口枚举

    class MonitorInfo
    {
        public int Number;
        public Screen Screen;
        public string FriendlyName;
        public uint Tech = 0xFFFFFFFF;

        public string DeviceName { get { return Screen.DeviceName.TrimEnd('\0'); } }
        public bool IsPrimary { get { return Screen.Primary; } }
        public bool IsInternal { get { return Tech == 0x80000000 || Tech == 6 || Tech == 11 || Tech == 13; } }

        public string TechName
        {
            get
            {
                switch (Tech)
                {
                    case 0: return "VGA";
                    case 4: return "DVI";
                    case 5: return "HDMI";
                    case 10: case 18: return "DisplayPort";
                    case 15: return L.T("无线投屏", "Wireless");
                    case 16: case 17: return L.T("USB/虚拟", "USB/Virtual");
                    default: return IsInternal ? L.T("内置屏幕", "Built-in display") : "";
                }
            }
        }

        public string Title
        {
            get
            {
                string s = L.T("显示器 ", "Display ") + Number;
                if (!string.IsNullOrEmpty(FriendlyName)) s += " · " + FriendlyName;
                if (IsPrimary) s += L.T("（主显示器）", " (primary)");
                return s;
            }
        }

        public string Detail
        {
            get
            {
                string s = Screen.Bounds.Width + "×" + Screen.Bounds.Height;
                if (TechName.Length > 0) s += " · " + TechName;
                return s;
            }
        }

        public override string ToString() { return Title + "    " + Detail; }
    }

    static class DisplayHelper
    {
        public static List<MonitorInfo> GetMonitors()
        {
            var names = QueryNames();
            var screens = new List<Screen>(Screen.AllScreens);
            screens.Sort(delegate (Screen a, Screen b)
            {
                int c = a.Bounds.X.CompareTo(b.Bounds.X);
                return c != 0 ? c : a.Bounds.Y.CompareTo(b.Bounds.Y);
            });
            var list = new List<MonitorInfo>();
            for (int i = 0; i < screens.Count; i++)
            {
                var m = new MonitorInfo { Number = i + 1, Screen = screens[i] };
                KeyValuePair<string, uint> info;
                if (names.TryGetValue(m.DeviceName, out info)) { m.FriendlyName = info.Key; m.Tech = info.Value; }
                list.Add(m);
            }
            return list;
        }

        static Dictionary<string, KeyValuePair<string, uint>> QueryNames()
        {
            var result = new Dictionary<string, KeyValuePair<string, uint>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                const uint QDC_ONLY_ACTIVE_PATHS = 2;
                Native.DISPLAYCONFIG_PATH_INFO[] paths = null;
                uint numPath = 0, numMode = 0;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (Native.GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPath, out numMode) != 0) return result;
                    paths = new Native.DISPLAYCONFIG_PATH_INFO[numPath];
                    var modes = new Native.DISPLAYCONFIG_MODE_INFO[numMode];
                    int rc = Native.QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero);
                    if (rc == 0) break;
                    if (rc != 122) return result; // ERROR_INSUFFICIENT_BUFFER 时重试
                    paths = null;
                }
                if (paths == null) return result;

                for (int i = 0; i < numPath; i++)
                {
                    var p = paths[i];
                    var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    src.header.type = 1;
                    src.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                    src.header.adapterId = p.sourceInfo.adapterId;
                    src.header.id = p.sourceInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                    var tgt = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    tgt.header.type = 2;
                    tgt.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_TARGET_DEVICE_NAME));
                    tgt.header.adapterId = p.targetInfo.adapterId;
                    tgt.header.id = p.targetInfo.id;
                    string friendly = "";
                    uint tech = p.targetInfo.outputTechnology;
                    if (Native.DisplayConfigGetDeviceInfo(ref tgt) == 0)
                    {
                        friendly = (tgt.monitorFriendlyDeviceName ?? "").Trim();
                        tech = tgt.outputTechnology;
                    }
                    string key = (src.viewGdiDeviceName ?? "").TrimEnd('\0');
                    if (key.Length > 0 && !result.ContainsKey(key))
                        result[key] = new KeyValuePair<string, uint>(friendly, tech);
                }
            }
            catch { }
            return result;
        }
    }

    class WindowItem
    {
        public IntPtr Handle;
        public string Title;
        public string ProcessName;
        public Image Icon;
    }

    static class WindowHelper
    {
        public static List<WindowItem> GetWindows(int iconSize)
        {
            var list = new List<WindowItem>();
            uint myPid = (uint)Process.GetCurrentProcess().Id;
            var pidNames = new Dictionary<uint, string>();

            Native.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
                int ex = Native.GetWindowLong(h, -20 /*GWL_EXSTYLE*/);
                if ((ex & 0x80 /*TOOLWINDOW*/) != 0 && (ex & 0x40000 /*APPWINDOW*/) == 0) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;

                int len = Native.GetWindowTextLength(h);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                Native.GetWindowText(h, sb, sb.Capacity);
                string title = sb.ToString();

                var cls = new StringBuilder(256);
                Native.GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Progman" || c == "Shell_TrayWnd" || c == "WorkerW") return true;

                Native.RECT r;
                if (Native.GetWindowRect(h, out r) && !Native.IsIconic(h) && (r.Width <= 1 || r.Height <= 1)) return true;

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == myPid) return true;
                string pname;
                if (!pidNames.TryGetValue(pid, out pname))
                {
                    try { using (var p = Process.GetProcessById((int)pid)) pname = p.ProcessName; }
                    catch { pname = ""; }
                    pidNames[pid] = pname;
                }

                list.Add(new WindowItem { Handle = h, Title = title, ProcessName = pname, Icon = GetIcon(h, iconSize) });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static Image GetIcon(IntPtr h, int size)
        {
            IntPtr icon = IntPtr.Zero, r;
            uint[] kinds = { 2 /*ICON_SMALL2*/, 0 /*ICON_SMALL*/, 1 /*ICON_BIG*/ };
            foreach (uint k in kinds)
            {
                if (Native.SendMessageTimeout(h, 0x7F /*WM_GETICON*/, new IntPtr(k), IntPtr.Zero, 2 /*ABORTIFHUNG*/, 50, out r) != IntPtr.Zero && r != IntPtr.Zero)
                { icon = r; break; }
            }
            if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, -34 /*GCLP_HICONSM*/);
            if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, -14 /*GCLP_HICON*/);
            try
            {
                using (var ic = icon != IntPtr.Zero ? Icon.FromHandle(icon) : (Icon)SystemIcons.Application.Clone())
                using (var bmp = ic.ToBitmap())
                    return new Bitmap(bmp, size, size);
            }
            catch { return null; }
        }
    }

    #endregion

    #region 投屏输出窗口

    abstract class ProjectorForm : Form
    {
        public readonly MonitorInfo Target;
        public readonly string SourceName;
        public event EventHandler SourceLost;
        bool keepAspect;
        bool cursorHidden;

        protected ProjectorForm(MonitorInfo target, string sourceName, bool keepAspect)
        {
            Target = target;
            SourceName = sourceName;
            this.keepAspect = keepAspect;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Text = L.T("投屏输出 - ", "Cast output - ") + sourceName;
            Bounds = target.Screen.Bounds;
        }

        public bool KeepAspect
        {
            get { return keepAspect; }
            set { keepAspect = value; OnLayoutChanged(); }
        }

        // 不抢占焦点，也不出现在 Alt+Tab 中
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*WS_EX_TOOLWINDOW*/; return cp; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = Target.Screen.Bounds;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            OnLayoutChanged();
        }

        protected virtual void OnLayoutChanged() { }

        protected void RaiseSourceLost()
        {
            var h = SourceLost;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!cursorHidden) { Cursor.Hide(); cursorHidden = true; }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
            base.OnFormClosed(e);
        }

        public static Rectangle Fit(int sw, int sh, Size box, bool keep)
        {
            if (!keep || sw <= 0 || sh <= 0) return new Rectangle(Point.Empty, box);
            double s = Math.Min((double)box.Width / sw, (double)box.Height / sh);
            int w = (int)Math.Round(sw * s), h = (int)Math.Round(sh * s);
            return new Rectangle((box.Width - w) / 2, (box.Height - h) / 2, w, h);
        }
    }

    /// <summary>投屏单个应用窗口（DWM 缩略图）</summary>
    class WindowProjector : ProjectorForm
    {
        public readonly IntPtr SourceWindow;
        IntPtr thumb;
        bool clientOnly;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        Native.RECT lastSrc, lastDst;

        public WindowProjector(MonitorInfo target, IntPtr source, string name, bool keepAspect, bool clientOnly)
            : base(target, name, keepAspect)
        {
            SourceWindow = source;
            this.clientOnly = clientOnly;
            timer.Interval = 100;
            timer.Tick += delegate { UpdateThumb(false); };
        }

        public bool ClientOnly
        {
            get { return clientOnly; }
            set { clientOnly = value; UpdateThumb(true); }
        }

        public bool SourceMinimized { get { return Native.IsIconic(SourceWindow); } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Native.DwmRegisterThumbnail(Handle, SourceWindow, out thumb) != 0) thumb = IntPtr.Zero;
            timer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateThumb(true);
        }

        protected override void OnLayoutChanged() { UpdateThumb(true); }

        void UpdateThumb(bool force)
        {
            if (thumb == IntPtr.Zero) return;
            if (!Native.IsWindow(SourceWindow)) { timer.Stop(); RaiseSourceLost(); return; }
            if (Native.IsIconic(SourceWindow)) return; // 最小化时保留上一次的画面参数

            Native.RECT wr;
            if (!Native.GetWindowRect(SourceWindow, out wr)) return;
            Native.RECT src;
            if (clientOnly)
            {
                Native.RECT cr;
                Native.GetClientRect(SourceWindow, out cr);
                var p = new Native.POINT();
                Native.ClientToScreen(SourceWindow, ref p);
                int x = p.X - wr.Left, y = p.Y - wr.Top;
                src = new Native.RECT(x, y, x + cr.Width, y + cr.Height);
            }
            else
            {
                // 裁掉 Win10/11 窗口四周不可见的调整边框
                Native.RECT ext;
                if (Native.DwmGetWindowAttribute(SourceWindow, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out ext, Marshal.SizeOf(typeof(Native.RECT))) == 0)
                    src = new Native.RECT(ext.Left - wr.Left, ext.Top - wr.Top, ext.Right - wr.Left, ext.Bottom - wr.Top);
                else
                    src = new Native.RECT(0, 0, wr.Width, wr.Height);
            }
            if (src.Width <= 0 || src.Height <= 0) return;

            Rectangle d = Fit(src.Width, src.Height, ClientSize, KeepAspect);
            var dst = new Native.RECT(d.Left, d.Top, d.Right, d.Bottom);
            if (!force && Same(src, lastSrc) && Same(dst, lastDst)) return;
            lastSrc = src; lastDst = dst;

            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_RECTSOURCE | Native.DWM_TNP_OPACITY |
                          Native.DWM_TNP_VISIBLE | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = dst,
                rcSource = src,
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false
            };
            Native.DwmUpdateThumbnailProperties(thumb, ref props);
        }

        static bool Same(Native.RECT a, Native.RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(thumb); thumb = IntPtr.Zero; }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>投屏整个显示器（GDI 抓屏）</summary>
    class ScreenProjector : ProjectorForm
    {
        readonly Rectangle source;
        readonly object sync = new object();
        Rectangle dest;
        IntPtr hwnd;
        Thread worker;
        volatile bool running;
        volatile bool showCursor;
        volatile int fps;
        volatile float actualFps;

        public ScreenProjector(MonitorInfo target, Rectangle source, string name, bool keepAspect, bool showCursor, int fps)
            : base(target, name, keepAspect)
        {
            this.source = source;
            this.showCursor = showCursor;
            this.fps = fps;
        }

        public bool ShowCursor { get { return showCursor; } set { showCursor = value; } }
        public int Fps { get { return fps; } set { fps = Math.Max(1, value); } }
        public float ActualFps { get { return actualFps; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            hwnd = Handle;
            UpdateDest();
            running = true;
            worker = new Thread(Loop) { IsBackground = true, Name = "ScreenCapture" };
            worker.Start();
        }

        protected override void OnLayoutChanged()
        {
            UpdateDest();
            Invalidate();
        }

        void UpdateDest()
        {
            lock (sync) dest = Fit(source.Width, source.Height, ClientSize, KeepAspect);
        }

        void Loop()
        {
            int w = source.Width, h = source.Height;
            Native.timeBeginPeriod(1);
            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            IntPtr memDC = Native.CreateCompatibleDC(screenDC);
            IntPtr bmp = Native.CreateCompatibleBitmap(screenDC, w, h);
            IntPtr old = Native.SelectObject(memDC, bmp);
            IntPtr lastCursor = IntPtr.Zero;
            int hotX = 0, hotY = 0;
            var clock = Stopwatch.StartNew();
            long fpsStart = 0;
            int frames = 0;
            try
            {
                while (running)
                {
                    long t0 = clock.ElapsedMilliseconds;
                    Native.BitBlt(memDC, 0, 0, w, h, screenDC, source.X, source.Y, Native.SRCCOPY);

                    if (showCursor)
                    {
                        var ci = new Native.CURSORINFO();
                        ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
                        if (Native.GetCursorInfo(ref ci) && (ci.flags & 1) != 0 && ci.hCursor != IntPtr.Zero)
                        {
                            if (ci.hCursor != lastCursor)
                            {
                                Native.ICONINFO ii;
                                if (Native.GetIconInfo(ci.hCursor, out ii))
                                {
                                    hotX = ii.xHotspot; hotY = ii.yHotspot;
                                    if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                                    if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                                }
                                lastCursor = ci.hCursor;
                            }
                            int cx = ci.ptScreenPos.X - source.X - hotX, cy = ci.ptScreenPos.Y - source.Y - hotY;
                            if (cx > -128 && cy > -128 && cx < w && cy < h)
                                Native.DrawIconEx(memDC, cx, cy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3 /*DI_NORMAL*/);
                        }
                    }

                    Rectangle d;
                    lock (sync) d = dest;
                    IntPtr wdc = Native.GetDC(hwnd);
                    if (wdc != IntPtr.Zero)
                    {
                        if (d.Width == w && d.Height == h)
                            Native.BitBlt(wdc, d.X, d.Y, w, h, memDC, 0, 0, Native.SRCCOPY);
                        else
                        {
                            Native.SetStretchBltMode(wdc, 4 /*HALFTONE*/);
                            Native.SetBrushOrgEx(wdc, 0, 0, IntPtr.Zero);
                            Native.StretchBlt(wdc, d.X, d.Y, d.Width, d.Height, memDC, 0, 0, w, h, Native.SRCCOPY);
                        }
                        Native.ReleaseDC(hwnd, wdc);
                    }

                    frames++;
                    long now = clock.ElapsedMilliseconds;
                    if (now - fpsStart >= 1000)
                    {
                        actualFps = frames * 1000f / (now - fpsStart);
                        frames = 0;
                        fpsStart = now;
                    }
                    int wait = (int)(1000 / Math.Max(1, fps) - (clock.ElapsedMilliseconds - t0));
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            finally
            {
                Native.SelectObject(memDC, old);
                Native.DeleteObject(bmp);
                Native.DeleteDC(memDC);
                Native.ReleaseDC(IntPtr.Zero, screenDC);
                Native.timeEndPeriod(1);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            running = false;
            if (worker != null) worker.Join(2000);
            base.OnFormClosing(e);
        }
    }

    /// <summary>"识别显示器" 时在每块屏幕上显示的编号</summary>
    class IdentifyForm : Form
    {
        readonly MonitorInfo monitor;

        public IdentifyForm(MonitorInfo m)
        {
            monitor = m;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(0, 103, 192);
            ShowInTaskbar = false;
            TopMost = true;
            var b = m.Screen.Bounds;
            int h = b.Height / 3, w = Math.Min(b.Width - 40, h * 3 / 2);
            Bounds = new Rectangle(b.X + (b.Width - w) / 2, b.Y + (b.Height - h) / 2, w, h);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += delegate { t.Dispose(); Close(); };
            t.Start();
            Click += delegate { Close(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = Bounds; // 规避跨 DPI 显示器创建时的尺寸调整
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int h = ClientSize.Height;
            using (var big = new Font("Segoe UI", h * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var small = new Font(L.FontName, Math.Max(12, h * 0.075f), GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(monitor.Number.ToString(), big, Brushes.White, new RectangleF(0, 0, ClientSize.Width, h * 0.72f), sf);
                string sub = (string.IsNullOrEmpty(monitor.FriendlyName) ? "" : monitor.FriendlyName + "\n") + monitor.Detail;
                g.DrawString(sub, small, Brushes.White, new RectangleF(0, h * 0.62f, ClientSize.Width, h * 0.36f), sf);
            }
        }
    }

    #endregion

    #region DLNA 推送

    class DlnaException : Exception
    {
        public DlnaException(string message) : base(message) { }
    }

    /// <summary>局域网里的一个 DLNA 媒体渲染器（投影仪 / 电视）</summary>
    class DlnaDevice
    {
        public const string AVT = "urn:schemas-upnp-org:service:AVTransport:1";
        public const string RC = "urn:schemas-upnp-org:service:RenderingControl:1";
        const string IID = "<InstanceID>0</InstanceID>";

        public string Name, Location, Host, Udn, AvTransportUrl, RenderingUrl;
        readonly object callLock = new object();

        public override string ToString() { return Name + "    (" + Host + ")"; }

        public void SetUri(string url, string meta)
        {
            Call(AvTransportUrl, AVT, "SetAVTransportURI",
                IID + "<CurrentURI>" + Esc(url) + "</CurrentURI><CurrentURIMetaData>" + Esc(meta) + "</CurrentURIMetaData>");
        }
        public void Play() { Call(AvTransportUrl, AVT, "Play", IID + "<Speed>1</Speed>"); }
        public void Pause() { Call(AvTransportUrl, AVT, "Pause", IID); }
        public void Stop() { Call(AvTransportUrl, AVT, "Stop", IID); }
        public void Seek(TimeSpan t)
        {
            Call(AvTransportUrl, AVT, "Seek", IID + "<Unit>REL_TIME</Unit><Target>" + FormatTime(t) + "</Target>");
        }
        public string GetState() { return Tag(Call(AvTransportUrl, AVT, "GetTransportInfo", IID), "CurrentTransportState"); }
        public void GetPosition(out TimeSpan pos, out TimeSpan dur)
        {
            string x = Call(AvTransportUrl, AVT, "GetPositionInfo", IID);
            pos = ParseTime(Tag(x, "RelTime"));
            dur = ParseTime(Tag(x, "TrackDuration"));
        }
        public int GetVolume()
        {
            if (RenderingUrl == null) return -1;
            int v;
            return int.TryParse(Tag(Call(RenderingUrl, RC, "GetVolume", IID + "<Channel>Master</Channel>"), "CurrentVolume"), out v) ? v : -1;
        }
        public void SetVolume(int v)
        {
            if (RenderingUrl != null) Call(RenderingUrl, RC, "SetVolume", IID + "<Channel>Master</Channel><DesiredVolume>" + v + "</DesiredVolume>");
        }
        public void SetMute(bool mute)
        {
            if (RenderingUrl != null) Call(RenderingUrl, RC, "SetMute", IID + "<Channel>Master</Channel><DesiredMute>" + (mute ? "1" : "0") + "</DesiredMute>");
        }

        string Call(string url, string service, string action, string args)
        {
            string body = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:" + action + " xmlns:u=\"" + service + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
            byte[] data = Encoding.UTF8.GetBytes(body);
            lock (callLock)
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "text/xml; charset=\"utf-8\"";
                req.Headers.Add("SOAPACTION", "\"" + service + "#" + action + "\"");
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.KeepAlive = false;
                req.Proxy = null;
                req.ServicePoint.Expect100Continue = false;
                req.ContentLength = data.Length;
                try
                {
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                    using (var resp = req.GetResponse())
                    using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return r.ReadToEnd();
                }
                catch (WebException ex)
                {
                    string detail = ex.Message;
                    if (ex.Response != null)
                    {
                        try
                        {
                            using (var r = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                            {
                                string x = r.ReadToEnd();
                                string code = Tag(x, "errorCode"), desc = Tag(x, "errorDescription");
                                if (code.Length > 0 || desc.Length > 0) detail = L.T("错误 ", "Error ") + code + " " + desc;
                            }
                        }
                        catch { }
                    }
                    throw new DlnaException(action + L.T(" 失败：", " failed: ") + detail);
                }
            }
        }

        public static string Esc(string s) { return SecurityElement.Escape(s ?? ""); }

        public static string Tag(string xml, string tag)
        {
            var m = Regex.Match(xml ?? "", "<(?:[\\w-]+:)?" + tag + "(?:\\s[^>]*)?>(.*?)</(?:[\\w-]+:)?" + tag + ">", RegexOptions.Singleline);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
        }

        public static TimeSpan ParseTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return TimeSpan.Zero;
            var p = s.Split(':');
            int h, m;
            double sec;
            if (p.Length != 3 || !int.TryParse(p[0], out h) || !int.TryParse(p[1], out m) ||
                !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
                return TimeSpan.Zero;
            return TimeSpan.FromSeconds(h * 3600 + m * 60 + sec);
        }

        public static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }
    }

    static class DlnaDiscovery
    {
        /// <summary>SSDP 搜索媒体渲染器。unicast 不为空时只向该 IP 发送（手动输入地址时使用）。</summary>
        public static List<DlnaDevice> Discover(int timeoutMs, IPAddress unicast, IEnumerable<string> extraLocations)
        {
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (extraLocations != null)
                foreach (var l in extraLocations) if (!string.IsNullOrEmpty(l)) locations.Add(l);

            // 部分设备（如极米）不回应单播搜索，所以始终发组播；指定 IP 时额外发单播，最后按 IP 过滤
            var dests = new List<IPEndPoint> { new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900) };
            if (unicast != null) dests.Add(new IPEndPoint(unicast, 1900));
            string[] targets = { "urn:schemas-upnp-org:device:MediaRenderer:1", DlnaDevice.AVT };
            var threads = new List<Thread>();
            foreach (var ip in LocalAddresses())
            {
                UdpClient sock;
                try { sock = new UdpClient(new IPEndPoint(ip, 0)); } catch { continue; }
                var t = new Thread(delegate ()
                {
                    try
                    {
                        for (int round = 0; round < 2; round++)
                        {
                            foreach (string st in targets)
                            {
                                byte[] b = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");
                                foreach (var dest in dests) sock.Send(b, b.Length, dest);
                            }
                            Thread.Sleep(200);
                        }
                        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                        while (DateTime.UtcNow < deadline)
                        {
                            sock.Client.ReceiveTimeout = Math.Max(50, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                            IPEndPoint from = null;
                            byte[] data;
                            try { data = sock.Receive(ref from); } catch (SocketException) { break; }
                            var m = Regex.Match(Encoding.UTF8.GetString(data), @"(?im)^location:\s*(\S+)");
                            if (m.Success) lock (locations) locations.Add(m.Groups[1].Value.Trim());
                        }
                    }
                    catch { }
                    finally { sock.Close(); }
                }) { IsBackground = true };
                threads.Add(t);
                t.Start();
            }
            foreach (var t in threads) t.Join(timeoutMs + 2000);

            var devices = new List<DlnaDevice>();
            var fetch = new List<Thread>();
            List<string> locs;
            lock (locations) locs = new List<string>(locations);
            foreach (string loc in locs)
            {
                string l = loc;
                var t = new Thread(delegate ()
                {
                    var d = Describe(l);
                    if (d != null) lock (devices) devices.Add(d);
                }) { IsBackground = true };
                fetch.Add(t);
                t.Start();
            }
            foreach (var t in fetch) t.Join(5000);

            var result = new List<DlnaDevice>();
            var seen = new HashSet<string>();
            lock (devices)
                foreach (var d in devices)
                {
                    if (unicast != null && d.Host != unicast.ToString()) continue;
                    if (seen.Add(string.IsNullOrEmpty(d.Udn) ? d.AvTransportUrl : d.Udn)) result.Add(d);
                }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            return result;
        }

        static IEnumerable<IPAddress> LocalAddresses()
        {
            var list = new List<IPAddress>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (a.ToString().StartsWith("169.254.")) continue;
                    list.Add(a);
                }
            }
            return list;
        }

        public static DlnaDevice Describe(string location)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(location);
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                req.Proxy = null;
                var doc = new XmlDocument { XmlResolver = null };
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream())
                    doc.Load(s);

                string avt = null, rc = null;
                foreach (XmlNode svc in doc.SelectNodes("//*[local-name()='service']"))
                {
                    string type = Text(svc, "serviceType") ?? "", ctl = Text(svc, "controlURL");
                    if (ctl == null) continue;
                    if (type.StartsWith("urn:schemas-upnp-org:service:AVTransport")) avt = ctl;
                    else if (type.StartsWith("urn:schemas-upnp-org:service:RenderingControl")) rc = ctl;
                }
                if (avt == null) return null;

                string urlBase = Text(doc, "URLBase");
                var baseUri = new Uri(string.IsNullOrEmpty(urlBase) ? location : urlBase);
                string name = Text(doc, "friendlyName");
                return new DlnaDevice
                {
                    Location = location,
                    Host = new Uri(location).Host,
                    Name = string.IsNullOrEmpty(name) ? L.T("DLNA 设备", "DLNA device") : name,
                    Udn = Text(doc, "UDN"),
                    AvTransportUrl = new Uri(baseUri, avt).ToString(),
                    RenderingUrl = rc == null ? null : new Uri(baseUri, rc).ToString()
                };
            }
            catch { return null; }
        }

        static string Text(XmlNode n, string name)
        {
            var x = n.SelectSingleNode(".//*[local-name()='" + name + "']");
            return x == null ? null : x.InnerText.Trim();
        }
    }

    static class MediaTypes
    {
        static readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".mp4", "video/mp4" }, { ".m4v", "video/mp4" }, { ".mkv", "video/x-matroska" }, { ".avi", "video/x-msvideo" },
            { ".mov", "video/quicktime" }, { ".wmv", "video/x-ms-wmv" }, { ".flv", "video/x-flv" }, { ".webm", "video/webm" },
            { ".ts", "video/mp2t" }, { ".m2ts", "video/mp2t" }, { ".mts", "video/mp2t" }, { ".mpg", "video/mpeg" },
            { ".mpeg", "video/mpeg" }, { ".vob", "video/mpeg" }, { ".3gp", "video/3gpp" }, { ".rmvb", "application/vnd.rn-realmedia-vbr" },
            { ".m3u8", "application/vnd.apple.mpegurl" },
            { ".mp3", "audio/mpeg" }, { ".flac", "audio/flac" }, { ".m4a", "audio/mp4" }, { ".aac", "audio/aac" },
            { ".wav", "audio/wav" }, { ".ogg", "audio/ogg" }, { ".ape", "audio/x-ape" },
            { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".png", "image/png" }, { ".gif", "image/gif" }, { ".bmp", "image/bmp" }
        };

        public static string Extension(string pathOrUrl)
        {
            string s = pathOrUrl ?? "";
            int q = s.IndexOfAny(new[] { '?', '#' });
            if (q >= 0 && s.Contains("://")) s = s.Substring(0, q);
            try { return Path.GetExtension(s); } catch { return ""; }
        }

        public static string Mime(string pathOrUrl)
        {
            string m;
            return map.TryGetValue(Extension(pathOrUrl), out m) ? m : "video/mp4";
        }

        public static bool IsMedia(string path)
        {
            return map.ContainsKey(Extension(path));
        }

        public static string FileFilter
        {
            get
            {
                var v = new List<string>();
                foreach (var kv in map) v.Add("*" + kv.Key);
                return L.T("视频 / 音频 / 图片|", "Video / Audio / Images|") + string.Join(";", v) + L.T("|所有文件|*.*", "|All files|*.*");
            }
        }

        public static string Didl(string title, string url, string mime, long size, bool local)
        {
            string cls = mime.StartsWith("audio") ? "object.item.audioItem.musicTrack"
                       : mime.StartsWith("image") ? "object.item.imageItem.photo" : "object.item.videoItem";
            string pi = "http-get:*:" + mime + ":" + (local ? "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000" : "*");
            return "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                   "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"><item id=\"1\" parentID=\"0\" restricted=\"1\">" +
                   "<dc:title>" + DlnaDevice.Esc(title) + "</dc:title><upnp:class>" + cls + "</upnp:class>" +
                   "<res protocolInfo=\"" + pi + "\"" + (size > 0 ? " size=\"" + size + "\"" : "") + ">" + DlnaDevice.Esc(url) + "</res>" +
                   "</item></DIDL-Lite>";
        }
    }

    /// <summary>极简 HTTP 文件服务器：让投影仪从本机读取视频文件（支持 Range，可拖动进度）</summary>
    class MediaServer
    {
        public const int FirstPort = 18650, LastPort = 18659;
        TcpListener listener;
        volatile bool running;
        readonly Dictionary<string, string> idToPath = new Dictionary<string, string>();
        readonly Dictionary<string, string> pathToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Port { get; private set; }
        public bool Running { get { return running; } }

        public void Start()
        {
            if (running) return;
            for (int p = FirstPort; p <= LastPort && listener == null; p++)
            {
                try { var l = new TcpListener(IPAddress.Any, p); l.Start(); listener = l; Port = p; }
                catch (SocketException) { }
            }
            if (listener == null)
            {
                listener = new TcpListener(IPAddress.Any, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            running = true;
            var l2 = listener;
            new Thread(delegate () { AcceptLoop(l2); }) { IsBackground = true, Name = "MediaServer" }.Start();
        }

        public void Stop()
        {
            running = false;
            if (listener != null) { try { listener.Stop(); } catch { } listener = null; }
        }

        public string UrlFor(string path, string hostIp)
        {
            string id;
            lock (idToPath)
            {
                if (!pathToId.TryGetValue(path, out id))
                {
                    id = Guid.NewGuid().ToString("N").Substring(0, 16);
                    pathToId[path] = id;
                    idToPath[id] = path;
                }
            }
            return "http://" + hostIp + ":" + Port + "/m/" + id + Uri.EscapeDataString(Path.GetExtension(path).ToLowerInvariant());
        }

        void AcceptLoop(TcpListener l)
        {
            while (running)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch { break; }
                ThreadPool.QueueUserWorkItem(Serve, c);
            }
        }

        void Serve(object state)
        {
            var client = (TcpClient)state;
            try
            {
                var ns = client.GetStream();
                ns.ReadTimeout = 20000;
                ns.WriteTimeout = 120000;
                string head = ReadHead(ns);
                if (head == null) return;
                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var first = lines[0].Split(' ');
                if (first.Length < 2) return;
                string method = first[0].ToUpperInvariant();
                string target = Uri.UnescapeDataString(first[1]);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
                }

                string path = null;
                if (target.StartsWith("/m/"))
                {
                    string name = target.Substring(3);
                    int q = name.IndexOf('?');
                    if (q >= 0) name = name.Substring(0, q);
                    int dot = name.IndexOf('.');
                    string id = dot >= 0 ? name.Substring(0, dot) : name;
                    lock (idToPath) idToPath.TryGetValue(id, out path);
                }
                if (path == null || !File.Exists(path) || (method != "GET" && method != "HEAD"))
                {
                    WriteText(ns, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, false))
                {
                    long len = fs.Length, start = 0, end = len - 1;
                    bool partial = false;
                    string range;
                    if (headers.TryGetValue("Range", out range))
                    {
                        var m = Regex.Match(range, @"bytes\s*=\s*(\d*)\s*-\s*(\d*)");
                        if (m.Success)
                        {
                            string a = m.Groups[1].Value, b = m.Groups[2].Value;
                            if (a.Length == 0 && b.Length > 0) start = Math.Max(0, len - long.Parse(b));
                            else
                            {
                                if (a.Length > 0) start = long.Parse(a);
                                if (b.Length > 0) end = Math.Min(len - 1, long.Parse(b));
                            }
                            if (start >= len || start > end)
                            {
                                WriteText(ns, "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + len + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                                return;
                            }
                            partial = true;
                        }
                    }
                    long count = end - start + 1;
                    var sb = new StringBuilder();
                    sb.Append("HTTP/1.1 ").Append(partial ? "206 Partial Content" : "200 OK").Append("\r\n");
                    sb.Append("Content-Type: ").Append(MediaTypes.Mime(path)).Append("\r\n");
                    sb.Append("Content-Length: ").Append(count).Append("\r\n");
                    if (partial) sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(len).Append("\r\n");
                    sb.Append("Accept-Ranges: bytes\r\n");
                    sb.Append("transferMode.dlna.org: Streaming\r\n");
                    sb.Append("contentFeatures.dlna.org: DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000\r\n");
                    sb.Append("Server: Windows UPnP/1.0 DLNADOC/1.50 ProjectorCast/1.0\r\n");
                    sb.Append("Connection: close\r\n\r\n");
                    WriteText(ns, sb.ToString());
                    if (method == "HEAD") return;

                    fs.Position = start;
                    var buf = new byte[256 * 1024];
                    while (count > 0 && running)
                    {
                        int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, count));
                        if (n <= 0) break;
                        ns.Write(buf, 0, n);
                        count -= n;
                    }
                }
            }
            catch { } // 播放器拖动进度时会主动断开连接，属正常情况
            finally { client.Close(); }
        }

        static void WriteText(Stream s, string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        static string ReadHead(Stream s)
        {
            var ms = new MemoryStream();
            int state = 0; // 匹配 \r\n\r\n
            while (ms.Length < 16384)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                ms.WriteByte((byte)b);
                if (b == '\r') state = (state == 2) ? 3 : 1;
                else if (b == '\n') state = (state == 1) ? 2 : (state == 3 ? 4 : 0);
                else state = 0;
                if (state == 4) return Encoding.UTF8.GetString(ms.ToArray());
            }
            return null;
        }
    }

    class PlaylistItem
    {
        public string Path;   // 本地文件
        public string Url;    // 网络链接
        public string Name;
        public long Size;
        public bool IsUrl { get { return Url != null; } }
    }

    static class InputBox
    {
        public static string Show(IWin32Window owner, string title, string prompt, string value)
        {
            using (var f = new Form())
            {
                f.AutoScaleDimensions = new SizeF(96F, 96F);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.Font = new Font(L.FontName, 9F);
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(460, 130);
                var lbl = new Label { Text = prompt, Location = new Point(14, 14), Size = new Size(432, 22) };
                var tb = new TextBox { Text = value ?? "", Location = new Point(14, 42), Size = new Size(432, 26) };
                var ok = new Button { Text = L.T("确定", "OK"), DialogResult = DialogResult.OK, Location = new Point(270, 84), Size = new Size(84, 30) };
                var cancel = new Button { Text = L.T("取消", "Cancel"), DialogResult = DialogResult.Cancel, Location = new Point(362, 84), Size = new Size(84, 30) };
                f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Shown += delegate { tb.Focus(); tb.SelectionStart = tb.Text.Length; };
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }
    }

    /// <summary>"推送视频" 标签页</summary>
    class DlnaPanel : Panel
    {
        const string FirewallRuleName = "ProjectorCast DLNA";

        readonly Action<string> setStatus;
        ComboBox cmbDev;
        Button btnSearch, btnManual, btnAdd, btnAddUrl, btnRemove, btnClear, btnPrev, btnPlay, btnPause, btnStop, btnNext;
        ListView list;
        Label lblNow, lblTime, lblVolVal;
        TrackBar seek, volume;
        CheckBox chkMute, chkAutoNext;

        readonly MediaServer server = new MediaServer();
        readonly System.Windows.Forms.Timer volTimer = new System.Windows.Forms.Timer { Interval = 250 };
        System.Threading.Timer pollTimer;
        int polling;
        bool searching, seeking, updatingVolume, firewallChecked;

        PlaylistItem nowPlaying;
        DlnaDevice playingOn;
        string lastState = "";
        TimeSpan lastPos, lastDur;
        DateTime ignoreEndUntil, seekHoldUntil;
        bool userStopped;

        static string SettingsDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectorCast"); }
        }
        static string LastDeviceFile { get { return Path.Combine(SettingsDir, "last_device.txt"); } }

        public DlnaPanel(Action<string> setStatus)
        {
            this.setStatus = setStatus;
            Size = new Size(580, 600);
            BuildUi();
        }

        /// <summary>正在播放本机文件（关闭程序会中断）</summary>
        public bool ServingLocalPlayback
        {
            get
            {
                return nowPlaying != null && !nowPlaying.IsUrl &&
                       (lastState == "PLAYING" || lastState == "PAUSED_PLAYBACK" || lastState == "TRANSITIONING");
            }
        }

        void BuildUi()
        {
            // ① 设备
            var grpDev = new GroupBox { Text = L.T("① 选择投影仪（与电脑连接同一个 WiFi）", "① Choose a projector (on the same Wi-Fi as this PC)"), Location = new Point(12, 10), Size = new Size(556, 72), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            cmbDev = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(16, 30), Size = new Size(314, 28), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            btnSearch = new Button { Text = L.T("搜索设备", "Search"), Location = new Point(338, 28), Size = new Size(96, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnManual = new Button { Text = L.T("输入 IP…", "Enter IP…"), Location = new Point(440, 28), Size = new Size(100, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            grpDev.Controls.AddRange(new Control[] { cmbDev, btnSearch, btnManual });

            // ② 播放列表
            var grpList = new GroupBox { Text = L.T("② 播放列表（可直接把视频文件或文件夹拖进来，双击播放）", "② Playlist (drag video files or folders here, double-click to play)"), Location = new Point(12, 90), Size = new Size(556, 290), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            btnAdd = new Button { Text = L.T("添加视频…", "Add videos…"), Location = new Point(16, 26), Size = new Size(100, 30) };
            btnAddUrl = new Button { Text = L.T("添加网络链接…", "Add URL…"), Location = new Point(122, 26), Size = new Size(124, 30) };
            btnRemove = new Button { Text = L.T("移除", "Remove"), Location = new Point(252, 26), Size = new Size(70, 30) };
            btnClear = new Button { Text = L.T("清空", "Clear"), Location = new Point(328, 26), Size = new Size(70, 30) };
            list = new ListView
            {
                Location = new Point(16, 62), Size = new Size(524, 212),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                View = View.Details, FullRowSelect = true, HideSelection = false, AllowDrop = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            list.Columns.Add(L.T("名称", "Name"));
            list.Columns.Add(L.T("大小 / 来源", "Size / Source"));
            grpList.Controls.AddRange(new Control[] { btnAdd, btnAddUrl, btnRemove, btnClear, list });

            // ③ 播放控制
            var grpCtl = new GroupBox { Text = L.T("③ 播放控制", "③ Playback"), Location = new Point(12, 388), Size = new Size(556, 200), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            lblNow = new Label { Text = L.T("未在播放", "Not playing"), Location = new Point(16, 26), Size = new Size(524, 22), AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            seek = new TrackBar { Location = new Point(10, 52), Size = new Size(366, 30), TickStyle = TickStyle.None, Minimum = 0, Maximum = 1, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lblTime = new Label { Text = "00:00:00 / 00:00:00", Location = new Point(378, 54), Size = new Size(166, 24), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnPrev = new Button { Text = L.T("上一个", "Previous"), Location = new Point(16, 92), Size = new Size(96, 36) };
            btnPlay = new Button { Text = L.T("▶ 播放", "▶ Play"), Location = new Point(118, 92), Size = new Size(96, 36), Font = new Font(L.FontName, 9F, FontStyle.Bold) };
            btnPause = new Button { Text = L.T("暂停", "Pause"), Location = new Point(220, 92), Size = new Size(96, 36) };
            btnStop = new Button { Text = L.T("■ 停止", "■ Stop"), Location = new Point(322, 92), Size = new Size(96, 36) };
            btnNext = new Button { Text = L.T("下一个", "Next"), Location = new Point(424, 92), Size = new Size(96, 36) };
            var lblVol = new Label { Text = L.T("音量", "Vol"), Location = new Point(16, 148), AutoSize = true };
            volume = new TrackBar { Location = new Point(52, 140), Size = new Size(200, 30), TickStyle = TickStyle.None, Minimum = 0, Maximum = 100, Value = 50 };
            lblVolVal = new Label { Text = "--", Location = new Point(256, 148), Size = new Size(40, 22) };
            chkMute = new CheckBox { Text = L.T("静音", "Mute"), Location = new Point(300, 146), AutoSize = true };
            chkAutoNext = new CheckBox { Text = L.T("自动播放下一个", "Auto-play next"), Location = new Point(380, 146), AutoSize = true, Checked = true };
            grpCtl.Controls.AddRange(new Control[] { lblNow, seek, lblTime, btnPrev, btnPlay, btnPause, btnStop, btnNext, lblVol, volume, lblVolVal, chkMute, chkAutoNext });

            Controls.AddRange(new Control[] { grpDev, grpList, grpCtl });

            // 事件
            btnSearch.Click += delegate { Search(null); };
            btnManual.Click += delegate
            {
                var d = cmbDev.SelectedItem as DlnaDevice;
                string s = InputBox.Show(FindForm(), L.T("输入投影仪地址", "Projector address"), L.T("请输入投影仪的 IP 地址（可在投影仪的 网络设置 里查看）：", "Enter the projector's IP address (see the projector's network settings):"), d != null ? d.Host : "192.168.1.");
                if (s == null) return;
                IPAddress ip;
                if (!IPAddress.TryParse(s, out ip)) { Warn(L.T("IP 地址格式不正确。", "That IP address isn't valid.")); return; }
                Search(ip);
            };
            cmbDev.SelectedIndexChanged += delegate
            {
                var d = cmbDev.SelectedItem as DlnaDevice;
                if (d == null) return;
                try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(LastDeviceFile, d.Location); } catch { }
                RefreshVolume(d);
            };
            btnAdd.Click += delegate
            {
                using (var dlg = new OpenFileDialog { Multiselect = true, Filter = MediaTypes.FileFilter, Title = L.T("选择要投放的视频", "Choose videos to cast") })
                    if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AddPaths(dlg.FileNames);
            };
            btnAddUrl.Click += delegate
            {
                string s = InputBox.Show(FindForm(), L.T("添加网络链接", "Add URL"), L.T("视频直链地址（http/https，如 .mp4 / .m3u8）：", "Direct video link (http/https, e.g. .mp4 / .m3u8):"), "");
                if (string.IsNullOrEmpty(s)) return;
                if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                { Warn(L.T("请输入以 http:// 或 https:// 开头的视频地址。", "Please enter a video address starting with http:// or https://.")); return; }
                string name = s;
                try { var u = new Uri(s); string f = Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath)); name = f.Length > 0 ? f : u.Host; } catch { }
                AddItem(new PlaylistItem { Url = s, Name = name });
            };
            btnRemove.Click += delegate
            {
                foreach (ListViewItem it in new System.Collections.ArrayList(list.SelectedItems)) list.Items.Remove(it);
            };
            btnClear.Click += delegate { list.Items.Clear(); };
            list.DoubleClick += delegate { var it = SelectedItem(); if (it != null) PlayItem(it); };
            list.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { var it = SelectedItem(); if (it != null) PlayItem(it); }
                else if (e.KeyCode == Keys.Delete) btnRemove.PerformClick();
            };
            list.Resize += delegate { LayoutColumns(); };
            list.DragEnter += delegate (object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            list.DragDrop += delegate (object s, DragEventArgs e)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) AddPaths(files);
            };

            btnPlay.Click += delegate { OnPlayClicked(); };
            btnPause.Click += delegate
            {
                var d = playingOn;
                if (d != null) RunBg(delegate { d.Pause(); }, L.T("暂停失败", "Pause failed"));
            };
            btnStop.Click += delegate
            {
                var d = playingOn;
                if (d == null) return;
                userStopped = true;
                RunBg(delegate { d.Stop(); }, L.T("停止失败", "Stop failed"));
            };
            btnPrev.Click += delegate { var it = Neighbor(-1); if (it != null) PlayItem(it); };
            btnNext.Click += delegate { var it = Neighbor(1); if (it != null) PlayItem(it); };

            seek.MouseDown += delegate { seeking = true; };
            seek.MouseUp += delegate { seeking = false; DoSeek(); };
            seek.KeyUp += delegate { DoSeek(); };
            seek.Scroll += delegate { lblTime.Text = DlnaDevice.FormatTime(TimeSpan.FromSeconds(seek.Value)) + " / " + DlnaDevice.FormatTime(lastDur); };

            volume.ValueChanged += delegate
            {
                lblVolVal.Text = volume.Value.ToString();
                if (!updatingVolume) { volTimer.Stop(); volTimer.Start(); }
            };
            volTimer.Tick += delegate
            {
                volTimer.Stop();
                var d = CurrentDevice();
                int v = volume.Value;
                if (d != null) RunBg(delegate { d.SetVolume(v); }, L.T("调节音量失败", "Volume change failed"));
            };
            chkMute.CheckedChanged += delegate
            {
                var d = CurrentDevice();
                bool m = chkMute.Checked;
                if (d != null) RunBg(delegate { d.SetMute(m); }, L.T("静音设置失败", "Mute failed"));
            };
        }

        /// <summary>主窗口加载完成后调用</summary>
        public void Start()
        {
            LayoutColumns();
            Search(null);
            pollTimer = new System.Threading.Timer(Poll, null, 1000, 1000);
        }

        public void Shutdown()
        {
            if (pollTimer != null) { pollTimer.Dispose(); pollTimer = null; }
            volTimer.Stop();
            server.Stop();
        }

        void LayoutColumns()
        {
            if (list.Columns.Count < 2) return;
            int second = list.Font.Height * 8;
            list.Columns[1].Width = second;
            list.Columns[0].Width = Math.Max(80, list.ClientSize.Width - second - 2);
        }

        DlnaDevice CurrentDevice() { return playingOn ?? cmbDev.SelectedItem as DlnaDevice; }

        PlaylistItem SelectedItem()
        {
            return list.SelectedItems.Count > 0 ? (PlaylistItem)list.SelectedItems[0].Tag : null;
        }

        // ---------- 搜索 ----------

        void Search(IPAddress unicast)
        {
            if (searching) return;
            searching = true;
            btnSearch.Enabled = btnManual.Enabled = false;
            btnSearch.Text = L.T("搜索中…", "Searching…");
            setStatus(unicast == null ? L.T("正在局域网中搜索投影仪…", "Searching the local network for projectors…") : L.T("正在连接 ", "Connecting to ") + unicast + " …");

            string saved = null;
            try { if (File.Exists(LastDeviceFile)) saved = File.ReadAllText(LastDeviceFile).Trim(); } catch { }
            var prev = cmbDev.SelectedItem as DlnaDevice;
            string prevUdn = prev != null ? prev.Udn : null;
            var extra = saved != null && unicast == null ? new[] { saved } : null;

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<DlnaDevice> found;
                try { found = DlnaDiscovery.Discover(3000, unicast, extra); }
                catch { found = new List<DlnaDevice>(); }
                UI(delegate
                {
                    searching = false;
                    btnSearch.Enabled = btnManual.Enabled = true;
                    btnSearch.Text = L.T("搜索设备", "Search");
                    if (unicast != null)
                    {
                        if (found.Count == 0)
                        {
                            setStatus(L.T(unicast + " 上没有找到 DLNA 投屏服务。", "No DLNA cast service found at " + unicast + "."));
                            Warn(L.T(unicast + " 上没有找到 DLNA 投屏服务。\n\n请确认 IP 正确、投影仪已开机并连接同一个 WiFi。", "No DLNA cast service found at " + unicast + ".\n\nCheck that the IP is correct and that the projector is on and connected to the same Wi-Fi."));
                            return;
                        }
                        foreach (var d in found)
                        {
                            bool exists = false;
                            foreach (DlnaDevice x in cmbDev.Items) if (x.Udn == d.Udn) exists = true;
                            if (!exists) cmbDev.Items.Add(d);
                        }
                        foreach (DlnaDevice x in cmbDev.Items) if (x.Udn == found[0].Udn) cmbDev.SelectedItem = x;
                        setStatus(L.T("已连接：", "Connected: ") + found[0].Name);
                        return;
                    }

                    cmbDev.BeginUpdate();
                    cmbDev.Items.Clear();
                    foreach (var d in found) cmbDev.Items.Add(d);
                    cmbDev.EndUpdate();
                    DlnaDevice pick = null;
                    foreach (var d in found) if (prevUdn != null && d.Udn == prevUdn) pick = d;
                    if (pick == null) foreach (var d in found) if (saved != null && d.Location == saved) pick = d;
                    if (pick == null && found.Count > 0) pick = found[0];
                    cmbDev.SelectedItem = pick;

                    if (found.Count == 0)
                        setStatus(L.T("没有找到投影仪。请确认投影仪已开机，且和电脑连在同一个 WiFi（同为 2.4G 或同为 5G），或点“输入 IP…”。", "No projector found. Make sure it is on and connected to the same Wi-Fi as this PC (both 2.4 GHz or both 5 GHz), or click \"Enter IP…\"."));
                    else
                        setStatus(L.T("找到 ", "Found ") + found.Count + L.T(" 个投屏设备。把视频拖进播放列表，双击即可在投影仪上播放。", " device(s). Drag videos into the playlist and double-click to play on the projector."));
                });
            });
        }

        void RefreshVolume(DlnaDevice d)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                int v;
                try { v = d.GetVolume(); } catch { return; }
                if (v < 0) return;
                UI(delegate
                {
                    updatingVolume = true;
                    volume.Value = Math.Max(0, Math.Min(100, v));
                    lblVolVal.Text = volume.Value.ToString();
                    updatingVolume = false;
                });
            });
        }

        // ---------- 播放列表 ----------

        void AddPaths(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    var files = new List<string>();
                    try
                    {
                        foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                            if (MediaTypes.IsMedia(f)) files.Add(f);
                    }
                    catch { }
                    files.Sort(StringComparer.CurrentCultureIgnoreCase);
                    foreach (string f in files) { AddFile(f); added++; }
                }
                else if (File.Exists(p)) { AddFile(p); added++; }
            }
            if (added > 0) setStatus(L.T("已添加 ", "Added ") + added + L.T(" 个文件。双击列表中的项目开始播放。", " file(s). Double-click an item to start playback."));
        }

        void AddFile(string path)
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            AddItem(new PlaylistItem { Path = path, Name = Path.GetFileName(path), Size = size });
        }

        void AddItem(PlaylistItem item)
        {
            var lvi = new ListViewItem(new[] { item.Name, item.IsUrl ? L.T("网络链接", "Web link") : FormatSize(item.Size) }) { Tag = item };
            lvi.ToolTipText = item.IsUrl ? item.Url : item.Path;
            list.Items.Add(lvi);
            if (list.SelectedItems.Count == 0) lvi.Selected = true;
        }

        static string FormatSize(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            return (b / 1024.0).ToString("0") + " KB";
        }

        PlaylistItem Neighbor(int delta)
        {
            if (list.Items.Count == 0) return null;
            int idx = -1;
            for (int i = 0; i < list.Items.Count; i++) if (list.Items[i].Tag == nowPlaying) idx = i;
            if (idx < 0 && list.SelectedIndices.Count > 0) idx = list.SelectedIndices[0];
            int n = idx + delta;
            if (idx < 0) n = 0;
            return n >= 0 && n < list.Items.Count ? (PlaylistItem)list.Items[n].Tag : null;
        }

        void MarkNowPlaying()
        {
            foreach (ListViewItem it in list.Items)
            {
                bool cur = it.Tag == nowPlaying;
                it.Font = new Font(list.Font, cur ? FontStyle.Bold : FontStyle.Regular);
                it.ForeColor = cur ? Color.FromArgb(0, 95, 184) : list.ForeColor;
                if (cur) it.EnsureVisible();
            }
        }

        // ---------- 播放 ----------

        void OnPlayClicked()
        {
            var sel = SelectedItem();
            if (nowPlaying != null && playingOn != null && lastState == "PAUSED_PLAYBACK" && (sel == null || sel == nowPlaying))
            {
                var d = playingOn;
                RunBg(delegate { d.Play(); }, L.T("继续播放失败", "Resume failed"));
                return;
            }
            if (sel == null && list.Items.Count > 0) sel = (PlaylistItem)list.Items[0].Tag;
            if (sel == null) { Warn(L.T("请先把视频添加到播放列表。", "Add a video to the playlist first.")); return; }
            PlayItem(sel);
        }

        void PlayItem(PlaylistItem item)
        {
            var dev = cmbDev.SelectedItem as DlnaDevice;
            if (dev == null) { Warn(L.T("还没有选择投影仪。请点“搜索设备”，或用“输入 IP…”手动添加。", "No projector selected. Click \"Search\", or add one manually with \"Enter IP…\".")); return; }

            string url;
            if (item.IsUrl) url = item.Url;
            else
            {
                if (!File.Exists(item.Path)) { Warn(L.T("文件不存在：\n", "File not found:\n") + item.Path); return; }
                try { server.Start(); }
                catch (Exception ex) { Warn(L.T("无法启动本机媒体服务：", "Could not start the local media server: ") + ex.Message); return; }
                EnsureFirewallRule();
                string ip;
                try { ip = LocalIpFor(dev.Host); }
                catch { Warn(L.T("无法确定本机在局域网中的地址，请检查网络连接。", "Could not determine this PC's local network address. Please check your network connection.")); return; }
                url = server.UrlFor(item.Path, ip);
            }
            string mime = MediaTypes.Mime(item.IsUrl ? item.Url : item.Path);
            string meta = MediaTypes.Didl(item.Name, url, mime, item.Size, !item.IsUrl);

            nowPlaying = item;
            playingOn = dev;
            userStopped = false;
            lastState = "TRANSITIONING";
            lastPos = lastDur = TimeSpan.Zero;
            ignoreEndUntil = DateTime.Now.AddSeconds(10);
            seek.Value = 0;
            lblNow.Text = L.T("正在加载：", "Loading: ") + item.Name;
            lblTime.Text = "00:00:00 / 00:00:00";
            MarkNowPlaying();
            setStatus(L.T("正在推送到 ", "Sending to ") + dev.Name + " …");

            RunBg(delegate
            {
                try { dev.Stop(); } catch { }
                dev.SetUri(url, meta);
                Thread.Sleep(300);
                dev.Play();
                UI(delegate { if (nowPlaying == item) setStatus(L.T("已推送到 ", "Sent to ") + dev.Name + L.T("：", ": ") + item.Name); });
            }, L.T("推送失败", "Send failed"));
        }

        void DoSeek()
        {
            var d = playingOn;
            if (d == null || nowPlaying == null || lastDur <= TimeSpan.Zero) return;
            var t = TimeSpan.FromSeconds(seek.Value);
            seekHoldUntil = DateTime.Now.AddSeconds(3);
            ignoreEndUntil = DateTime.Now.AddSeconds(5);
            RunBg(delegate { d.Seek(t); }, L.T("跳转失败", "Seek failed"));
        }

        void Poll(object _)
        {
            var dev = playingOn;
            if (dev == null || nowPlaying == null) return;
            if (Interlocked.Exchange(ref polling, 1) == 1) return;
            try
            {
                string state = dev.GetState();
                TimeSpan pos = TimeSpan.Zero, dur = TimeSpan.Zero;
                try { dev.GetPosition(out pos, out dur); } catch { }
                UI(delegate { OnPolled(dev, state, pos, dur); });
            }
            catch { }
            finally { Interlocked.Exchange(ref polling, 0); }
        }

        void OnPolled(DlnaDevice dev, string state, TimeSpan pos, TimeSpan dur)
        {
            if (dev != playingOn || nowPlaying == null) return;
            bool wasActive = lastState == "PLAYING" || lastState == "PAUSED_PLAYBACK" || lastState == "TRANSITIONING";
            bool stopped = state == "STOPPED" || state == "NO_MEDIA_PRESENT";
            bool ended = stopped && wasActive && !userStopped && DateTime.Now > ignoreEndUntil &&
                         lastDur > TimeSpan.Zero && lastPos >= lastDur - TimeSpan.FromSeconds(10);

            if (dur > TimeSpan.Zero) lastDur = dur;
            if (state == "PLAYING" || state == "PAUSED_PLAYBACK") lastPos = pos;
            if (!(stopped && DateTime.Now < ignoreEndUntil)) lastState = state; // 刚推送时短暂的 STOPPED 不算

            string text;
            switch (state)
            {
                case "PLAYING": text = L.T("正在播放", "Playing"); break;
                case "PAUSED_PLAYBACK": text = L.T("已暂停", "Paused"); break;
                case "TRANSITIONING": text = L.T("正在加载", "Loading"); break;
                case "STOPPED": case "NO_MEDIA_PRESENT": text = DateTime.Now < ignoreEndUntil ? L.T("正在加载", "Loading") : L.T("已停止", "Stopped"); break;
                default: text = state; break;
            }
            lblNow.Text = text + L.T("：", ": ") + nowPlaying.Name;

            if (!seeking && DateTime.Now > seekHoldUntil)
            {
                int max = (int)Math.Max(1, lastDur.TotalSeconds);
                if (seek.Maximum != max) seek.Maximum = max;
                seek.Value = Math.Max(0, Math.Min(max, (int)pos.TotalSeconds));
                lblTime.Text = DlnaDevice.FormatTime(pos) + " / " + DlnaDevice.FormatTime(lastDur);
            }

            if (ended)
            {
                if (chkAutoNext.Checked)
                {
                    var next = Neighbor(1);
                    if (next != null) { PlayItem(next); return; }
                }
                setStatus(L.T("播放完毕。", "Playback finished."));
            }
        }

        // ---------- 工具 ----------

        static string LocalIpFor(string host)
        {
            using (var u = new UdpClient())
            {
                u.Connect(host, 9);
                return ((IPEndPoint)u.Client.LocalEndPoint).Address.ToString();
            }
        }

        /// <summary>投影仪需要从本机读取视频，第一次使用时添加入站防火墙规则（需管理员确认一次）</summary>
        void EnsureFirewallRule()
        {
            if (firewallChecked) return;
            firewallChecked = true;
            try
            {
                var check = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + FirewallRuleName + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(check))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0) return;
                }
            }
            catch { }

            var r = MessageBox.Show(FindForm(),
                L.T("投影仪需要通过局域网读取本机的视频文件。\n\n首次使用需要在 Windows 防火墙中放行端口 ", "The projector needs to read video files from this PC over the local network.\n\nThe first time, Windows Firewall needs to allow ports ") + MediaServer.FirstPort + "-" + MediaServer.LastPort +
                L.T("（仅 TCP 入站），接下来会弹出一次管理员确认。\n\n现在添加吗？", " (inbound TCP only). You will see one administrator prompt.\n\nAdd the rule now?"),
                L.T("防火墙设置", "Firewall"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return;
            try
            {
                var add = new ProcessStartInfo("netsh",
                    "advfirewall firewall add rule name=\"" + FirewallRuleName + "\" dir=in action=allow protocol=TCP localport=" +
                    MediaServer.FirstPort + "-" + MediaServer.LastPort + " profile=any")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(add)) p.WaitForExit(30000);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                setStatus(L.T("未添加防火墙规则，投影仪可能无法读取本机视频。", "No firewall rule was added. The projector may not be able to read local videos."));
            }
        }

        void RunBg(Action action, string errorPrefix)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { action(); }
                catch (Exception ex)
                {
                    UI(delegate
                    {
                        setStatus(errorPrefix + L.T("：", ": ") + ex.Message);
                        if (errorPrefix == L.T("推送失败", "Send failed"))
                        {
                            lblNow.Text = L.T("推送失败：", "Send failed: ") + (nowPlaying != null ? nowPlaying.Name : "");
                            Warn(L.T("推送失败：", "Send failed: ") + ex.Message + L.T("\n\n请确认投影仪处于开机状态且在同一个 WiFi 下。", "\n\nMake sure the projector is on and connected to the same Wi-Fi."));
                        }
                    });
                }
            });
        }

        void UI(Action a)
        {
            var f = FindForm();
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
            try { f.BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void Warn(string text)
        {
            MessageBox.Show(FindForm(), text, L.T("推送视频", "Send video"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    #endregion

    #region 主窗口

    class CastConfig
    {
        public bool WindowMode;
        public IntPtr Hwnd;
        public string SourceDevice;
        public string TargetDevice;
        public string SourceName;
    }

    class MainForm : Form
    {
        const int HOTKEY_ID = 0x5150;
        const string HOTKEY_TEXT = "Ctrl+Shift+F9";

        RadioButton rbWindow, rbDesktop;
        ListView list;
        ImageList images;
        Button btnRefresh, btnIdentify, btnExtend, btnWireless, btnDisplaySettings, btnStart, btnStop;
        ComboBox cmbTarget;
        CheckBox chkAspect, chkClientOnly, chkCursor;
        NumericUpDown numFps;
        ToolStripStatusLabel status;
        TabControl tabs;
        Panel screenPanel;
        DlnaPanel dlna;
        readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer displayChangeTimer = new System.Windows.Forms.Timer();

        List<MonitorInfo> monitors = new List<MonitorInfo>();
        ProjectorForm projector;
        CastConfig current;
        int iconSize = 16;

        public MainForm()
        {
            BuildUi();
            statusTimer.Interval = 500;
            statusTimer.Tick += delegate { UpdateStatus(); };
            displayChangeTimer.Interval = 800;
            displayChangeTimer.Tick += delegate { displayChangeTimer.Stop(); HandleDisplayChange(); };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        void BuildUi()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font(L.FontName, 9F);
            Text = L.T("投影仪投屏工具", "ProjectorCast");
            ClientSize = new Size(596, 670);
            MinimumSize = new Size(540, 600);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ① 来源
            var grpSource = new GroupBox { Text = L.T("① 选择要投屏的内容", "① Choose what to cast"), Location = new Point(12, 10), Size = new Size(556, 330), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            rbWindow = new RadioButton { Text = L.T("应用窗口", "App window"), Location = new Point(16, 26), AutoSize = true, Checked = true };
            rbDesktop = new RadioButton { Text = L.T("整个桌面（显示器）", "Entire desktop (display)"), Location = new Point(120, 26), AutoSize = true };
            btnRefresh = new Button { Text = L.T("刷新列表", "Refresh"), Location = new Point(450, 22), Size = new Size(90, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            images = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
            list = new ListView
            {
                Location = new Point(16, 60), Size = new Size(524, 256),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                SmallImageList = images, HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            list.Columns.Add(L.T("窗口标题", "Window title"));
            list.Columns.Add(L.T("程序", "Program"));
            grpSource.Controls.AddRange(new Control[] { rbWindow, rbDesktop, btnRefresh, list });

            // ② 目标
            var grpTarget = new GroupBox { Text = L.T("② 选择投影仪（输出到哪个显示器）", "② Choose the projector (output display)"), Location = new Point(12, 350), Size = new Size(556, 108), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            cmbTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(16, 28), Size = new Size(426, 28), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            btnIdentify = new Button { Text = L.T("识别显示器", "Identify"), Location = new Point(450, 26), Size = new Size(90, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnExtend = new Button { Text = L.T("设为扩展模式", "Extend mode"), Location = new Point(16, 64), Size = new Size(120, 30) };
            btnWireless = new Button { Text = L.T("连接无线投影…", "Connect wireless…"), Location = new Point(144, 64), Size = new Size(124, 30) };
            btnDisplaySettings = new Button { Text = L.T("显示设置", "Display settings"), Location = new Point(276, 64), Size = new Size(L.P(90, 124), 30) };
            grpTarget.Controls.AddRange(new Control[] { cmbTarget, btnIdentify, btnExtend, btnWireless, btnDisplaySettings });

            // ③ 选项
            var grpOpt = new GroupBox { Text = L.T("③ 选项", "③ Options"), Location = new Point(12, 466), Size = new Size(556, 64), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            chkAspect = new CheckBox { Text = L.T("保持宽高比", "Keep aspect ratio"), Location = new Point(16, 28), AutoSize = true, Checked = true };
            chkClientOnly = new CheckBox { Text = L.T("隐藏窗口标题栏", "Hide title bar"), Location = new Point(L.P(118, 150), 28), AutoSize = true };
            chkCursor = new CheckBox { Text = L.T("显示鼠标指针", "Show cursor"), Location = new Point(L.P(250, 268), 28), AutoSize = true, Checked = true };
            var lblFps = new Label { Text = L.T("帧率", "FPS"), Location = new Point(L.P(372, 380), 30), AutoSize = true };
            numFps = new NumericUpDown { Location = new Point(L.P(408, 414), 26), Size = new Size(60, 26), Minimum = 5, Maximum = 60, Value = 30 };
            grpOpt.Controls.AddRange(new Control[] { chkAspect, chkClientOnly, chkCursor, lblFps, numFps });

            btnStart = new Button { Text = L.T("▶  开始投屏", "▶  Start casting"), Location = new Point(12, 540), Size = new Size(272, 44), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Font = new Font(L.FontName, 10.5F, FontStyle.Bold) };
            btnStop = new Button { Text = L.T("■  停止投屏", "■  Stop casting"), Location = new Point(296, 540), Size = new Size(272, 44), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Font = new Font(L.FontName, 10.5F), Enabled = false };

            var strip = new StatusStrip { SizingGrip = true };
            status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            strip.Items.Add(status);
            strip.Items.Add(BuildLanguageMenu());

            // 各页内容先按固定尺寸布局（锚点据此计算），再填满标签页
            // （Dock 在 OnLoad 中设置，避免标签页初始尺寸过小把锚点距离压坏）
            screenPanel = new Panel { Size = new Size(580, 600) };
            screenPanel.Controls.AddRange(new Control[] { grpSource, grpTarget, grpOpt, btnStart, btnStop });
            dlna = new DlnaPanel(SetStatus);

            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            var pageDlna = new TabPage(L.T("推送视频（WiFi / DLNA）", "Send video (Wi-Fi / DLNA)"));
            pageDlna.Controls.Add(dlna);
            var pageScreen = new TabPage(L.T("屏幕投屏（HDMI / 扩展屏）", "Screen cast (HDMI / extended display)"));
            pageScreen.Controls.Add(screenPanel);
            tabs.TabPages.Add(pageDlna);
            tabs.TabPages.Add(pageScreen);

            Controls.Add(tabs);
            Controls.Add(strip);
            ResumeLayout(false);
            PerformLayout();

            // 事件
            rbWindow.CheckedChanged += delegate { if (rbWindow.Checked) { RefreshSources(); UpdateUiState(); } };
            rbDesktop.CheckedChanged += delegate { if (rbDesktop.Checked) { RefreshSources(); UpdateUiState(); } };
            btnRefresh.Click += delegate { RefreshMonitors(); RefreshSources(); };
            list.DoubleClick += delegate { StartFromUi(); };
            list.Resize += delegate { LayoutColumns(); };
            btnIdentify.Click += delegate { Identify(); };
            btnExtend.Click += delegate { SetExtendMode(); };
            btnWireless.Click += delegate { OpenWirelessCast(); };
            btnDisplaySettings.Click += delegate { TryStart("ms-settings:display", null); };
            btnStart.Click += delegate { StartFromUi(); };
            btnStop.Click += delegate { StopProjection(); SetStatus(L.T("投屏已停止。", "Casting stopped.")); };
            chkAspect.CheckedChanged += delegate { if (projector != null) projector.KeepAspect = chkAspect.Checked; };
            chkClientOnly.CheckedChanged += delegate
            {
                var wp = projector as WindowProjector;
                if (wp != null) wp.ClientOnly = chkClientOnly.Checked;
            };
            chkCursor.CheckedChanged += delegate
            {
                var sp = projector as ScreenProjector;
                if (sp != null) sp.ShowCursor = chkCursor.Checked;
            };
            numFps.ValueChanged += delegate
            {
                var sp = projector as ScreenProjector;
                if (sp != null) sp.Fps = (int)numFps.Value;
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            foreach (TabPage page in tabs.TabPages)
            {
                page.Controls[0].Dock = DockStyle.Fill;
            }
            using (var g = CreateGraphics()) iconSize = (int)Math.Round(16 * g.DpiX / 96f);
            images.ImageSize = new Size(iconSize, iconSize);
            RefreshMonitors();
            RefreshSources();
            UpdateUiState();
            LayoutColumns();
            statusTimer.Start();
            dlna.Start();
            tabs.SelectedIndexChanged += delegate
            {
                if (tabs.SelectedIndex == 1 && projector == null)
                    SetStatus(L.T("提示：投影仪需以“扩展”模式连接（Win+P → 扩展）。投屏中按 ", "Tip: connect the projector in \"Extend\" mode (Win+P → Extend). While casting, press ") + HOTKEY_TEXT + L.T(" 可随时停止。", " to stop at any time."));
            };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && dlna.ServingLocalPlayback)
            {
                var r = MessageBox.Show(this, L.T("投影仪正在播放本机上的视频，关闭本程序会中断播放。\n\n确定要退出吗？", "The projector is playing a video from this PC. Closing the app will stop playback.\n\nQuit anyway?"),
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_F9
            Native.RegisterHotKey(Handle, HOTKEY_ID, 0x2 | 0x4 | 0x4000, 0x78);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, HOTKEY_ID);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 /*WM_HOTKEY*/ && m.WParam.ToInt32() == HOTKEY_ID)
            {
                if (projector != null) { StopProjection(); SetStatus(L.T("已通过快捷键停止投屏。", "Casting stopped with the hotkey.")); }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            statusTimer.Stop();
            displayChangeTimer.Stop();
            StopProjection();
            dlna.Shutdown();
            base.OnFormClosed(e);
        }

        void LayoutColumns()
        {
            if (list.Columns.Count < 2) return;
            int second = (int)(list.Font.Height * (rbWindow.Checked ? 9 : 10));
            list.Columns[1].Width = second;
            list.Columns[0].Width = Math.Max(80, list.ClientSize.Width - second - 2);
        }

        // ---------- 列表 ----------

        void RefreshMonitors()
        {
            string prev = null;
            var sel = cmbTarget.SelectedItem as MonitorInfo;
            if (sel != null) prev = sel.DeviceName;

            monitors = DisplayHelper.GetMonitors();
            cmbTarget.BeginUpdate();
            cmbTarget.Items.Clear();
            foreach (var m in monitors) cmbTarget.Items.Add(m);
            cmbTarget.EndUpdate();

            MonitorInfo pick = null;
            if (prev != null) pick = monitors.Find(m => m.DeviceName == prev);
            // 默认：优先选非主屏、非内置屏（通常就是投影仪）
            if (pick == null) pick = monitors.Find(m => !m.IsPrimary && !m.IsInternal);
            if (pick == null) pick = monitors.Find(m => !m.IsPrimary);
            if (pick == null && monitors.Count > 0) pick = monitors[0];
            cmbTarget.SelectedItem = pick;
        }

        void RefreshSources()
        {
            object prevKey = null;
            if (list.SelectedItems.Count > 0)
            {
                var t = list.SelectedItems[0].Tag;
                if (t is WindowItem) prevKey = ((WindowItem)t).Handle;
                else if (t is MonitorInfo) prevKey = ((MonitorInfo)t).DeviceName;
            }

            list.BeginUpdate();
            list.Items.Clear();
            images.Images.Clear();
            if (rbWindow.Checked)
            {
                list.Columns[0].Text = L.T("窗口标题", "Window title");
                list.Columns[1].Text = L.T("程序", "Program");
                foreach (var w in WindowHelper.GetWindows(iconSize))
                {
                    var item = new ListViewItem(new[] { w.Title, w.ProcessName }) { Tag = w };
                    if (w.Icon != null)
                    {
                        string key = w.Handle.ToString();
                        images.Images.Add(key, w.Icon);
                        item.ImageKey = key;
                    }
                    list.Items.Add(item);
                    if (prevKey is IntPtr && (IntPtr)prevKey == w.Handle) item.Selected = true;
                }
            }
            else
            {
                list.Columns[0].Text = L.T("显示器", "Display");
                list.Columns[1].Text = L.T("分辨率 / 接口", "Resolution / Connection");
                foreach (var m in monitors)
                {
                    var item = new ListViewItem(new[] { m.Title, m.Detail }) { Tag = m };
                    list.Items.Add(item);
                    if (prevKey is string && (string)prevKey == m.DeviceName) item.Selected = true;
                }
                if (list.SelectedItems.Count == 0)
                    foreach (ListViewItem it in list.Items)
                        if (((MonitorInfo)it.Tag).IsPrimary) { it.Selected = true; break; }
            }
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
            if (list.SelectedItems.Count > 0) list.SelectedItems[0].EnsureVisible();
            list.EndUpdate();
            LayoutColumns();
        }

        MonitorInfo FindMonitor(string device)
        {
            return monitors.Find(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase));
        }

        // ---------- 投屏控制 ----------

        void StartFromUi()
        {
            var target = cmbTarget.SelectedItem as MonitorInfo;
            if (target == null) { Warn(L.T("请先选择投影仪所在的显示器。", "Choose the display your projector is connected to first.")); return; }

            if (monitors.Count < 2)
            {
                var r = MessageBox.Show(this,
                    L.T("当前只检测到 1 个显示器。\n\n请确认投影仪已连接，并且以“扩展”模式工作（而不是“复制”或“仅第二屏幕”）。\n\n是否现在切换到扩展模式？", "Only 1 display was detected.\n\nMake sure the projector is connected and set to \"Extend\" mode (not \"Duplicate\" or \"Second screen only\").\n\nSwitch to Extend mode now?"),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes) SetExtendMode();
                return;
            }

            var cfg = new CastConfig { TargetDevice = target.DeviceName };
            if (list.SelectedItems.Count == 0)
            {
                Warn(rbWindow.Checked ? L.T("请在列表中选择要投屏的应用窗口。", "Select the app window to cast from the list.") : L.T("请在列表中选择要投屏的显示器。", "Select the display to cast from the list."));
                return;
            }
            object tag = list.SelectedItems[0].Tag;
            if (rbWindow.Checked)
            {
                var w = (WindowItem)tag;
                cfg.WindowMode = true;
                cfg.Hwnd = w.Handle;
                cfg.SourceName = w.Title;
            }
            else
            {
                var m = (MonitorInfo)tag;
                cfg.SourceDevice = m.DeviceName;
                cfg.SourceName = m.Title;
            }

            if (target.IsPrimary && (projector == null || current.TargetDevice != target.DeviceName))
            {
                var r = MessageBox.Show(this,
                    L.T("目标是主显示器，投屏画面会覆盖整个主屏幕。\n\n停止方法：按 ", "The target is your primary display, so the cast will cover your whole main screen.\n\nTo stop: press ") + HOTKEY_TEXT + L.T("，或点击投屏画面后按 Esc。\n\n确定继续吗？", ", or click the cast and press Esc.\n\nContinue?"),
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
            }

            Start(cfg, true);
        }

        bool Start(CastConfig cfg, bool interactive)
        {
            var target = FindMonitor(cfg.TargetDevice);
            if (target == null)
            {
                if (interactive) Warn(L.T("找不到目标显示器，请点击“刷新列表”后重试。", "Target display not found. Click \"Refresh\" and try again."));
                return false;
            }

            ProjectorForm p;
            if (cfg.WindowMode)
            {
                if (!Native.IsWindow(cfg.Hwnd))
                {
                    if (interactive) { Warn(L.T("该窗口已经关闭。", "That window has been closed.")); RefreshSources(); }
                    return false;
                }
                p = new WindowProjector(target, cfg.Hwnd, cfg.SourceName, chkAspect.Checked, chkClientOnly.Checked);
            }
            else
            {
                var src = FindMonitor(cfg.SourceDevice);
                if (src == null)
                {
                    if (interactive) Warn(L.T("找不到要投屏的显示器，请点击“刷新列表”后重试。", "Source display not found. Click \"Refresh\" and try again."));
                    return false;
                }
                if (src.DeviceName == target.DeviceName)
                {
                    if (interactive) Warn(L.T("不能把显示器投屏到它自己（画面会无限嵌套）。\n\n请在列表中选择另一个显示器作为投屏内容，或更换目标显示器。", "A display can't be cast onto itself (the picture would repeat forever).\n\nChoose another display to cast, or change the target display."));
                    return false;
                }
                cfg.SourceName = src.Title;
                p = new ScreenProjector(target, src.Screen.Bounds, cfg.SourceName, chkAspect.Checked, chkCursor.Checked, (int)numFps.Value);
            }

            StopProjection();
            projector = p;
            current = cfg;
            p.SourceLost += OnSourceLost;
            p.FormClosed += OnProjectorClosed;
            p.Show();
            UpdateUiState();
            UpdateStatus();
            return true;
        }

        void StopProjection()
        {
            if (projector != null)
            {
                var p = projector;
                projector = null;
                current = null;
                p.SourceLost -= OnSourceLost;
                p.FormClosed -= OnProjectorClosed;
                p.Close();
            }
            UpdateUiState();
        }

        void OnProjectorClosed(object sender, FormClosedEventArgs e)
        {
            if (sender != projector) return;
            projector = null;
            current = null;
            UpdateUiState();
            SetStatus(L.T("投屏已停止。", "Casting stopped."));
        }

        void OnSourceLost(object sender, EventArgs e)
        {
            BeginInvoke(new Action(delegate
            {
                if (sender != projector) return;
                StopProjection();
                SetStatus(L.T("源窗口已关闭，投屏已停止。", "The source window was closed, so casting stopped."));
                if (rbWindow.Checked) RefreshSources();
            }));
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated) return;
            BeginInvoke(new Action(delegate { displayChangeTimer.Stop(); displayChangeTimer.Start(); }));
        }

        void HandleDisplayChange()
        {
            RefreshMonitors();
            if (rbDesktop.Checked) RefreshSources();
            if (current == null) return;
            var cfg = current;
            StopProjection();
            if (Start(cfg, false)) SetStatus(L.T("显示器配置已变化，已自动恢复投屏。", "Display setup changed. Casting resumed automatically."));
            else SetStatus(L.T("显示器配置已变化（投影仪可能已断开），投屏已停止。", "Display setup changed (the projector may have disconnected). Casting stopped."));
        }

        void UpdateUiState()
        {
            bool active = projector != null;
            btnStop.Enabled = active;
            btnStart.Text = active ? L.T("⇄  切换投屏内容", "⇄  Switch source") : L.T("▶  开始投屏", "▶  Start casting");
            chkClientOnly.Enabled = rbWindow.Checked;
            chkCursor.Enabled = rbDesktop.Checked;
            numFps.Enabled = rbDesktop.Checked;
        }

        void UpdateStatus()
        {
            if (projector == null) return;
            string s = L.T("● 正在投屏：", "● Casting: ") + projector.SourceName + "  →  " + projector.Target.Title;
            var sp = projector as ScreenProjector;
            if (sp != null && sp.ActualFps > 0) s += "   ·   " + sp.ActualFps.ToString("0") + " fps";
            var wp = projector as WindowProjector;
            if (wp != null && wp.SourceMinimized) s += L.T("   ⚠ 源窗口已最小化，请将其还原", "   ⚠ The source window is minimized. Please restore it");
            status.Text = s;
        }

        void SetStatus(string text) { status.Text = text; }

        void Warn(string text) { MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        // ---------- 语言 ----------

        ToolStripDropDownButton BuildLanguageMenu()
        {
            // 菜单本身中英并列，任何语言的用户都能找到
            var menu = new ToolStripDropDownButton(L.Zh ? "语言：中文" : "Language: English")
            {
                Alignment = ToolStripItemAlignment.Right,
                ToolTipText = "语言 / Language"
            };
            var items = new[]
            {
                new KeyValuePair<string, string>(L.Auto, "跟随系统 / Follow system"),
                new KeyValuePair<string, string>(L.Chinese, "中文"),
                new KeyValuePair<string, string>(L.English, "English"),
            };
            foreach (var kv in items)
            {
                string pref = kv.Key;
                var mi = new ToolStripMenuItem(kv.Value) { Checked = L.Preference == pref };
                mi.Click += delegate { ChangeLanguage(pref); };
                menu.DropDownItems.Add(mi);
            }
            return menu;
        }

        void ChangeLanguage(string preference)
        {
            if (preference == L.Preference) return;
            bool newZh = preference == L.Chinese || (preference == L.Auto && L.SystemIsChinese);
            L.Save(preference);
            if (newZh == L.Zh) { L.Preference = preference; return; }

            // 语言在启动时确定，切换需要重启程序；正在投屏或播放时先确认
            bool busy = projector != null || dlna.ServingLocalPlayback;
            string msg = newZh
                ? "需要重新启动程序才能切换到中文。" + (busy ? "\n\n当前的投屏 / 播放会中断。" : "") + "\n\n现在重启吗？"
                : "ProjectorCast needs to restart to switch to English." + (busy ? "\n\nCurrent casting / playback will stop." : "") + "\n\nRestart now?";
            var r = MessageBox.Show(this, msg, "ProjectorCast", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                SetStatus(newZh ? "下次启动时将使用中文。" : "English will be used next time you start the app.");
                return;
            }
            StopProjection();
            dlna.Shutdown();
            Application.Restart();
        }

        // ---------- 显示器工具 ----------

        void Identify()
        {
            RefreshMonitors();
            foreach (var m in monitors) new IdentifyForm(m).Show();
        }

        void SetExtendMode()
        {
            string exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");
            if (TryStart(exe, "/extend"))
                SetStatus(L.T("已请求切换到扩展模式，稍候显示器列表会自动刷新。", "Switching to Extend mode. The display list will refresh shortly."));
            else
                SendWinKey(0x50); // Win+P
        }

        void OpenWirelessCast()
        {
            SendWinKey(0x4B); // Win+K：打开“投放/连接”面板（Miracast 投影仪、电视等）
            SetStatus(L.T("已打开无线投放面板。连接成功后请在 Win+P 中选择“扩展”，然后在上方选择该显示器。", "Opened the wireless cast panel. After connecting, choose \"Extend\" in Win+P, then select that display above."));
        }

        static void SendWinKey(byte vk)
        {
            const uint KEYUP = 2;
            Native.keybd_event(0x5B, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
            Native.keybd_event(0x5B, 0, KEYUP, UIntPtr.Zero);
        }

        static bool TryStart(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = true };
                if (args != null) psi.Arguments = args;
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }
    }

    #endregion
}
