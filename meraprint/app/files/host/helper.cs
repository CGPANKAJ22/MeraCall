// MeraPrint Host Helper — screen capture, mouse/keyboard, "Allow?" popup, "Remote chal raha hai" patti.
// Windows ka apna C# compiler (csc.exe, .NET Framework 4) isse banata hai — kuch install nahi karna.
// host.js ise chalata hai. Baat-cheet:
//   andar (stdin, ek line ek kaam):  CAP scale quality | M x y | B L|R|M 1|0 | W dy dx | K vk 1|0 | T base64 | ASK id base64 | IND base64|- | QUIT
//   bahar (stdout, binary): [1 byte type][4 byte length LE][payload]
//      F = frame (4B width, 4B height, jpeg)   E = frame nahi bani (text)   A = jawab "id 1|0"   X = "Band karo" dabaya   L = log
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Runtime.InteropServices;

static class Helper
{
    // ---------------- Win32 ----------------
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, RDOWN = 0x8, RUP = 0x10, MDOWN = 0x20, MUP = 0x40, WHEEL = 0x800, HWHEEL = 0x1000, ABSOLUTE = 0x8000;
    const uint KEYUP = 0x2, UNICODE = 0x4, EXTENDED = 0x1;

    static Stream outS;
    static readonly object outLock = new object();
    static ImageCodecInfo jpegCodec;

    static void Send(char type, byte[] payload)
    {
        lock (outLock)
        {
            byte[] h = new byte[5];
            h[0] = (byte)type;
            BitConverter.GetBytes(payload.Length).CopyTo(h, 1);
            outS.Write(h, 0, 5);
            outS.Write(payload, 0, payload.Length);
            outS.Flush();
        }
    }
    static void SendText(char type, string s) { Send(type, Encoding.UTF8.GetBytes(s)); }
    static string B64(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return ""; } }

    [STAThread]
    static void Main()
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        outS = Console.OpenStandardOutput();
        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") jpegCodec = c;
        TextReader input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        string line;
        while ((line = input.ReadLine()) != null)
        {
            string[] p = line.Trim().Split(' ');
            if (p.Length == 0 || p[0].Length == 0) continue;
            try
            {
                switch (p[0])
                {
                    case "CAP": Capture(Dbl(p, 1, 1), (int)Dbl(p, 2, 60)); break;
                    case "M": MouseMove(Dbl(p, 1, 0), Dbl(p, 2, 0)); break;
                    case "B": MouseButton(p.Length > 1 ? p[1] : "L", p.Length > 2 && p[2] == "1"); break;
                    case "W": Wheel((int)Dbl(p, 1, 0), (int)Dbl(p, 2, 0)); break;
                    case "K": Key((ushort)Dbl(p, 1, 0), p.Length > 2 && p[2] == "1"); break;
                    case "T": TypeText(B64(p.Length > 1 ? p[1] : "")); break;
                    case "ASK": Ask(p.Length > 1 ? p[1] : "0", B64(p.Length > 2 ? p[2] : "")); break;
                    case "IND": Indicator(p.Length > 1 && p[1] != "-" ? B64(p[1]) : null); break;
                    case "QUIT": Environment.Exit(0); break;
                }
            }
            catch (Exception e) { if (p[0] == "CAP") SendText('E', e.Message); else SendText('L', p[0] + ": " + e.Message); }
        }
        Environment.Exit(0);
    }
    static double Dbl(string[] p, int i, double def)
    {
        double v;
        if (p.Length > i && double.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
        return def;
    }

    // ---------------- Screen ----------------
    static void Capture(double scale, int quality)
    {
        if (scale < 0.2) scale = 0.2; if (scale > 1) scale = 1;
        if (quality < 10) quality = 10; if (quality > 95) quality = 95;
        Rectangle b = Screen.PrimaryScreen.Bounds;
        using (Bitmap full = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb))
        {
            using (Graphics g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                DrawCursor(g, b);
            }
            int w = Math.Max(1, (int)(b.Width * scale)), h = Math.Max(1, (int)(b.Height * scale));
            Bitmap img = full;
            if (w != b.Width)
            {
                img = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (Graphics g2 = Graphics.FromImage(img))
                {
                    g2.InterpolationMode = InterpolationMode.Bilinear;
                    g2.DrawImage(full, 0, 0, w, h);
                }
            }
            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(BitConverter.GetBytes(b.Width), 0, 4);
                ms.Write(BitConverter.GetBytes(b.Height), 0, 4);
                EncoderParameters ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                img.Save(ms, jpegCodec, ep);
                if (img != full) img.Dispose();
                Send('F', ms.ToArray());
            }
        }
    }
    static void DrawCursor(Graphics g, Rectangle b)
    {
        try
        {
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!GetCursorInfo(ref ci) || ci.flags != 1) return;   // 1 = dikh raha hai
            ICONINFO ii;
            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            IntPtr hdc = g.GetHdc();
            DrawIconEx(hdc, ci.pt.x - b.X - hx, ci.pt.y - b.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
            g.ReleaseHdc(hdc);
        }
        catch { }
    }

    // ---------------- Mouse / keyboard ----------------
    static void SendOne(INPUT i) { SendInput(1, new INPUT[] { i }, Marshal.SizeOf(typeof(INPUT))); }
    static INPUT Mouse(int dx, int dy, uint data, uint flags)
    {
        INPUT i = new INPUT(); i.type = INPUT_MOUSE;
        i.u.mi.dx = dx; i.u.mi.dy = dy; i.u.mi.mouseData = data; i.u.mi.dwFlags = flags;
        return i;
    }
    static void MouseMove(double x, double y)   // x, y = 0..1 (primary screen)
    {
        if (x < 0) x = 0; if (x > 1) x = 1; if (y < 0) y = 0; if (y > 1) y = 1;
        SendOne(Mouse((int)(x * 65535), (int)(y * 65535), 0, MOVE | ABSOLUTE));
    }
    static void MouseButton(string b, bool down)
    {
        uint f = b == "R" ? (down ? RDOWN : RUP) : b == "M" ? (down ? MDOWN : MUP) : (down ? LDOWN : LUP);
        SendOne(Mouse(0, 0, 0, f));
    }
    static void Wheel(int dy, int dx)
    {
        if (dy != 0) SendOne(Mouse(0, 0, unchecked((uint)dy), WHEEL));
        if (dx != 0) SendOne(Mouse(0, 0, unchecked((uint)dx), HWHEEL));
    }
    static bool IsExtended(ushort vk)
    {
        return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C || vk == 0x5D
            || vk == 0x6F || vk == 0x90 || vk == 0xA3 || vk == 0xA5 || vk == 0x2C;
    }
    static void Key(ushort vk, bool down)
    {
        if (vk == 0) return;
        INPUT i = new INPUT(); i.type = INPUT_KEYBOARD;
        i.u.ki.wVk = vk;
        i.u.ki.dwFlags = (down ? 0 : KEYUP) | (IsExtended(vk) ? EXTENDED : 0);
        SendOne(i);
    }
    static void TypeText(string s)
    {
        foreach (char c in s)
        {
            if (c == '\n' || c == '\r') { Key(0x0D, true); Key(0x0D, false); continue; }
            foreach (bool down in new bool[] { true, false })
            {
                INPUT i = new INPUT(); i.type = INPUT_KEYBOARD;
                i.u.ki.wVk = 0; i.u.ki.wScan = c;
                i.u.ki.dwFlags = UNICODE | (down ? 0 : KEYUP);
                SendOne(i);
            }
        }
    }

    // ---------------- "Allow?" popup ----------------
    static void Ask(string id, string text)
    {
        Thread t = new Thread(delegate ()
        {
            bool ok = false;
            Form f = new Form();
            f.Text = "MeraPrint - Ijazat";
            f.TopMost = true; f.ShowInTaskbar = true;
            f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.ClientSize = new Size(460, 210);
            f.Font = new Font("Segoe UI", 11f);
            Label l = new Label(); l.Text = text; l.SetBounds(20, 15, 420, 110); l.Font = new Font("Segoe UI", 12f);
            Label c = new Label(); c.SetBounds(20, 125, 420, 22); c.ForeColor = Color.Gray; c.Font = new Font("Segoe UI", 9f);
            Button yes = new Button(); yes.Text = "Haan, Allow"; yes.SetBounds(20, 152, 200, 42); yes.BackColor = Color.FromArgb(22, 163, 74); yes.ForeColor = Color.White; yes.FlatStyle = FlatStyle.Flat;
            Button no = new Button(); no.Text = "Mana"; no.SetBounds(240, 152, 200, 42); no.BackColor = Color.FromArgb(220, 38, 38); no.ForeColor = Color.White; no.FlatStyle = FlatStyle.Flat;
            yes.Click += delegate { ok = true; f.Close(); };
            no.Click += delegate { ok = false; f.Close(); };
            int left = 90;
            System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer(); tm.Interval = 1000;
            tm.Tick += delegate { left--; c.Text = left + " second me apne aap Mana"; if (left <= 0) { tm.Stop(); f.Close(); } };
            c.Text = left + " second me apne aap Mana";
            f.Controls.Add(l); f.Controls.Add(c); f.Controls.Add(yes); f.Controls.Add(no);
            f.Shown += delegate { f.Activate(); System.Media.SystemSounds.Exclamation.Play(); };
            tm.Start();
            Application.Run(f);
            tm.Stop();
            SendText('A', id + " " + (ok ? "1" : "0"));
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    // ---------------- "Remote chal raha hai" patti ----------------
    static Form ind;
    static Label indLabel;
    static void Indicator(string text)
    {
        if (text == null)
        {
            Form f = ind; ind = null;
            if (f != null) try { f.BeginInvoke((MethodInvoker)delegate { f.Close(); }); } catch { }
            return;
        }
        if (ind != null)
        {
            Form f = ind; Label lb = indLabel;
            try { f.BeginInvoke((MethodInvoker)delegate { lb.Text = text; }); } catch { }
            return;
        }
        ManualResetEvent ready = new ManualResetEvent(false);
        Thread t = new Thread(delegate ()
        {
            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None; f.TopMost = true; f.ShowInTaskbar = false;
            f.BackColor = Color.FromArgb(185, 28, 28); f.Opacity = 0.92;
            f.StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.Size = new Size(470, 38);
            f.Location = new Point(wa.X + (wa.Width - 470) / 2, wa.Y);
            Label l = new Label(); l.Text = text; l.ForeColor = Color.White; l.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            l.SetBounds(10, 9, 330, 22);
            Button x = new Button(); x.Text = "X  Band karo"; x.SetBounds(345, 5, 118, 28); x.BackColor = Color.White; x.FlatStyle = FlatStyle.Flat;
            x.Click += delegate { SendText('X', "kick"); };
            f.Controls.Add(l); f.Controls.Add(x);
            ind = f; indLabel = l;
            f.Shown += delegate { ready.Set(); };
            Application.Run(f);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        ready.WaitOne(3000);
    }
}
