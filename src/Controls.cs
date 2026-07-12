using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StickerStudio
{
    // Скруглённая кнопка с иконкой (Segoe MDL2 Assets), hover/pressed/checked/accent
    class StyledButton : Control
    {
        public string Glyph;            // символ Segoe MDL2 Assets, напр. ""
        public bool Accent;             // синяя основная кнопка
        public bool Checked;            // "включённое" состояние (тоже синее)
        public bool RoundFull;          // полностью круглая (для Play)
        public bool Ghost;              // без фона в покое (второстепенные действия)
        public Color SwatchColor = Color.Empty; // цветовой квадратик (для пипетки)
        public float GlyphSize = 11f;
        public Point GlyphNudge = Point.Empty;  // оптическая центровка иконки

        bool hover, pressed;

        public StyledButton()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Hand;
            ForeColor = Theme.TextMain;
            Font = new Font("Segoe UI", 9.75f);
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

            bool lit = Accent || Checked;
            Color bg;
            bool paintBg = true;
            if (!Enabled) bg = Ghost ? Color.Empty : Theme.BtnPressed;
            else if (lit) bg = pressed ? Theme.AccentPressed : (hover ? Theme.AccentHover : Theme.Accent);
            else if (Ghost)
            {
                if (pressed) bg = Theme.BtnPressed;
                else if (hover) bg = Theme.BtnHover;
                else { bg = Color.Empty; paintBg = false; }
            }
            else bg = pressed ? Theme.BtnPressed : (hover ? Theme.BtnHover : Theme.BtnBase);
            if (!Enabled && Ghost) paintBg = false;

            Color fore = !Enabled ? Theme.TextMuted : (lit ? Color.White : Theme.TextMain);

            int rad = RoundFull ? Height / 2 : Theme.S(8);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (paintBg)
            {
                using (GraphicsPath path = Rounded(r, rad))
                using (SolidBrush b = new SolidBrush(bg))
                    g.FillPath(b, path);
            }

            // focus-ринг только при клавиатурной навигации
            if (Focused && ShowFocusCues && Enabled)
            {
                using (GraphicsPath path = Rounded(r, rad))
                using (Pen fp = new Pen(lit ? Color.White : Theme.Accent, 1.6f))
                    g.DrawPath(fp, path);
            }

            // контент: [цвет][иконка][текст] по центру
            int sw = SwatchColor.IsEmpty ? 0 : Theme.S(16);
            SizeF glyphSz = SizeF.Empty;
            Font glyphFont = null;
            if (!string.IsNullOrEmpty(Glyph))
            {
                glyphFont = new Font("Segoe MDL2 Assets", GlyphSize);
                glyphSz = g.MeasureString(Glyph, glyphFont);
            }
            SizeF textSz = string.IsNullOrEmpty(Text) ? SizeF.Empty : g.MeasureString(Text, Font);
            int gap = Theme.S(6);
            float total = sw + (sw > 0 ? gap : 0)
                + glyphSz.Width + (glyphSz.Width > 0 && textSz.Width > 0 ? gap : 0)
                + textSz.Width;
            float x = (Width - total) / 2f;

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
            if (glyphFont != null)
            {
                using (SolidBrush fb = new SolidBrush(fore))
                    g.DrawString(Glyph, glyphFont, fb,
                        x + GlyphNudge.X, (Height - glyphSz.Height) / 2f + Theme.S(1) + GlyphNudge.Y);
                x += glyphSz.Width + (textSz.Width > 0 ? gap : 0);
                glyphFont.Dispose();
            }
            if (textSz.Width > 0)
            {
                using (SolidBrush fb = new SolidBrush(fore))
                    g.DrawString(Text, Font, fb, x, (Height - textSz.Height) / 2f);
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
