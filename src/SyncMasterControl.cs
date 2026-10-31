// SyncMaster Control - portable DDC/CI monitor control panel.
// Built with the C# compiler that ships with Windows (see build.ps1); needs nothing installed.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("SyncMaster Control")]
[assembly: System.Reflection.AssemblyProduct("SyncMaster Control")]
[assembly: System.Reflection.AssemblyDescription("Monitor settings over DDC/CI")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

// ============================================================================
// DDC/CI access: NVIDIA driver (NvAPI I2C) first, Windows dxva2 as fallback
// ============================================================================
public class Mon {
    public string Name;
    public bool Nv;        // true = NVIDIA I2C, false = Windows dxva2
    public IntPtr Handle;  // NVIDIA GPU handle or Windows physical monitor handle
    public uint Mask;      // NVIDIA output mask
}

public static class Ddc {
    // ---------- Windows dxva2 ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("dxva2.dll")]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")]
    static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")]
    static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);

    // ---------- NVIDIA NvAPI ----------
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NvQuery(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvEnumGpus([Out] IntPtr[] gpus, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvGetOutputs(IntPtr gpu, out uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvI2C(IntPtr gpu, IntPtr info, [In, Out] uint[] unknown);
    static T Nv<T>(uint id) {
        IntPtr p = NvQuery(id);
        if (p == IntPtr.Zero) throw new Exception("NvAPI function missing");
        return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    // Fills an NV_I2C_INFO_V3 struct (64 bytes on x64) and runs one I2C transfer
    static bool NvXfer(Mon m, bool write, byte addr, byte[] reg, byte[] data) {
        IntPtr info = Marshal.AllocHGlobal(64), pReg = IntPtr.Zero, pData = Marshal.AllocHGlobal(data.Length);
        try {
            for (int i = 0; i < 64; i++) Marshal.WriteByte(info, i, 0);
            if (reg != null) { pReg = Marshal.AllocHGlobal(reg.Length); Marshal.Copy(reg, 0, pReg, reg.Length); }
            if (write) Marshal.Copy(data, 0, pData, data.Length);
            Marshal.WriteInt32(info, 0, 64 | (3 << 16));     // version
            Marshal.WriteInt32(info, 4, (int)m.Mask);         // displayMask
            Marshal.WriteByte(info, 8, 1);                    // bIsDDCPort
            Marshal.WriteByte(info, 9, addr);                 // i2cDevAddress
            Marshal.WriteIntPtr(info, 16, pReg);              // pbI2cRegAddress
            Marshal.WriteInt32(info, 24, reg == null ? 0 : reg.Length);
            Marshal.WriteIntPtr(info, 32, pData);             // pbData
            Marshal.WriteInt32(info, 40, data.Length);        // cbSize
            Marshal.WriteInt32(info, 44, 0xFFFF);             // i2cSpeed (deprecated)
            NvI2C fn = Nv<NvI2C>(write ? 0x283AC65Au : 0x4D7B0709u);
            int r = fn(m.Handle, info, new uint[] { 0, 0 });
            if (!write) Marshal.Copy(pData, data, 0, data.Length);
            return r == 0;
        } finally {
            Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(pData);
            if (pReg != IntPtr.Zero) Marshal.FreeHGlobal(pReg);
        }
    }

    static string EdidName(Mon m) {
        var edid = new byte[128];
        if (!NvXfer(m, false, 0xA0, new byte[] { 0 }, edid)) return null;
        for (int d = 54; d <= 108; d += 18) {
            if (edid[d] == 0 && edid[d + 1] == 0 && edid[d + 3] == 0xFC) {
                string s = System.Text.Encoding.ASCII.GetString(edid, d + 5, 13);
                int nl = s.IndexOf('\n');
                return (nl >= 0 ? s.Substring(0, nl) : s).Trim();
            }
        }
        return null;
    }

    static bool NvRead(Mon m, byte code, out uint cur, out uint max) {
        cur = max = 0;
        byte[] req = { 0x82, 0x01, code, (byte)(0x6E ^ 0x51 ^ 0x82 ^ 0x01 ^ code) };
        if (!NvXfer(m, true, 0x6E, new byte[] { 0x51 }, req)) return false;
        Thread.Sleep(50);   // DDC/CI needs >=40ms before the reply
        var r = new byte[12];
        if (!NvXfer(m, false, 0x6E, null, r)) return false;
        // Reply: 6E 88 02 rc code type maxHi maxLo curHi curLo checksum
        if (r[0] != 0x6E || r[1] != 0x88 || r[2] != 0x02 || r[3] != 0x00 || r[4] != code) return false;
        byte chk = 0x50;
        for (int i = 0; i < 10; i++) chk ^= r[i];
        if (chk != r[10]) return false;
        max = (uint)(r[6] << 8 | r[7]);
        cur = (uint)(r[8] << 8 | r[9]);
        return true;
    }

    static bool NvWrite(Mon m, byte code, uint value) {
        byte hi = (byte)(value >> 8), lo = (byte)value;
        byte[] req = { 0x84, 0x03, code, hi, lo, (byte)(0x6E ^ 0x51 ^ 0x84 ^ 0x03 ^ code ^ hi ^ lo) };
        bool ok = NvXfer(m, true, 0x6E, new byte[] { 0x51 }, req);
        Thread.Sleep(50);
        return ok;
    }

    public static Mon[] GetMonitors() {
        var result = new List<Mon>();
        try {
            if (Nv<NvInit>(0x0150E828)() == 0) {
                var gpus = new IntPtr[64]; uint n;
                Nv<NvEnumGpus>(0xE5AC921F)(gpus, out n);
                for (int g = 0; g < n; g++) {
                    uint mask;
                    if (Nv<NvGetOutputs>(0x1730BFC9)(gpus[g], out mask) != 0) continue;
                    for (int b = 0; b < 32; b++) {
                        if ((mask & (1u << b)) == 0) continue;
                        var m = new Mon { Nv = true, Handle = gpus[g], Mask = 1u << b };
                        m.Name = (EdidName(m) ?? "Display") + "  (NVIDIA)";
                        result.Add(m);
                    }
                }
            }
        } catch (Exception) { }   // no NVIDIA driver: fall through to Windows

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, r, d) => {
            uint n;
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(h, out n) && n > 0) {
                var arr = new PHYSICAL_MONITOR[n];
                if (GetPhysicalMonitorsFromHMONITOR(h, n, arr))
                    foreach (var p in arr)
                        result.Add(new Mon { Name = p.szPhysicalMonitorDescription + "  (Windows)", Handle = p.hPhysicalMonitor });
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    // Returns {current, max} or null if the monitor did not answer
    public static uint[] Read(Mon m, byte code) {
        for (int i = 0; i < 3; i++) {   // DDC is flaky; retry a few times
            uint cur, max;
            bool ok = m.Nv ? NvRead(m, code, out cur, out max)
                           : GetVCPFeatureAndVCPFeatureReply(m.Handle, code, IntPtr.Zero, out cur, out max);
            if (ok) return new uint[] { cur, max };
            Thread.Sleep(50);
        }
        return null;
    }

    public static bool Write(Mon m, byte code, uint value) {
        for (int i = 0; i < 3; i++) {
            if (m.Nv ? NvWrite(m, code, value) : SetVCPFeature(m.Handle, code, value)) return true;
            Thread.Sleep(50);
        }
        return false;
    }
}

// ============================================================================
// Look and feel
// ============================================================================
static class Theme {
    public static readonly Color Bg       = Color.FromArgb(0x14, 0x16, 0x1B);
    public static readonly Color Card     = Color.FromArgb(0x1E, 0x21, 0x28);
    public static readonly Color Border   = Color.FromArgb(0x2B, 0x2F, 0x38);
    public static readonly Color Text     = Color.FromArgb(0xE8, 0xEA, 0xED);
    public static readonly Color Sub      = Color.FromArgb(0x95, 0x9C, 0xA6);
    public static readonly Color Track    = Color.FromArgb(0x33, 0x37, 0x42);
    public static readonly Color Chip     = Color.FromArgb(0x2A, 0x2E, 0x37);
    public static readonly Color ChipHov  = Color.FromArgb(0x36, 0x3B, 0x46);
    public static readonly Color Accent   = Color.FromArgb(0x4C, 0x8D, 0xFF);
    public static readonly Color Danger   = Color.FromArgb(0xE5, 0x53, 0x4B);
    public static readonly Color Ok       = Color.FromArgb(0x3F, 0xB9, 0x50);
    public static readonly Color Red      = Color.FromArgb(0xF2, 0x5C, 0x54);
    public static readonly Color Green    = Color.FromArgb(0x3F, 0xB9, 0x50);
    public static readonly Color Blue     = Color.FromArgb(0x4C, 0x8D, 0xFF);

    public static readonly Font Title   = new Font("Segoe UI Semibold", 16f);
    public static readonly Font Body    = new Font("Segoe UI", 10f);
    public static readonly Font Small   = new Font("Segoe UI", 8.5f);
    public static readonly Font Section = new Font("Segoe UI Semibold", 8.5f);
    public static readonly Font Value   = new Font("Segoe UI Semibold", 10f);

    public static float Scale = 1f;
    public static int S(float v) { return (int)Math.Round(v * Scale); }

    public static GraphicsPath Round(RectangleF r, float rad) {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

class Card : Panel {
    public string Title;
    public Card(string title) {
        Title = title;
        DoubleBuffered = true;
        BackColor = Theme.Card;
    }
    protected override void OnPaintBackground(PaintEventArgs e) {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(12)))
        using (var b = new SolidBrush(Theme.Card))
        using (var pen = new Pen(Theme.Border)) {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
        }
        TextRenderer.DrawText(g, Title.ToUpperInvariant(), Theme.Section, new Point(Theme.S(18), Theme.S(14)), Theme.Sub,
            TextFormatFlags.NoPrefix);
    }
}

class FlatBtn : Control {
    public bool Selected, Danger;
    bool hover, down;
    public FlatBtn(string text) {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        BackColor = Theme.Card;
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnKeyUp(KeyEventArgs e) {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
        base.OnKeyUp(e);
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill, fg;
        if (!Enabled)                    { fill = Theme.Chip;   fg = Theme.Sub; }
        else if (Selected)               { fill = Theme.Accent; fg = Color.White; }
        else if (Danger && hover)        { fill = Theme.Danger; fg = Color.White; }
        else if (Danger)                 { fill = Theme.Chip;   fg = Theme.Danger; }
        else                             { fill = hover ? Theme.ChipHov : Theme.Chip; fg = Theme.Text; }
        if (down && Enabled) fill = ControlPaint.Dark(fill, 0.05f);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var p = Theme.Round(r, Theme.S(8)))
        using (var b = new SolidBrush(fill)) {
            g.FillPath(b, p);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.Accent, 1.5f)) g.DrawPath(pen, p);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

class ModernSlider : Control {
    public string Caption, Note;
    public Color Accent = Theme.Accent;
    public event EventHandler ValueChanging;   // while dragging: send live
    public event EventHandler ValueCommitted;  // released: remember it
    int max = 100, val;
    bool drag;

    public ModernSlider(string caption) {
        Caption = caption;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public int Maximum { get { return max; } set { max = Math.Max(1, value); val = Math.Min(val, max); Invalidate(); } }
    public int Value { get { return val; } }
    public void SetValueSilently(int v) { val = Math.Max(0, Math.Min(max, v)); Invalidate(); }

    Rectangle TrackRect() {
        int pad = Theme.S(10);
        return new Rectangle(pad, Height - Theme.S(17), Width - pad * 2, Theme.S(6));
    }

    void Change(int v, bool commit) {
        v = Math.Max(0, Math.Min(max, v));
        if (v != val) {
            val = v;
            Note = null;
            Invalidate();
            if (ValueChanging != null) ValueChanging(this, EventArgs.Empty);
        }
        if (commit && ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
    }

    int ValueAt(int x) {
        var t = TrackRect();
        return (int)Math.Round((x - t.Left) / (double)t.Width * max);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        drag = true;
        Change(ValueAt(e.X), false);
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (drag) Change(ValueAt(e.X), false); }
    protected override void OnMouseUp(MouseEventArgs e) {
        if (!drag) return;
        drag = false;
        Change(val, true);
    }
    protected override void OnMouseWheel(MouseEventArgs e) {
        if (Enabled) Change(val + (e.Delta > 0 ? 2 : -2), true);
    }
    protected override bool IsInputKey(Keys k) {
        return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
    }
    protected override void OnKeyDown(KeyEventArgs e) {
        int d = 0;
        switch (e.KeyCode) {
            case Keys.Left: case Keys.Down: d = -1; break;
            case Keys.Right: case Keys.Up: d = 1; break;
            case Keys.PageDown: d = -10; break;
            case Keys.PageUp: d = 10; break;
            case Keys.Home: d = -max; break;
            case Keys.End: d = max; break;
        }
        if (d != 0 && Enabled) { Change(val + d, true); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var head = new Rectangle(0, 0, Width, Theme.S(22));
        TextRenderer.DrawText(g, Caption, Theme.Body, head, Enabled ? Theme.Text : Theme.Sub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        string vs = Enabled ? val.ToString() : "--";
        var vsz = TextRenderer.MeasureText(g, vs, Theme.Value);
        TextRenderer.DrawText(g, vs, Theme.Value, head, Enabled ? Theme.Text : Theme.Sub,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        if (!string.IsNullOrEmpty(Note)) {
            var nr = new Rectangle(0, 0, Width - vsz.Width - Theme.S(4), head.Height);
            TextRenderer.DrawText(g, Note, Theme.Small, nr, Theme.Sub, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        var t = TrackRect();
        Color acc = Enabled ? Accent : Theme.Track;
        using (var p = Theme.Round(t, t.Height / 2f))
        using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
        float fw = t.Width * (val / (float)max);
        if (Enabled && fw > 0)
            using (var p = Theme.Round(new RectangleF(t.Left, t.Top, Math.Max(fw, t.Height), t.Height), t.Height / 2f))
            using (var b = new SolidBrush(acc)) g.FillPath(b, p);

        if (Enabled) {
            float cx = t.Left + fw, cy = t.Top + t.Height / 2f, rad = Theme.S(drag ? 9 : 8);
            if (Focused || drag)
                using (var b = new SolidBrush(Color.FromArgb(60, acc)))
                    g.FillEllipse(b, cx - rad - Theme.S(5), cy - rad - Theme.S(5), (rad + Theme.S(5)) * 2, (rad + Theme.S(5)) * 2);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
            using (var pen = new Pen(acc, Theme.S(2))) g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
        }
    }
}

// ============================================================================
// Main window
// ============================================================================
class MainForm : Form {
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // One background thread does all DDC traffic, in order, so the UI never freezes
    readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
    readonly Dictionary<byte, uint> pending = new Dictionary<byte, uint>();
    readonly Dictionary<string, int> settings;
    readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SyncMasterControl.ini");

    Mon[] monitors = new Mon[0];
    volatile Mon current;

    ComboBox picker;
    Label status;
    Panel statusDot;
    readonly List<ModernSlider> sliders = new List<ModernSlider>();
    readonly List<FlatBtn> modeChips = new List<FlatBtn>();
    readonly List<Control> monitorControls = new List<Control>();
    readonly ToolTip tips = new ToolTip();

    // Picture mode values from the PX2370's capabilities string
    static readonly byte[] Modes = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0xF0, 0xF1, 0xF2, 0xF9, 0xFA, 0xFB };

    public MainForm() {
        settings = LoadSettings();
        var worker = new Thread(() => {
            foreach (var a in queue.GetConsumingEnumerable()) {
                try { a(); } catch (Exception) { }
            }
        });
        worker.IsBackground = true;
        worker.Start();

        Text = "SyncMaster Control";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        DoubleBuffered = true;
        ClientSize = new Size(Theme.S(860), Theme.S(604));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        tips.InitialDelay = 300;

        BuildHeader();
        BuildDisplayCard();
        BuildColorCard();
        BuildModeCard();
        BuildInputCard();
        BuildStatus();
        SetControlsEnabled(false);
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        int on = 1;   // dark title bar on Windows 10 20H1+ / 11
        try { if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4); } catch (Exception) { }
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        SetStatus("Looking for monitors...", Theme.Sub);
        queue.Add(() => {
            var found = Ddc.GetMonitors();
            UI(() => {
                monitors = found;
                picker.Items.Clear();
                foreach (var m in found) picker.Items.Add(m.Name);
                if (found.Length == 0) { SetStatus("No monitors found", Theme.Danger); return; }
                picker.SelectedIndex = 0;
            });
        });
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        queue.CompleteAdding();
        base.OnFormClosed(e);
    }

    // ---------------- layout ----------------
    static int S(float v) { return Theme.S(v); }

    void BuildHeader() {
        var title = new Label { Text = "SyncMaster Control", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true,
                                Location = new Point(S(22), S(16)), BackColor = Theme.Bg };
        var sub = new Label { Text = "Monitor settings over DDC/CI - no buttons needed", Font = Theme.Small, ForeColor = Theme.Sub,
                              AutoSize = true, Location = new Point(S(25), S(52)), BackColor = Theme.Bg };
        picker = new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed,
            BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Body,
            Location = new Point(S(500), S(30)), Width = S(340), ItemHeight = S(24)
        };
        picker.DrawItem += (s, e) => {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, picker.Items[e.Index].ToString(), Theme.Body,
                new Rectangle(e.Bounds.X + S(6), e.Bounds.Y, e.Bounds.Width - S(6), e.Bounds.Height),
                sel ? Color.White : Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        picker.SelectedIndexChanged += (s, e) => {
            if (picker.SelectedIndex < 0 || picker.SelectedIndex >= monitors.Length) return;
            current = monitors[picker.SelectedIndex];
            LoadValues();
        };
        Controls.Add(title);
        Controls.Add(sub);
        Controls.Add(picker);
    }

    Card AddCard(string title, int x, int y, int w, int h) {
        var c = new Card(title) { Location = new Point(S(x), S(y)), Size = new Size(S(w), S(h)) };
        Controls.Add(c);
        return c;
    }

    ModernSlider AddSlider(Card card, string caption, byte code, int y, Color accent) {
        var sl = new ModernSlider(caption) { Location = new Point(S(10), S(y)), Size = new Size(S(380), S(52)), Accent = accent, Tag = code };
        sl.ValueChanging += (s, e) => SendLive(code, (uint)sl.Value);
        sl.ValueCommitted += (s, e) => {
            settings["vcp_" + code.ToString("X2")] = sl.Value;
            SaveSettings();
            SetStatus(string.Format("{0} set to {1}", caption, sl.Value), Theme.Ok);
        };
        card.Controls.Add(sl);
        sliders.Add(sl);
        monitorControls.Add(sl);
        return sl;
    }

    FlatBtn AddButton(Card card, string text, int x, int y, int w, int h, EventHandler click) {
        var b = new FlatBtn(text) { Location = new Point(S(x), S(y)), Size = new Size(S(w), S(h)) };
        b.Click += click;
        card.Controls.Add(b);
        monitorControls.Add(b);
        return b;
    }

    void BuildDisplayCard() {
        var c = AddCard("Display", 20, 88, 400, 238);
        AddSlider(c, "Brightness", 0x10, 44, Theme.Accent);
        AddSlider(c, "Contrast", 0x12, 106, Theme.Accent);
        AddSlider(c, "Sharpness", 0x87, 168, Theme.Accent);
        // No saturation slider: the PX2370 doesn't support VCP 0x8A
    }

    void BuildColorCard() {
        var c = AddCard("Color", 20, 338, 400, 222);
        AddSlider(c, "Red", 0x16, 44, Theme.Red);
        AddSlider(c, "Green", 0x18, 102, Theme.Green);
        AddSlider(c, "Blue", 0x1A, 160, Theme.Blue);
        AddButton(c, "Reset colors", 262, 9, 120, 28, (s, e) => Command("Colors reset", 0x08, 1, true));
    }

    void BuildModeCard() {
        var c = AddCard("Picture mode", 440, 88, 400, 222);
        var hint = new Label { Text = "The monitor doesn't report which mode is on - pick one and watch the screen. Brightness is locked in some modes.",
                               Font = Theme.Small, ForeColor = Theme.Sub, BackColor = Theme.Card,
                               Location = new Point(S(18), S(38)), Size = new Size(S(364), S(36)) };
        c.Controls.Add(hint);
        int last;
        settings.TryGetValue("mode", out last);
        for (int i = 0; i < Modes.Length; i++) {
            byte v = Modes[i];
            int col = i % 4, row = i / 4;
            var chip = AddButton(c, "Mode " + (i + 1), 18 + col * 92, 82 + row * 44, 84, 36, null);
            chip.Selected = v == last;
            tips.SetToolTip(chip, string.Format("VCP 0xDC = 0x{0:X2}", v));
            chip.Click += (s, e) => {
                foreach (var m in modeChips) { m.Selected = m == chip; m.Invalidate(); }
                settings["mode"] = v;
                SaveSettings();
                Command(chip.Text + " applied", 0xDC, v, true);
            };
            modeChips.Add(chip);
        }
    }

    void BuildInputCard() {
        var c = AddCard("Input & power", 440, 322, 400, 238);
        AddButton(c, "HDMI", 18, 44, 178, 36, (s, e) => Command("Switched to HDMI", 0x60, 5, false));
        AddButton(c, "DVI", 204, 44, 178, 36, (s, e) => {
            if (Ask("Switch to the DVI input?\n\nIf nothing is plugged into DVI the monitor will show no picture. " +
                    "Use this app on your laptop screen and click HDMI to switch back."))
                Command("Switched to DVI", 0x60, 3, false);
        });
        AddButton(c, "Standby", 18, 88, 178, 36, (s, e) => Command("Monitor in standby", 0xD6, 4, false));
        AddButton(c, "Wake up", 204, 88, 178, 36, (s, e) => Command("Monitor on", 0xD6, 1, false));
        var line = new Panel { BackColor = Theme.Border, Location = new Point(S(18), S(140)), Size = new Size(S(364), 1) };
        c.Controls.Add(line);
        var reset = AddButton(c, "Factory reset", 18, 156, 364, 36, (s, e) => {
            if (Ask("Reset all monitor settings to factory defaults?"))
                Command("Factory settings restored", 0x04, 1, true);
        });
        reset.Danger = true;
        var note = new Label { Text = "Resets brightness, contrast, colors and picture mode.", Font = Theme.Small, ForeColor = Theme.Sub,
                               BackColor = Theme.Card, AutoSize = true, Location = new Point(S(18), S(200)) };
        c.Controls.Add(note);
    }

    void BuildStatus() {
        statusDot = new Panel { Size = new Size(S(8), S(8)), Location = new Point(S(24), S(578)), BackColor = Theme.Sub };
        statusDot.Paint += (s, e) => {
            e.Graphics.Clear(Theme.Bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush((Color)statusDot.Tag)) e.Graphics.FillEllipse(b, 0, 0, statusDot.Width - 1, statusDot.Height - 1);
        };
        statusDot.Tag = Theme.Sub;
        status = new Label { AutoSize = false, Location = new Point(S(38), S(570)), Size = new Size(S(800), S(24)),
                             ForeColor = Theme.Sub, BackColor = Theme.Bg, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft };
        Controls.Add(statusDot);
        Controls.Add(status);
    }

    // ---------------- behaviour ----------------
    void UI(Action a) {
        if (IsDisposed) return;
        try { BeginInvoke(a); } catch (Exception) { }
    }

    void SetStatus(string text, Color dot) {
        status.Text = text;
        statusDot.Tag = dot;
        statusDot.Invalidate();
    }

    void SetControlsEnabled(bool on) {
        foreach (var c in monitorControls) c.Enabled = on;
    }

    bool Ask(string question) {
        return MessageBox.Show(this, question, "SyncMaster Control", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    void LoadValues() {
        var m = current;
        if (m == null) return;
        SetControlsEnabled(false);
        SetStatus("Reading settings from " + m.Name.Trim() + "...", Theme.Sub);
        var codes = new List<byte>();
        foreach (var sl in sliders) codes.Add((byte)sl.Tag);
        queue.Add(() => {
            var res = new Dictionary<byte, uint[]>();
            foreach (var code in codes) res[code] = Ddc.Read(m, code);
            UI(() => {
                if (m != current) return;
                int answered = 0;
                foreach (var sl in sliders) {
                    var r = res[(byte)sl.Tag];
                    if (r == null || r[1] == 0) { sl.Enabled = false; sl.Note = "no reply"; sl.Invalidate(); continue; }
                    answered++;
                    sl.Enabled = true;
                    sl.Maximum = (int)r[1];
                    if (r[0] <= r[1]) { sl.SetValueSilently((int)r[0]); sl.Note = null; }
                    else {
                        // The PX2370 accepts brightness but reports 255 of 100; show the last value set here instead
                        int saved;
                        sl.SetValueSilently(settings.TryGetValue("vcp_" + ((byte)sl.Tag).ToString("X2"), out saved) ? saved : sl.Maximum / 2);
                        sl.Note = "last set here";
                    }
                    sl.Invalidate();
                }
                foreach (var c in monitorControls) if (!(c is ModernSlider)) c.Enabled = answered > 0;
                if (answered == 0) SetStatus("This monitor isn't answering DDC/CI commands", Theme.Danger);
                else SetStatus("Connected to " + m.Name.Trim(), Theme.Ok);
            });
        });
    }

    // Slider drags produce many values; only the newest per setting is sent
    void SendLive(byte code, uint value) {
        bool schedule;
        lock (pending) { schedule = pending.Count == 0; pending[code] = value; }
        if (!schedule) return;
        queue.Add(() => {
            while (true) {
                List<KeyValuePair<byte, uint>> batch;
                lock (pending) {
                    if (pending.Count == 0) return;
                    batch = new List<KeyValuePair<byte, uint>>(pending);
                    pending.Clear();
                }
                var m = current;
                foreach (var kv in batch) Ddc.Write(m, kv.Key, kv.Value);
            }
        });
    }

    void Command(string done, byte code, uint value, bool reload) {
        var m = current;
        if (m == null) return;
        queue.Add(() => {
            bool ok = Ddc.Write(m, code, value);
            UI(() => SetStatus(ok ? done : "The monitor didn't accept that command", ok ? Theme.Ok : Theme.Danger));
            if (ok && reload) { Thread.Sleep(700); UI(LoadValues); }
        });
    }

    Dictionary<string, int> LoadSettings() {
        var d = new Dictionary<string, int>();
        try {
            foreach (var line in File.ReadAllLines(settingsPath)) {
                int i = line.IndexOf('='), v;
                if (i > 0 && int.TryParse(line.Substring(i + 1).Trim(), out v)) d[line.Substring(0, i).Trim()] = v;
            }
        } catch (Exception) { }
        return d;
    }

    void SaveSettings() {
        try {
            var lines = new List<string>();
            foreach (var kv in settings) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(settingsPath, lines.ToArray());
        } catch (Exception) { }   // read-only folder: just don't remember
    }
}

static class Program {
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main() {
        try { SetProcessDPIAware(); } catch (Exception) { }
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
