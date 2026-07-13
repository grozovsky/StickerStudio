using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StickerStudio
{
    enum StudioIcon
    {
        None,
        Back,
        Undo,
        Crop,
        Background,
        Play,
        Pause,
        Export,
        Lock,
        Check,
        Close,
        VideoUpload,
        FileVideo,
        Eyedropper
    }

    // Phosphor Icons Regular 2.1.2, embedded with the application. Using the
    // original icon font gives every action one optical grid and stroke weight.
    static class IconPainter
    {
        static readonly object Sync = new object();
        static PrivateFontCollection collection;
        static FontFamily iconFamily;
        static IntPtr fontMemory = IntPtr.Zero;
        static PrivateFontCollection fillCollection;
        static FontFamily fillFamily;
        static IntPtr fillFontMemory = IntPtr.Zero;
        static bool attempted;

        static FontFamily LoadEmbeddedFont(string resourceName,
            out PrivateFontCollection fonts, out IntPtr memory)
        {
            fonts = null;
            memory = IntPtr.Zero;
            using (Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                byte[] data = new byte[stream.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int read = stream.Read(data, offset, data.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                memory = Marshal.AllocCoTaskMem(data.Length);
                Marshal.Copy(data, 0, memory, data.Length);
                fonts = new PrivateFontCollection();
                fonts.AddMemoryFont(memory, data.Length);
                return fonts.Families.Length > 0 ? fonts.Families[0] : null;
            }
        }

        static void EnsureLoaded()
        {
            if (attempted) return;
            lock (Sync)
            {
                if (attempted) return;
                attempted = true;
                try
                {
                    iconFamily = LoadEmbeddedFont("phosphor.ttf",
                        out collection, out fontMemory);
                    fillFamily = LoadEmbeddedFont("phosphor-fill.ttf",
                        out fillCollection, out fillFontMemory);
                }
                catch { iconFamily = null; }
            }
        }

        static int Codepoint(StudioIcon icon)
        {
            switch (icon)
            {
                case StudioIcon.Back: return 0xE058;
                case StudioIcon.Undo: return 0xE038;
                case StudioIcon.Crop: return 0xE1D4;
                case StudioIcon.Background: return 0xE6B6;
                case StudioIcon.Play: return 0xE3D0;
                case StudioIcon.Pause: return 0xE39E;
                case StudioIcon.Export: return 0xEAF0;
                case StudioIcon.Lock: return 0xE2FA;
                case StudioIcon.Check: return 0xE182;
                case StudioIcon.Close: return 0xE4F6;
                case StudioIcon.VideoUpload: return 0xE4C0;
                case StudioIcon.FileVideo: return 0xEA22;
                case StudioIcon.Eyedropper: return 0xE568;
                default: return 0;
            }
        }

        public static void Draw(Graphics g, StudioIcon icon, RectangleF bounds, Color color)
        {
            if (icon == StudioIcon.None || bounds.Width <= 0 || bounds.Height <= 0) return;
            EnsureLoaded();
            int codepoint = Codepoint(icon);
            if (codepoint == 0 || iconFamily == null) return;

            bool filled = icon == StudioIcon.Play || icon == StudioIcon.Pause;
            FontFamily family = filled && fillFamily != null ? fillFamily : iconFamily;
            float logicalSide = Math.Min(bounds.Width, bounds.Height) / Math.Max(.75f, Theme.UiScale);
            float pointSize = Math.Max(7f, logicalSide * (filled ? .82f : .66f));
            bounds.Y += Math.Max(1f, Theme.S(1));
            GraphicsState state = g.Save();
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Font font = new Font(family, pointSize, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(char.ConvertFromUtf32(codepoint), font, brush, bounds, format);
            }
            g.Restore(state);
        }
    }

    // Retained only as a fallback reference for older builds; production controls
    // use the official Phosphor renderer above.
    static class LegacyIconPainter
    {
        public static void Draw(Graphics g, StudioIcon icon, RectangleF bounds, Color color)
        {
            if (icon == StudioIcon.None || bounds.Width <= 0 || bounds.Height <= 0) return;

            float scale = Math.Min(bounds.Width, bounds.Height) / 24f;
            float ox = bounds.X + (bounds.Width - 24f * scale) / 2f;
            float oy = bounds.Y + (bounds.Height - 24f * scale) / 2f;
            GraphicsState state = g.Save();
            g.TranslateTransform(ox, oy);
            g.ScaleTransform(scale, scale);

            using (Pen p = new Pen(color, 1.85f))
            using (SolidBrush b = new SolidBrush(color))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                switch (icon)
                {
                    case StudioIcon.Back:
                        g.DrawLines(p, new PointF[] { new PointF(15.5f, 5f), new PointF(8.5f, 12f), new PointF(15.5f, 19f) });
                        break;
                    case StudioIcon.Undo:
                        g.DrawArc(p, 5f, 6f, 14f, 12f, 205f, 275f);
                        g.DrawLines(p, new PointF[] { new PointF(5.5f, 6f), new PointF(5.5f, 11f), new PointF(10.5f, 10.5f) });
                        break;
                    case StudioIcon.Crop:
                        g.DrawLines(p, new PointF[] { new PointF(5f, 3.5f), new PointF(5f, 17f), new PointF(20.5f, 17f) });
                        g.DrawLines(p, new PointF[] { new PointF(3.5f, 7f), new PointF(17f, 7f), new PointF(17f, 20.5f) });
                        break;
                    case StudioIcon.Background:
                        g.DrawLine(p, 6f, 18f, 17f, 7f);
                        g.DrawLine(p, 14.7f, 6.2f, 17.8f, 9.3f);
                        g.DrawLine(p, 6f, 4f, 6f, 7f);
                        g.DrawLine(p, 4.5f, 5.5f, 7.5f, 5.5f);
                        g.DrawLine(p, 18.5f, 15f, 18.5f, 19f);
                        g.DrawLine(p, 16.5f, 17f, 20.5f, 17f);
                        break;
                    case StudioIcon.Play:
                        g.FillPolygon(b, new PointF[] { new PointF(8f, 5.5f), new PointF(19f, 12f), new PointF(8f, 18.5f) });
                        break;
                    case StudioIcon.Pause:
                        p.Width = 2.6f;
                        g.DrawLine(p, 9f, 6f, 9f, 18f);
                        g.DrawLine(p, 15f, 6f, 15f, 18f);
                        break;
                    case StudioIcon.Export:
                        g.DrawLine(p, 12f, 4f, 12f, 15f);
                        g.DrawLines(p, new PointF[] { new PointF(8f, 11f), new PointF(12f, 15f), new PointF(16f, 11f) });
                        g.DrawLines(p, new PointF[] { new PointF(5f, 16f), new PointF(5f, 20f), new PointF(19f, 20f), new PointF(19f, 16f) });
                        break;
                    case StudioIcon.Lock:
                        g.DrawArc(p, 8f, 3.5f, 8f, 10f, 180f, 180f);
                        g.DrawRectangle(p, 6f, 10f, 12f, 10f);
                        g.FillEllipse(b, 11f, 14f, 2f, 2f);
                        break;
                    case StudioIcon.Check:
                        g.DrawLines(p, new PointF[] { new PointF(5f, 12.5f), new PointF(10f, 17.5f), new PointF(19.5f, 7.5f) });
                        break;
                    case StudioIcon.Close:
                        g.DrawLine(p, 6f, 6f, 18f, 18f);
                        g.DrawLine(p, 18f, 6f, 6f, 18f);
                        break;
                    case StudioIcon.VideoUpload:
                        g.DrawRectangle(p, 3.5f, 5f, 17f, 14f);
                        g.DrawLine(p, 12f, 15f, 12f, 8f);
                        g.DrawLines(p, new PointF[] { new PointF(8.8f, 11.2f), new PointF(12f, 8f), new PointF(15.2f, 11.2f) });
                        break;
                    case StudioIcon.Eyedropper:
                        g.DrawLine(p, 7f, 17f, 17f, 7f);
                        g.DrawLine(p, 14f, 5f, 19f, 10f);
                        g.DrawLine(p, 5f, 19f, 8f, 18f);
                        break;
                }
            }
            g.Restore(state);
        }
    }

    // Native, keyboard-accessible button with the shared StudioIcon family.
    class StyledButton : Button
    {
        public StudioIcon Icon;
        public bool Accent;             // синяя основная кнопка
        public bool Checked;            // "включённое" состояние (тоже синее)
        public bool RoundFull;          // полностью круглая (для Play)
        public bool Ghost;              // без фона в покое (второстепенные действия)
        public bool Vertical;           // иконка над подписью (панель инструментов)
        public bool Border;             // тонкая граница для вторичных действий
        public Color SwatchColor = Color.Empty; // цветовой квадратик (для пипетки)

        bool hover, pressed;

        public StyledButton()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Hand;
            ForeColor = Theme.TextMain;
            Font = new Font(Theme.BodyFont, 9.5f);
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        public void SetChecked(bool v)
        {
            if (Checked != v) { Checked = v; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BackMain);

            Color bg;
            bool paintBg = true;
            if (!Enabled) bg = Ghost ? Color.Empty : Theme.BtnPressed;
            else if (Accent) bg = pressed ? Theme.AccentPressed : (hover ? Theme.AccentHover : Theme.Accent);
            else if (Checked) bg = pressed ? Theme.AccentSoft :
                (hover ? Color.FromArgb(92, 40, 24) : Theme.AccentSoft);
            else if (Ghost)
            {
                if (pressed) bg = Theme.BtnPressed;
                else if (hover) bg = Theme.BtnHover;
                else { bg = Color.Empty; paintBg = false; }
            }
            else bg = pressed ? Theme.BtnPressed : (hover ? Theme.BtnHover : Theme.BtnBase);
            if (!Enabled && Ghost) paintBg = false;

            Color fore = !Enabled ? Color.FromArgb(92, Theme.TextMuted) :
                (Accent ? Color.White : (Checked ? Color.FromArgb(255, 205, 186) : Theme.TextMain));

            int rad = RoundFull ? Height / 2 : Theme.S(11);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (paintBg)
            {
                using (GraphicsPath path = Rounded(r, rad))
                using (SolidBrush b = new SolidBrush(bg))
                    g.FillPath(b, path);
            }

            if (Enabled && (Border || Checked) && !Accent)
            {
                Color bc = Checked ? Color.FromArgb(115, Theme.Accent) :
                    (hover ? Theme.BorderHover : Theme.BorderIdle);
                using (GraphicsPath path = Rounded(r, rad))
                using (Pen bp = new Pen(bc, 1f))
                    g.DrawPath(bp, path);
            }

            // focus-ринг только при клавиатурной навигации
            if (Focused && ShowFocusCues && Enabled)
            {
                using (GraphicsPath path = Rounded(r, rad))
                using (Pen fp = new Pen(Accent ? Color.White : Theme.Accent, 2f))
                    g.DrawPath(fp, path);
            }

            // content: [swatch][icon][label], centered as a single unit
            int sw = SwatchColor.IsEmpty ? 0 : Theme.S(16);
            int iconSide = Icon == StudioIcon.None ? 0 : Theme.S(RoundFull ? 22 : 18);
            SizeF iconSz = new SizeF(iconSide, iconSide);
            SizeF textSz = string.IsNullOrEmpty(Text) ? SizeF.Empty : g.MeasureString(Text, Font);
            int gap = Theme.S(6);
            float total = sw + (sw > 0 ? gap : 0)
                + iconSz.Width + (iconSz.Width > 0 && textSz.Width > 0 ? gap : 0)
                + textSz.Width;
            float x = (Width - total) / 2f;
            int pressOffset = 0;

            if (Vertical && sw == 0)
            {
                int verticalIcon = Icon == StudioIcon.None ? 0 : Theme.S(22);
                float vgap = textSz.Height > 0 && verticalIcon > 0 ? Theme.S(5) : 0;
                float totalH = verticalIcon + vgap + textSz.Height;
                float y = (Height - totalH) / 2f + pressOffset;
                if (verticalIcon > 0)
                {
                    IconPainter.Draw(g, Icon,
                        new RectangleF((Width - verticalIcon) / 2f, y, verticalIcon, verticalIcon), fore);
                    y += verticalIcon + vgap;
                }
                if (textSz.Width > 0)
                {
                    using (SolidBrush fb = new SolidBrush(fore))
                        g.DrawString(Text, Font, fb, (Width - textSz.Width) / 2f, y);
                }
                return;
            }

            if (sw > 0)
            {
                RectangleF sr = new RectangleF(x, (Height - sw) / 2f, sw, sw);
                using (GraphicsPath sp = Rounded(Rectangle.Round(sr), Theme.S(4)))
                {
                    using (SolidBrush sb = new SolidBrush(SwatchColor)) g.FillPath(sb, sp);
                    using (Pen pp = new Pen(Color.FromArgb(90, Color.White), 1f)) g.DrawPath(pp, sp);
                }
                x += sw + gap;
            }
            if (iconSide > 0)
            {
                IconPainter.Draw(g, Icon,
                    new RectangleF(x, (Height - iconSide) / 2f + pressOffset, iconSide, iconSide), fore);
                x += iconSide + (textSz.Width > 0 ? gap : 0);
            }
            if (textSz.Width > 0)
            {
                using (SolidBrush fb = new SolidBrush(fore))
                    g.DrawString(Text, Font, fb, x, (Height - textSz.Height) / 2f + pressOffset);
            }
        }

        internal static GraphicsPath Rounded(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(2, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            AccessibleName = Text;
            Invalidate();
            base.OnTextChanged(e);
        }
    }

    // Rounded surfaces and small semantic labels keep the visual language
    // consistent without depending on a third-party UI framework.
    class SurfacePanel : Panel
    {
        public Color FillColor = Theme.Surface;
        public Color StrokeColor = Theme.BorderIdle;
        public int Radius = 16;
        public bool DrawStroke = true;

        public SurfacePanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.BackMain);
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath p = StyledButton.Rounded(r, Theme.S(Radius)))
            using (SolidBrush b = new SolidBrush(FillColor))
                e.Graphics.FillPath(b, p);
            if (DrawStroke)
            {
                using (GraphicsPath p = StyledButton.Rounded(r, Theme.S(Radius)))
                using (Pen pen = new Pen(StrokeColor, 1f))
                    e.Graphics.DrawPath(pen, p);
            }
            base.OnPaint(e);
        }
    }

    class PillLabel : Control
    {
        public Color Tone = Theme.Accent;
        public bool Dot;

        public PillLabel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Font = new Font(Theme.BodySemiboldFont, 9f);
            ForeColor = Theme.TextSoft;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BackMain);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Color.FromArgb(26, Tone);
            using (GraphicsPath p = StyledButton.Rounded(r, Theme.S(9)))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
            using (GraphicsPath p = StyledButton.Rounded(r, Theme.S(9)))
            using (Pen pen = new Pen(Color.FromArgb(82, Tone), 1f))
                g.DrawPath(pen, p);

            int dotW = Dot ? Theme.S(6) : 0;
            SizeF ts = g.MeasureString(Text, Font);
            float total = ts.Width + (Dot ? Theme.S(8) + dotW : 0);
            float x = (Width - total) / 2f;
            if (Dot)
            {
                using (SolidBrush db = new SolidBrush(Tone))
                    g.FillEllipse(db, x, (Height - dotW) / 2f, dotW, dotW);
                x += dotW + Theme.S(8);
            }
            using (SolidBrush tb = new SolidBrush(ForeColor))
                g.DrawString(Text, Font, tb, x, (Height - ts.Height) / 2f);
        }
    }

    class StatusRow : Control
    {
        public StudioIcon Icon = StudioIcon.Check;
        public string Caption = "Статус";
        public string Value = "-";
        public Color Tone = Theme.TextMuted;
        public bool Strong;

        public StatusRow()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.BackPanel;
            AccessibleRole = AccessibleRole.StaticText;
        }

        public void SetStatus(string caption, string value, StudioIcon icon, Color tone)
        {
            Caption = caption;
            Value = value;
            Icon = icon;
            Tone = tone;
            AccessibleName = caption + ": " + value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BackPanel);
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color fill = Strong ? Color.FromArgb(34, Tone) : Theme.Surface;
            using (GraphicsPath p = StyledButton.Rounded(r, Theme.S(10)))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
            using (GraphicsPath p = StyledButton.Rounded(r, Theme.S(10)))
            using (Pen pen = new Pen(Strong ? Color.FromArgb(74, Tone) : Theme.BorderIdle, 1f))
                g.DrawPath(pen, p);

            int tile = Theme.S(28);
            Rectangle tileRect = new Rectangle(Theme.S(10), (Height - tile) / 2, tile, tile);
            using (GraphicsPath p = StyledButton.Rounded(tileRect, Theme.S(8)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(42, Tone)))
                g.FillPath(b, p);
            IconPainter.Draw(g, Icon,
                new RectangleF(tileRect.X + Theme.S(6), tileRect.Y + Theme.S(6),
                    tileRect.Width - Theme.S(12), tileRect.Height - Theme.S(12)), Tone);

            int textX = tileRect.Right + Theme.S(10);
            int rightPad = Theme.S(12);
            int available = Math.Max(1, Width - textX - rightPad);
            int captionW = Math.Min(Theme.S(112), Math.Max(Theme.S(72), available * 50 / 100));
            using (Font captionFont = new Font(Theme.BodySemiboldFont, 9f))
            using (Font valueFont = new Font(Theme.BodyFont, 9f))
            {
                TextRenderer.DrawText(g, Caption, captionFont,
                    new Rectangle(textX, 0, captionW, Height), Theme.TextMain,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Value, valueFont,
                    new Rectangle(textX + captionW, 0,
                        Math.Max(1, available - captionW), Height),
                    Strong ? Theme.TextMain : Theme.TextSoft,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    class UxLiveMark : Control
    {
        static Image logo;

        public UxLiveMark()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            if (logo == null) logo = LoadLogo();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BackMain);
            if (logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                int side = Math.Min(Width, Height);
                Rectangle r = new Rectangle((Width - side) / 2, (Height - side) / 2, side, side);
                g.DrawImage(logo, r);
            }
        }

        static Image LoadLogo()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("uxlive.png"))
                {
                    if (s == null) return null;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        ms.Position = 0;
                        using (Bitmap temp = new Bitmap(ms)) return new Bitmap(temp);
                    }
                }
            }
            catch { return null; }
        }
    }

    // Тёмный слайдер: линия + заливка до бегунка + круглый бегунок.
    // Состояния: hover (бегунок крупнее), focus (кольцо), клавиши ←/→
    class NiceSlider : Control
    {
        public int Minimum;
        public int Maximum = 100;
        int val;
        bool drag;
        bool hover;

        public event EventHandler ValueChanged;

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(Minimum, Math.Min(Maximum, value));
                if (v != val)
                {
                    val = v;
                    Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        public NiceSlider()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.Slider;
        }

        int ThumbX()
        {
            int m = Theme.S(10);
            int w = Width - m * 2;
            return m + (int)((long)(val - Minimum) * w / Math.Max(1, Maximum - Minimum));
        }

        void SetFromX(int x)
        {
            int m = Theme.S(10);
            int w = Math.Max(1, Width - m * 2);
            Value = Minimum + (int)Math.Round((double)(x - m) * (Maximum - Minimum) / w);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); drag = true; SetFromX(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { Value = Value - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Value = Value + 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BackPanel);

            int m = Theme.S(10);
            int cy = Height / 2;
            int th = Theme.S(4);
            int tx = ThumbX();

            using (SolidBrush track = new SolidBrush(Theme.BackHeader))
                FillRounded(g, track, new Rectangle(m, cy - th / 2, Width - m * 2, th), th / 2);
            if (tx > m)
            {
                using (SolidBrush fill = new SolidBrush(Theme.Accent))
                    FillRounded(g, fill, new Rectangle(m, cy - th / 2, tx - m, th), th / 2);
            }

            int tr = Theme.S(hover || drag || Focused ? 10 : 8);
            if (Focused && ShowFocusCues)
            {
                using (Pen focus = new Pen(Theme.Accent, 2f))
                    g.DrawEllipse(focus, tx - tr / 2 - Theme.S(3), cy - tr / 2 - Theme.S(3),
                        tr + Theme.S(6), tr + Theme.S(6));
            }
            using (SolidBrush thumb = new SolidBrush(Color.White))
                g.FillEllipse(thumb, tx - tr / 2 - 1, cy - tr / 2 - 1, tr + 2, tr + 2);
            using (SolidBrush inner = new SolidBrush(Theme.Accent))
                g.FillEllipse(inner, tx - tr / 2 + 1, cy - tr / 2 + 1, tr - 2, tr - 2);
        }

        static void FillRounded(Graphics g, Brush b, Rectangle r, int rad)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = StyledButton.Rounded(r, Math.Max(1, rad)))
                g.FillPath(b, p);
        }
    }
}
