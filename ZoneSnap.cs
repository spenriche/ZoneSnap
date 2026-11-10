// ZoneSnap - a tiny window zone-snapping tool for Windows.
//
// Hold Shift while dragging a window: zones appear, drop the window on one to snap it.
// Right-click the tray icon to pick a layout, edit zones visually, or open zones.ini.
//
// Single source file, no dependencies. Rebuild after editing by running build.cmd
// (uses the C# compiler that ships with Windows, so nothing needs installing).
//
// How it stays light: no keyboard or mouse hooks. Windows tells us when a window
// starts/stops being moved; only during a drag does a 30 ms timer poll the cursor.
// No network code. Idle CPU use is zero.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZoneSnap
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    }

    // ---------------------------------------------------------------- config

    class ZoneLayout
    {
        public string Name;
        public List<RectangleF> Zones = new List<RectangleF>();   // x, y, width, height in percent of the monitor
    }

    class Config
    {
        public static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zones.ini");

        public string Key = "shift";                 // shift | ctrl | alt | none (none = always show zones while dragging)
        public int Gap = 8;                          // pixels between zones
        public int Opacity = 40;                     // overlay opacity, percent
        public Color ZoneColor = Color.FromArgb(45, 125, 210);
        public Color HighlightColor = Color.FromArgb(140, 210, 255);
        public List<ZoneLayout> Layouts = new List<ZoneLayout>();
        public Dictionary<string, string> Monitors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int Version;                          // bumped on every change so overlays know to rebuild
        DateTime stamp;

        static ZoneLayout Make(string name, params float[] v)
        {
            ZoneLayout l = new ZoneLayout();
            l.Name = name;
            for (int i = 0; i + 3 < v.Length; i += 4) l.Zones.Add(new RectangleF(v[i], v[i + 1], v[i + 2], v[i + 3]));
            return l;
        }

        void AddDefaults()
        {
            Layouts.Add(Make("Three columns", 0, 0, 33.33f, 100, 33.33f, 0, 33.34f, 100, 66.67f, 0, 33.33f, 100));
            Layouts.Add(Make("Two columns", 0, 0, 50, 100, 50, 0, 50, 100));
            Layouts.Add(Make("Main + side stack", 0, 0, 60, 100, 60, 0, 40, 50, 60, 50, 40, 50));
            Layouts.Add(Make("Grid 2x2", 0, 0, 50, 50, 50, 0, 50, 50, 0, 50, 50, 50, 50, 50, 50, 50));
        }

        public static string DeviceOf(Screen s)
        {
            string d = s.DeviceName;
            int z = d.IndexOf('\0');
            return z >= 0 ? d.Substring(0, z) : d;
        }

        public ZoneLayout Find(string name)
        {
            foreach (ZoneLayout l in Layouts)
                if (string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) return l;
            return null;
        }

        public ZoneLayout LayoutFor(Screen s)
        {
            string name;
            ZoneLayout l = null;
            if (Monitors.TryGetValue(DeviceOf(s), out name)) l = Find(name);
            if (l == null && Monitors.TryGetValue("default", out name)) l = Find(name);
            return l ?? Layouts[0];
        }

        static Color ParseColor(string hex, Color fallback)
        {
            try
            {
                int v = Convert.ToInt32(hex.Trim().TrimStart('#'), 16);
                return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
            }
            catch { return fallback; }
        }

        static string Hex(Color c) { return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        public void Load()
        {
            Layouts.Clear();
            Monitors.Clear();
            Version++;
            if (!File.Exists(FilePath)) { AddDefaults(); Save(); return; }

            string section = "";
            ZoneLayout cur = null;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string t = raw.Trim();
                if (t.Length == 0 || t[0] == '#' || t[0] == ';') continue;
                if (t[0] == '[' && t[t.Length - 1] == ']')
                {
                    section = t.Substring(1, t.Length - 2).Trim();
                    cur = null;
                    if (section.StartsWith("layout ", StringComparison.OrdinalIgnoreCase))
                    {
                        cur = new ZoneLayout();
                        cur.Name = section.Substring(7).Trim();
                        Layouts.Add(cur);
                    }
                    section = section.ToLowerInvariant();
                    continue;
                }
                if (cur != null)
                {
                    string[] p = t.Split(',');
                    float x, y, w, h;
                    if (p.Length == 4
                        && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                        && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                        && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                        && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out h)
                        && w > 0 && h > 0)
                        cur.Zones.Add(new RectangleF(x, y, w, h));
                    continue;
                }
                int eq = t.IndexOf('=');
                if (eq < 0) continue;
                string k = t.Substring(0, eq).Trim(), val = t.Substring(eq + 1).Trim();
                if (section == "monitors") { Monitors[k] = val; continue; }
                if (section != "settings") continue;
                int n;
                switch (k.ToLowerInvariant())
                {
                    case "key": Key = val.ToLowerInvariant(); break;
                    case "gap": if (int.TryParse(val, out n)) Gap = Math.Max(0, Math.Min(100, n)); break;
                    case "opacity": if (int.TryParse(val, out n)) Opacity = Math.Max(10, Math.Min(100, n)); break;
                    case "color": ZoneColor = ParseColor(val, ZoneColor); break;
                    case "highlight": HighlightColor = ParseColor(val, HighlightColor); break;
                }
            }
            if (Layouts.Count == 0) AddDefaults();
            stamp = File.GetLastWriteTimeUtc(FilePath);
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# ZoneSnap settings. Edit and save; changes are picked up on the next drag.");
            sb.AppendLine();
            sb.AppendLine("[settings]");
            sb.AppendLine("# key: shift, ctrl, alt, or none (none = zones always show while dragging)");
            sb.AppendLine("key = " + Key);
            sb.AppendLine("# gap: pixels of space between zones");
            sb.AppendLine("gap = " + Gap);
            sb.AppendLine("# opacity: 10-100");
            sb.AppendLine("opacity = " + Opacity);
            sb.AppendLine("# colors as RRGGBB");
            sb.AppendLine("color = " + Hex(ZoneColor));
            sb.AppendLine("highlight = " + Hex(HighlightColor));
            sb.AppendLine();
            sb.AppendLine("# Which layout each monitor uses. 'default' applies to any monitor not listed.");
            sb.AppendLine("[monitors]");
            if (!Monitors.ContainsKey("default")) Monitors["default"] = Layouts[0].Name;
            foreach (KeyValuePair<string, string> m in Monitors) sb.AppendLine(m.Key + " = " + m.Value);
            sb.AppendLine();
            sb.AppendLine("# Each zone is: x, y, width, height  (percent of the monitor, 0-100)");
            foreach (ZoneLayout l in Layouts)
            {
                sb.AppendLine("[layout " + l.Name + "]");
                foreach (RectangleF z in l.Zones)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:0.##}, {1:0.##}, {2:0.##}, {3:0.##}", z.X, z.Y, z.Width, z.Height));
                sb.AppendLine();
            }
            try
            {
                File.WriteAllText(FilePath, sb.ToString());
                stamp = File.GetLastWriteTimeUtc(FilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save zones.ini:\n" + ex.Message, "ZoneSnap");
            }
            Version++;
        }

        public void ReloadIfChanged()
        {
            try
            {
                if (File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != stamp) Load();
            }
            catch { }
        }

        // Percent zone -> pixel rectangle inside 'area'.
        public static Rectangle ToPixels(RectangleF z, Rectangle area, int gap)
        {
            int l = area.Left + (int)Math.Round(z.Left * area.Width / 100.0);
            int t = area.Top + (int)Math.Round(z.Top * area.Height / 100.0);
            int r = area.Left + (int)Math.Round(z.Right * area.Width / 100.0);
            int b = area.Top + (int)Math.Round(z.Bottom * area.Height / 100.0);
            Rectangle rc = Rectangle.FromLTRB(l, t, r, b);
            rc.Inflate(-gap / 2, -gap / 2);
            return rc;
        }
    }

    // --------------------------------------------------------------- overlay

    // One click-through, never-activated window per monitor that paints the zones.
    class Overlay : Form
    {
        public Screen Scr;
        public Rectangle[] Hit;      // zone areas without gap, screen coordinates (for hit testing)
        public Rectangle[] Zones;    // zone areas with gap, screen coordinates (drawn, and snapped to)
        public int Hot = -1;
        Config cfg;

        public Overlay(Screen s, Config c)
        {
            Scr = s; cfg = c;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = s.WorkingArea;
            BackColor = Color.Black;
            Opacity = c.Opacity / 100.0;
            DoubleBuffered = true;

            ZoneLayout l = c.LayoutFor(s);
            Hit = new Rectangle[l.Zones.Count];
            Zones = new Rectangle[l.Zones.Count];
            for (int i = 0; i < l.Zones.Count; i++)
            {
                Hit[i] = Config.ToPixels(l.Zones[i], s.WorkingArea, 0);
                Zones[i] = Config.ToPixels(l.Zones[i], s.WorkingArea, c.Gap);
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                // layered | transparent (click-through) | no-activate | tool window | topmost
                p.ExStyle |= 0x80000 | 0x20 | 0x08000000 | 0x80 | 0x8;
                return p;
            }
        }

        public void SetHot(int i)
        {
            if (i != Hot) { Hot = i; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Point o = Bounds.Location;
            using (SolidBrush normal = new SolidBrush(cfg.ZoneColor))
            using (SolidBrush hot = new SolidBrush(cfg.HighlightColor))
            using (Pen border = new Pen(Color.White, 2))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                for (int i = 0; i < Zones.Length; i++)
                {
                    Rectangle r = Zones[i];
                    r.Offset(-o.X, -o.Y);
                    g.FillRectangle(i == Hot ? hot : normal, r);
                    g.DrawRectangle(border, r);
                    float size = Math.Max(18, Math.Min(r.Width, r.Height) / 5);
                    using (Font f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                        g.DrawString((i + 1).ToString(), f, Brushes.White, r, sf);
                }
            }
        }
    }

    // ---------------------------------------------------------------- editor

    // Full-monitor visual editor for one layout.
    class Editor : Form
    {
        Config cfg;
        ZoneLayout layout;
        List<Rectangle> zs = new List<Rectangle>();   // zones in client pixels
        int active = -1;
        int mode;            // 0 idle, 1 drawing, 2 moving, 3 resizing
        int edges;           // 1 left, 2 top, 4 right, 8 bottom
        Point start;
        Rectangle orig;
        public bool Saved;
        const int MinSize = 40, SnapDist = 12, EdgeGrip = 10;

        public Editor(Screen s, Config c, ZoneLayout l)
        {
            cfg = c; layout = l;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = s.WorkingArea;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 28);
            Opacity = 0.9;
            DoubleBuffered = true;
            KeyPreview = true;
            Rectangle area = new Rectangle(0, 0, Bounds.Width, Bounds.Height);
            foreach (RectangleF z in l.Zones) zs.Add(Config.ToPixels(z, area, 0));
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }

        int HitZone(Point p)
        {
            for (int i = zs.Count - 1; i >= 0; i--) if (zs[i].Contains(p)) return i;
            return -1;
        }

        static int EdgesAt(Rectangle r, Point p)
        {
            int e = 0;
            if (p.X - r.Left < EdgeGrip) e |= 1;
            if (r.Right - p.X < EdgeGrip) e |= 4;
            if (p.Y - r.Top < EdgeGrip) e |= 2;
            if (r.Bottom - p.Y < EdgeGrip) e |= 8;
            return e;
        }

        static Cursor CursorFor(int e)
        {
            if (e == 3 || e == 12) return Cursors.SizeNWSE;
            if (e == 6 || e == 9) return Cursors.SizeNESW;
            if (e == 1 || e == 4) return Cursors.SizeWE;
            if (e == 2 || e == 8) return Cursors.SizeNS;
            return Cursors.SizeAll;
        }

        // Snap a coordinate to screen edges, halves/thirds/quarters, and the edges of other zones.
        int Snap(int v, bool horizontal)
        {
            int full = horizontal ? ClientSize.Width : ClientSize.Height;
            v = Math.Max(0, Math.Min(full, v));
            int best = v, bestD = SnapDist + 1;
            int[] fixedPts = { 0, full, full / 2, full / 3, full * 2 / 3, full / 4, full * 3 / 4 };
            foreach (int c in fixedPts) { int d = Math.Abs(c - v); if (d < bestD) { bestD = d; best = c; } }
            for (int i = 0; i < zs.Count; i++)
            {
                if (i == active) continue;
                int a = horizontal ? zs[i].Left : zs[i].Top, b = horizontal ? zs[i].Right : zs[i].Bottom;
                int da = Math.Abs(a - v), db = Math.Abs(b - v);
                if (da < bestD) { bestD = da; best = a; }
                if (db < bestD) { bestD = db; best = b; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = HitZone(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                if (i >= 0) { zs.RemoveAt(i); Invalidate(); }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            start = e.Location;
            if (i >= 0)
            {
                active = i; orig = zs[i];
                edges = EdgesAt(orig, e.Location);
                mode = edges != 0 ? 3 : 2;
            }
            else
            {
                zs.Add(new Rectangle(e.Location, Size.Empty));
                active = zs.Count - 1;
                mode = 1;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = e.Location;
            if (mode == 0)
            {
                int i = HitZone(p);
                Cursor = i < 0 ? Cursors.Cross : CursorFor(EdgesAt(zs[i], p));
                return;
            }
            int dx = p.X - start.X, dy = p.Y - start.Y;
            if (mode == 1)
            {
                zs[active] = Rectangle.FromLTRB(
                    Snap(Math.Min(start.X, p.X), true), Snap(Math.Min(start.Y, p.Y), false),
                    Snap(Math.Max(start.X, p.X), true), Snap(Math.Max(start.Y, p.Y), false));
            }
            else if (mode == 2)
            {
                Rectangle r = orig;
                r.Offset(dx, dy);
                int sl = Snap(r.Left, true);
                if (sl != r.Left) r.X = sl; else r.X += Snap(r.Right, true) - r.Right;
                int st = Snap(r.Top, false);
                if (st != r.Top) r.Y = st; else r.Y += Snap(r.Bottom, false) - r.Bottom;
                r.X = Math.Max(0, Math.Min(ClientSize.Width - r.Width, r.X));
                r.Y = Math.Max(0, Math.Min(ClientSize.Height - r.Height, r.Y));
                zs[active] = r;
            }
            else
            {
                int l = orig.Left, t = orig.Top, r = orig.Right, b = orig.Bottom;
                if ((edges & 1) != 0) l = Math.Min(Snap(l + dx, true), r - MinSize);
                if ((edges & 4) != 0) r = Math.Max(Snap(r + dx, true), l + MinSize);
                if ((edges & 2) != 0) t = Math.Min(Snap(t + dy, false), b - MinSize);
                if ((edges & 8) != 0) b = Math.Max(Snap(b + dy, false), t + MinSize);
                zs[active] = Rectangle.FromLTRB(l, t, r, b);
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (mode == 1 && active >= 0 && active < zs.Count
                && (zs[active].Width < MinSize || zs[active].Height < MinSize))
                zs.RemoveAt(active);
            mode = 0; active = -1;
            Invalidate();
        }

        void Fill(int cols, int rows)
        {
            zs.Clear();
            int w = ClientSize.Width, h = ClientSize.Height;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    zs.Add(Rectangle.FromLTRB(w * x / cols, h * y / rows, w * (x + 1) / cols, h * (y + 1) / rows));
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (e.KeyCode == Keys.Enter || (e.Control && e.KeyCode == Keys.S)) { SaveZones(); Close(); return; }
            if (e.KeyCode == Keys.G) { Fill(2, 2); return; }
            if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
            {
                int n = e.KeyCode - Keys.D0;
                if (e.Shift) Fill(1, n); else Fill(n, 1);
            }
        }

        void SaveZones()
        {
            if (zs.Count == 0)
            {
                MessageBox.Show(this, "Draw at least one zone before saving.", "ZoneSnap");
                return;
            }
            float w = ClientSize.Width, h = ClientSize.Height;
            layout.Zones.Clear();
            foreach (Rectangle r in zs)
                layout.Zones.Add(new RectangleF(
                    (float)Math.Round(r.X * 100f / w, 2), (float)Math.Round(r.Y * 100f / h, 2),
                    (float)Math.Round(r.Width * 100f / w, 2), (float)Math.Round(r.Height * 100f / h, 2)));
            cfg.Save();
            Saved = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float scale = g.DpiX / 96f;
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(200, cfg.ZoneColor)))
            using (Pen border = new Pen(Color.White, 2))
            using (StringFormat sf = new StringFormat())
            using (Font small = new Font("Segoe UI", 14 * scale, GraphicsUnit.Pixel))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                for (int i = 0; i < zs.Count; i++)
                {
                    Rectangle r = zs[i];
                    r.Inflate(-cfg.Gap / 2, -cfg.Gap / 2);
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    g.FillRectangle(fill, r);
                    g.DrawRectangle(border, r);
                    float size = Math.Max(18, Math.Min(r.Width, r.Height) / 5);
                    using (Font f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                        g.DrawString((i + 1).ToString(), f, Brushes.White, r, sf);
                }

                string help = "Editing \"" + layout.Name + "\"\n"
                    + "Drag empty space: new zone   |   Drag a zone: move   |   Drag an edge: resize   |   Right-click: delete\n"
                    + "1-9: that many columns   |   Shift+1-9: rows   |   G: 2x2 grid   |   Enter: save   |   Esc: cancel";
                SizeF hs = g.MeasureString(help, small);
                RectangleF box = new RectangleF((ClientSize.Width - hs.Width) / 2 - 16, 24 * scale, hs.Width + 32, hs.Height + 20);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 0, 0, 0))) g.FillRectangle(bg, box);
                g.DrawString(help, small, Brushes.White, box, sf);
            }
        }
    }

    // ------------------------------------------------------------------- app

    class App : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        Config cfg = new Config();
        NotifyIcon tray;
        List<Overlay> overlays = new List<Overlay>();
        string builtFor = "";                 // screens + config version the overlays were built for
        Timer timer = new Timer();
        Native.WinEventProc proc;             // kept in a field so the GC does not collect the callback
        IntPtr hook;
        IntPtr drag;                          // window currently being dragged
        bool shown, hasTarget;
        Rectangle target;
        Editor editor;

        public App()
        {
            cfg.Load();

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "ZoneSnap - hold Shift while dragging a window";
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += delegate { BuildMenu(); };
            tray.DoubleClick += delegate { Edit(Screen.PrimaryScreen); };
            tray.Visible = true;

            timer.Interval = 30;
            timer.Tick += delegate { Tick(); };

            // EVENT_SYSTEM_MOVESIZESTART (0x0A) .. EVENT_SYSTEM_MOVESIZEEND (0x0B), out of context, skip own windows
            proc = OnWinEvent;
            hook = Native.SetWinEventHook(0x000A, 0x000B, IntPtr.Zero, proc, 0, 0, 0x0002);
        }

        static Icon MakeIcon()
        {
            using (Bitmap b = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Transparent);
                    using (SolidBrush blue = new SolidBrush(Color.FromArgb(45, 125, 210))) g.FillRectangle(blue, 1, 3, 30, 26);
                    g.FillRectangle(Brushes.White, 4, 6, 11, 20);
                    g.FillRectangle(Brushes.White, 17, 6, 11, 9);
                    g.FillRectangle(Brushes.White, 17, 17, 11, 9);
                }
                IntPtr h = b.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        // ------------------------------------------------------------ dragging

        void OnWinEvent(IntPtr h, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (evt == 0x000A) BeginDrag(hwnd);
            else if (evt == 0x000B) EndDrag();
        }

        // True when the user grabbed a border/corner (resizing) rather than the title bar (moving).
        static bool IsResize(IntPtr hwnd)
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            IntPtr res;
            IntPtr lp = (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
            if (Native.SendMessageTimeout(hwnd, 0x0084 /*WM_NCHITTEST*/, IntPtr.Zero, lp, 2, 100, out res) == IntPtr.Zero) return false;
            int ht = (int)(res.ToInt64() & 0xFFFF);
            return ht >= 10 && ht <= 17;
        }

        bool KeyHeld()
        {
            int vk;
            switch (cfg.Key)
            {
                case "none": return true;
                case "ctrl": vk = 0x11; break;
                case "alt": vk = 0x12; break;
                default: vk = 0x10; break;
            }
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        void BeginDrag(IntPtr hwnd)
        {
            if (editor != null || hwnd == IntPtr.Zero) return;
            cfg.ReloadIfChanged();
            if (IsResize(hwnd)) return;
            drag = hwnd;
            timer.Start();
            Tick();
        }

        void Tick()
        {
            if (drag == IntPtr.Zero || !Native.IsWindow(drag)) { CancelDrag(); return; }
            if (KeyHeld())
            {
                if (!shown) ShowOverlays();
                UpdateHot();
            }
            else if (shown) HideOverlays();
        }

        void UpdateHot()
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            Point pt = new Point(p.X, p.Y);
            hasTarget = false;
            foreach (Overlay o in overlays)
            {
                int best = -1;
                long bestArea = long.MaxValue;
                for (int i = 0; i < o.Hit.Length; i++)
                {
                    if (!o.Hit[i].Contains(pt)) continue;
                    long a = (long)o.Hit[i].Width * o.Hit[i].Height;     // overlapping zones: smallest wins
                    if (a < bestArea) { bestArea = a; best = i; }
                }
                o.SetHot(best);
                if (best >= 0) { target = o.Zones[best]; hasTarget = true; }
            }
        }

        void EndDrag()
        {
            if (drag == IntPtr.Zero) return;
            timer.Stop();
            IntPtr h = drag;
            drag = IntPtr.Zero;
            bool snap = false;
            if (shown && KeyHeld()) { UpdateHot(); snap = hasTarget; }
            HideOverlays();
            if (snap) SnapWindow(h, target);
        }

        void CancelDrag()
        {
            timer.Stop();
            drag = IntPtr.Zero;
            HideOverlays();
        }

        static void SnapWindow(IntPtr h, Rectangle z)
        {
            if (Native.IsZoomed(h)) Native.ShowWindow(h, 9 /*SW_RESTORE*/);
            Native.RECT wr, fr;
            if (!Native.GetWindowRect(h, out wr)) return;
            // Windows 10/11 windows have an invisible resize border; compensate so the visible frame fills the zone.
            // (Measured once, before moving: the DWM frame bounds lag behind right after a move.)
            if (Native.DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out fr, 16) != 0) fr = wr;
            int x = z.Left - (fr.L - wr.L), y = z.Top - (fr.T - wr.T);
            int w = z.Width + (fr.L - wr.L) + (wr.R - fr.R), hgt = z.Height + (fr.T - wr.T) + (wr.B - fr.B);
            const uint flags = 0x0004 | 0x0010;   // NOZORDER | NOACTIVATE
            Native.SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, flags);
            // Moving to a monitor with different scaling can make the window resize itself; apply once more if so.
            if (Native.GetWindowRect(h, out wr) && (wr.L != x || wr.T != y || wr.R - wr.L != w || wr.B - wr.T != hgt))
                Native.SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, flags);
        }

        // ------------------------------------------------------------ overlays

        void ShowOverlays()
        {
            StringBuilder sig = new StringBuilder();
            sig.Append(cfg.Version);
            foreach (Screen s in Screen.AllScreens) sig.Append('|').Append(s.DeviceName).Append(s.WorkingArea.ToString());
            if (sig.ToString() != builtFor)
            {
                foreach (Overlay o in overlays) o.Dispose();
                overlays.Clear();
                foreach (Screen s in Screen.AllScreens) overlays.Add(new Overlay(s, cfg));
                builtFor = sig.ToString();
            }
            foreach (Overlay o in overlays) o.Show();
            shown = true;
        }

        void HideOverlays()
        {
            foreach (Overlay o in overlays) { o.Hide(); o.Hot = -1; }
            shown = false;
            hasTarget = false;
        }

        // ---------------------------------------------------------------- menu

        void BuildMenu()
        {
            cfg.ReloadIfChanged();
            ToolStripItemCollection root = tray.ContextMenuStrip.Items;
            root.Clear();
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 1) AddMonitorItems(root, screens[0]);
            else
                for (int i = 0; i < screens.Length; i++)
                {
                    ToolStripMenuItem m = new ToolStripMenuItem("Monitor " + (i + 1) + (screens[i].Primary ? " (main)" : ""));
                    AddMonitorItems(m.DropDownItems, screens[i]);
                    root.Add(m);
                }
            root.Add(new ToolStripSeparator());
            root.Add("Open settings file", null, delegate { System.Diagnostics.Process.Start("notepad.exe", "\"" + Config.FilePath + "\""); });
            ToolStripMenuItem auto = new ToolStripMenuItem("Start with Windows");
            auto.Checked = AutostartEnabled();
            auto.Click += delegate { SetAutostart(!AutostartEnabled()); };
            root.Add(auto);
            root.Add("Exit", null, delegate { Quit(); });
        }

        void AddMonitorItems(ToolStripItemCollection items, Screen s)
        {
            Screen scr = s;
            ZoneLayout current = cfg.LayoutFor(s);
            foreach (ZoneLayout l in cfg.Layouts)
            {
                ZoneLayout pick = l;
                ToolStripMenuItem it = new ToolStripMenuItem(l.Name);
                it.Checked = l == current;
                it.Click += delegate { Assign(scr, pick); };
                items.Add(it);
            }
            items.Add(new ToolStripSeparator());
            items.Add("Edit zones of \"" + current.Name + "\"...", null, delegate { Edit(scr); });
            items.Add("New layout...", null, delegate { NewLayout(scr); });
            if (cfg.Layouts.Count > 1)
                items.Add("Delete \"" + current.Name + "\"", null, delegate { DeleteLayout(current); });
        }

        void Assign(Screen s, ZoneLayout l)
        {
            cfg.Monitors[Config.DeviceOf(s)] = l.Name;
            if (Screen.AllScreens.Length == 1) cfg.Monitors["default"] = l.Name;
            cfg.Save();
        }

        void Edit(Screen s)
        {
            if (editor != null) { editor.Activate(); return; }
            editor = new Editor(s, cfg, cfg.LayoutFor(s));
            editor.FormClosed += delegate
            {
                bool saved = editor.Saved;
                editor.Dispose();
                editor = null;
                if (!saved) cfg.Load();     // discard anything half-made (e.g. a new layout that was cancelled)
            };
            editor.Show();
        }

        void NewLayout(Screen s)
        {
            string name = Prompt("Name for the new layout");
            if (string.IsNullOrEmpty(name)) return;
            name = name.Replace("[", "").Replace("]", "").Replace("=", "").Trim();
            if (name.Length == 0) return;
            if (cfg.Find(name) != null) { MessageBox.Show("A layout with that name already exists.", "ZoneSnap"); return; }
            ZoneLayout l = new ZoneLayout();
            l.Name = name;
            l.Zones.AddRange(cfg.LayoutFor(s).Zones);   // start from a copy of the current layout
            cfg.Layouts.Add(l);
            cfg.Monitors[Config.DeviceOf(s)] = name;
            if (Screen.AllScreens.Length == 1) cfg.Monitors["default"] = name;
            cfg.Version++;
            Edit(s);                                     // written to disk only if the editor is saved
        }

        void DeleteLayout(ZoneLayout l)
        {
            if (MessageBox.Show("Delete the layout \"" + l.Name + "\"?", "ZoneSnap", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            cfg.Layouts.Remove(l);
            List<string> keys = new List<string>(cfg.Monitors.Keys);
            foreach (string k in keys)
                if (string.Equals(cfg.Monitors[k], l.Name, StringComparison.OrdinalIgnoreCase)) cfg.Monitors[k] = cfg.Layouts[0].Name;
            cfg.Save();
        }

        static string Prompt(string title)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false; f.TopMost = true;
                f.ClientSize = new Size(320, 84);
                TextBox t = new TextBox(); t.SetBounds(12, 12, 296, 24);
                Button ok = new Button(); ok.Text = "OK"; ok.SetBounds(152, 46, 75, 26); ok.DialogResult = DialogResult.OK;
                Button cancel = new Button(); cancel.Text = "Cancel"; cancel.SetBounds(233, 46, 75, 26); cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? t.Text.Trim() : null;
            }
        }

        // ----------------------------------------------------------- autostart

        static bool AutostartEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("ZoneSnap") != null;
        }

        static void SetAutostart(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (on) k.SetValue("ZoneSnap", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("ZoneSnap", false);
            }
        }

        void Quit()
        {
            if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (System.Threading.Mutex m = new System.Threading.Mutex(true, "Local\\ZoneSnap_SingleInstance", out first))
            {
                if (!first) return;
                // Per-monitor DPI awareness so zone coordinates are real pixels on every monitor.
                try { if (!Native.SetProcessDpiAwarenessContext((IntPtr)(-4))) Native.SetProcessDPIAware(); }
                catch { try { Native.SetProcessDPIAware(); } catch { } }
                Application.EnableVisualStyles();
                Application.Run(new App());
            }
        }
    }
}
