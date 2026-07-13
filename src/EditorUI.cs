using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace StickerStudio
{
    // ---------------------------------------------------------------
    //  Превью: шахматка, кадр, кроп-рамка 1:1, пипетка
    // ---------------------------------------------------------------
    class PreviewControl : Control
    {
        public VideoDoc Doc;
        public KeySettings ActiveKey;                 // null/выкл = без кия
        public Rectangle AppliedCropPreview = Rectangle.Empty; // в координатах превью-кадра
        public bool CropMode;
        public RectangleF CropSel;                    // в координатах превью-кадра, квадрат
        public bool PickMode;

        public event Action<Color> ColorPicked;

        int frameIdx;
        Bitmap baseBmp; int baseIdx = -1;
        Bitmap keyedBmp; int keyedIdx = -1;
        int keyVersion; int keyedVersion = -1;

        // drag
        int dragCorner = -1;      // 0..3 = ручки; 4 = перемещение
        PointF dragAnchor;        // противоположный угол при ресайзе
        PointF dragOffset;

        const float MinSel = 32f;

        public PreviewControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        public void SetFrame(int idx)
        {
            if (idx != frameIdx) { frameIdx = idx; Invalidate(); }
        }

        public int CurrentFrame { get { return frameIdx; } }

        public void BumpKeyVersion()
        {
            keyVersion++;
            Invalidate();
        }

        public void ResetCaches()
        {
            baseIdx = -1;
            keyedIdx = -1;
            Invalidate();
        }

        Size ContentSize()
        {
            if (Doc == null) return new Size(4, 3);
            if (!CropMode && !AppliedCropPreview.IsEmpty)
                return AppliedCropPreview.Size;
            return new Size(Doc.PreviewW, Doc.PreviewH);
        }

        Rectangle ImageScreenRect()
        {
            Size c = ContentSize();
            int pad = Theme.S(18);
            int aw = Math.Max(10, ClientSize.Width - pad * 2);
            int ah = Math.Max(10, ClientSize.Height - pad * 2);
            double k = Math.Min((double)aw / c.Width, (double)ah / c.Height);
            int w = (int)(c.Width * k), h = (int)(c.Height * k);
            return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        // экран -> координаты ПОЛНОГО превью-кадра
        PointF ScreenToImage(Point p)
        {
            Rectangle r = ImageScreenRect();
            Size c = ContentSize();
            float x = (p.X - r.X) * (float)c.Width / r.Width;
            float y = (p.Y - r.Y) * (float)c.Height / r.Height;
            if (!CropMode && !AppliedCropPreview.IsEmpty)
            {
                x += AppliedCropPreview.X;
                y += AppliedCropPreview.Y;
            }
            return new PointF(x, y);
        }

        RectangleF ImageToScreen(RectangleF img)
        {
            Rectangle r = ImageScreenRect();
            Size c = ContentSize();
            return new RectangleF(
                r.X + img.X * (float)r.Width / c.Width,
                r.Y + img.Y * (float)r.Height / c.Height,
                img.Width * (float)r.Width / c.Width,
                img.Height * (float)r.Height / c.Height);
        }

        Bitmap CurrentBitmap()
        {
            if (Doc == null || Doc.Frames.Count == 0) return null;
            if (baseIdx != frameIdx)
            {
                if (baseBmp != null) baseBmp.Dispose();
                baseBmp = Doc.DecodeFrame(frameIdx);
                baseIdx = frameIdx;
                keyedIdx = -1;
            }
            if (ActiveKey == null || !ActiveKey.Enabled) return baseBmp;
            if (keyedIdx != frameIdx || keyedVersion != keyVersion)
            {
                if (keyedBmp != null) keyedBmp.Dispose();
                keyedBmp = (Bitmap)baseBmp.Clone();
                ChromaKey.Apply(keyedBmp, ActiveKey);
                keyedIdx = frameIdx;
                keyedVersion = keyVersion;
            }
            return keyedBmp;
        }

        public Color PickColorAt(Point screenPt)
        {
            Bitmap b = baseBmp;
            if (b == null) return Color.Empty;
            PointF ip = ScreenToImage(screenPt);
            int x = (int)ip.X, y = (int)ip.Y;
            if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return Color.Empty;
            Color c = b.GetPixel(x, y);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Stage);
            using (Pen grid = new Pen(Color.FromArgb(13, Color.White), 1f))
            {
                int step = Theme.S(36);
                for (int x = step; x < Width; x += step) g.DrawLine(grid, x, 0, x, Height);
                for (int y = step; y < Height; y += step) g.DrawLine(grid, 0, y, Width, y);
            }
            if (Doc == null || Doc.Frames.Count == 0) return;

            Rectangle r = ImageScreenRect();

            Rectangle shadowRect = r;
            shadowRect.Inflate(Theme.S(8), Theme.S(8));
            shadowRect.Offset(0, Theme.S(5));
            using (GraphicsPath shadow = StyledButton.Rounded(shadowRect, Theme.S(15)))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                g.FillPath(sb, shadow);

            // шахматка (видна сквозь прозрачные места)
            int cell = Theme.S(10);
            using (SolidBrush b1 = new SolidBrush(Theme.Checker1))
            using (SolidBrush b2 = new SolidBrush(Theme.Checker2))
            {
                g.FillRectangle(b1, r);
                Region old = g.Clip;
                g.SetClip(r);
                for (int yy = r.Y, ry = 0; yy < r.Bottom; yy += cell, ry++)
                {
                    for (int xx = r.X + ((ry % 2) * cell); xx < r.Right; xx += cell * 2)
                        g.FillRectangle(b2, xx, yy, cell, cell);
                }
                g.Clip = old;
            }

            Bitmap bmp = CurrentBitmap();
            if (bmp == null) return;

            g.InterpolationMode = InterpolationMode.Bilinear;
            if (!CropMode && !AppliedCropPreview.IsEmpty)
                g.DrawImage(bmp, r, AppliedCropPreview, GraphicsUnit.Pixel);
            else
                g.DrawImage(bmp, r);

            using (GraphicsPath frame = StyledButton.Rounded(r, Theme.S(9)))
            using (Pen fp = new Pen(Color.FromArgb(72, Color.White), 1f))
                g.DrawPath(fp, frame);

            if (CropMode)
            {
                RectangleF sel = ImageToScreen(CropSel);
                using (SolidBrush dark = new SolidBrush(Color.FromArgb(150, 10, 10, 14)))
                {
                    g.FillRectangle(dark, r.X, r.Y, r.Width, sel.Y - r.Y);
                    g.FillRectangle(dark, r.X, sel.Bottom, r.Width, r.Bottom - sel.Bottom);
                    g.FillRectangle(dark, r.X, sel.Y, sel.X - r.X, sel.Height);
                    g.FillRectangle(dark, sel.Right, sel.Y, r.Right - sel.Right, sel.Height);
                }
                using (Pen p = new Pen(Theme.Accent, 2f))
                    g.DrawRectangle(p, sel.X, sel.Y, sel.Width, sel.Height);

                using (Pen guide = new Pen(Color.FromArgb(115, Color.White), 1f))
                {
                    guide.DashStyle = DashStyle.Dash;
                    g.DrawLine(guide, sel.Left + sel.Width / 3f, sel.Top,
                        sel.Left + sel.Width / 3f, sel.Bottom);
                    g.DrawLine(guide, sel.Left + sel.Width * 2f / 3f, sel.Top,
                        sel.Left + sel.Width * 2f / 3f, sel.Bottom);
                    g.DrawLine(guide, sel.Left, sel.Top + sel.Height / 3f,
                        sel.Right, sel.Top + sel.Height / 3f);
                    g.DrawLine(guide, sel.Left, sel.Top + sel.Height * 2f / 3f,
                        sel.Right, sel.Top + sel.Height * 2f / 3f);
                }

                int hs = Theme.S(11);
                using (SolidBrush hb = new SolidBrush(Theme.Accent))
                using (Pen hp = new Pen(Color.White, 1.4f))
                {
                    PointF[] corners = SelCorners(sel);
                    foreach (PointF c in corners)
                    {
                        g.FillEllipse(hb, c.X - hs / 2, c.Y - hs / 2, hs, hs);
                        g.DrawEllipse(hp, c.X - hs / 2, c.Y - hs / 2, hs, hs);
                    }
                }
            }
        }

        static PointF[] SelCorners(RectangleF s)
        {
            return new PointF[]
            {
                new PointF(s.X, s.Y),
                new PointF(s.Right, s.Y),
                new PointF(s.Right, s.Bottom),
                new PointF(s.X, s.Bottom)
            };
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (PickMode)
            {
                Color c = PickColorAt(e.Location);
                if (!c.IsEmpty && ColorPicked != null) ColorPicked(c);
                return;
            }
            if (!CropMode) return;

            RectangleF sel = ImageToScreen(CropSel);
            PointF[] corners = SelCorners(sel);
            int grab = Theme.S(15);
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(e.X - corners[i].X) <= grab && Math.Abs(e.Y - corners[i].Y) <= grab)
                {
                    dragCorner = i;
                    // противоположный угол - якорь
                    PointF op = corners[(i + 2) % 4];
                    PointF opImg = ScreenToImage(Point.Round(op));
                    dragAnchor = opImg;
                    return;
                }
            }
            if (sel.Contains(e.Location))
            {
                dragCorner = 4;
                PointF ip = ScreenToImage(e.Location);
                dragOffset = new PointF(ip.X - CropSel.X, ip.Y - CropSel.Y);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (PickMode) { Cursor = Cursors.Cross; return; }
            if (!CropMode) { Cursor = Cursors.Default; return; }

            if (dragCorner == -1)
            {
                RectangleF sel = ImageToScreen(CropSel);
                PointF[] corners = SelCorners(sel);
                int grab = Theme.S(12);
                bool onCorner = false;
                for (int i = 0; i < 4; i++)
                {
                    if (Math.Abs(e.X - corners[i].X) <= grab && Math.Abs(e.Y - corners[i].Y) <= grab)
                    { onCorner = true; break; }
                }
                Cursor = onCorner ? Cursors.SizeNWSE :
                    (sel.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default);
                return;
            }

            float w = Doc.PreviewW, h = Doc.PreviewH;
            PointF ip = ScreenToImage(e.Location);
            ip.X = Math.Max(0, Math.Min(w, ip.X));
            ip.Y = Math.Max(0, Math.Min(h, ip.Y));

            if (dragCorner == 4)
            {
                float nx = ip.X - dragOffset.X;
                float ny = ip.Y - dragOffset.Y;
                nx = Math.Max(0, Math.Min(w - CropSel.Width, nx));
                ny = Math.Max(0, Math.Min(h - CropSel.Height, ny));
                CropSel = new RectangleF(nx, ny, CropSel.Width, CropSel.Height);
            }
            else
            {
                float ax = dragAnchor.X, ay = dragAnchor.Y;
                float s = Math.Max(Math.Abs(ip.X - ax), Math.Abs(ip.Y - ay));
                float maxW = ip.X >= ax ? w - ax : ax;
                float maxH = ip.Y >= ay ? h - ay : ay;
                s = Math.Max(MinSel, Math.Min(s, Math.Min(maxW, maxH)));
                float nx = ip.X >= ax ? ax : ax - s;
                float ny = ip.Y >= ay ? ay : ay - s;
                CropSel = new RectangleF(nx, ny, s, s);
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragCorner = -1;
        }
    }

    // ---------------------------------------------------------------
    //  Таймлайн: филмстрип из превью-кадров + CUT-окно (мин 0.5с, макс 6с)
    // ---------------------------------------------------------------
    class TimelineControl : Control
    {
        public VideoDoc Doc;
        public double Position;

        public event Action<double> SeekRequested;
        public event Action CutChanging;                    // во время драга
        public event Action<EditState> CutCommitted;        // отпустили мышь; аргумент - состояние ДО

        int dragMode; // 0 нет, 1 левая ручка, 2 правая, 3 окно, 4 seek
        double dragGrabOffset;
        EditState preDrag;
        bool hoverScrub;
        int hoverScrubX;

        Bitmap strip;       // кэш филмстрипа под текущий размер
        int stripW, stripH;

        public TimelineControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Default;
            AccessibleRole = AccessibleRole.Slider;
            AccessibleName = "Позиция и границы фрагмента";
        }

        public void ResetStrip()
        {
            if (strip != null) { strip.Dispose(); strip = null; }
            Invalidate();
        }

        Rectangle TrackRect()
        {
            int m = Theme.S(8);
            return new Rectangle(m, Theme.S(26),
                Math.Max(20, Width - m * 2), Math.Max(12, Height - Theme.S(34)));
        }

        Rectangle ScrubRect()
        {
            int m = Theme.S(8);
            return new Rectangle(m, 0, Math.Max(20, Width - m * 2), Theme.S(22));
        }

        // Филмстрип собирается один раз под текущую ширину из кадров превью
        void EnsureStrip(Rectangle tr)
        {
            if (Doc == null || Doc.Frames.Count == 0) return;
            if (strip != null && stripW == tr.Width && stripH == tr.Height) return;
            if (strip != null) strip.Dispose();
            stripW = tr.Width;
            stripH = tr.Height;
            strip = new Bitmap(Math.Max(1, tr.Width), Math.Max(1, tr.Height));
            using (Graphics g = Graphics.FromImage(strip))
            {
                g.Clear(Theme.BackHeader);
                g.InterpolationMode = InterpolationMode.Bilinear;
                int th = tr.Height;
                int tw = Math.Max(8, (int)Math.Round(
                    (double)th * Doc.PreviewW / Math.Max(1, Doc.PreviewH)));
                int n = (int)Math.Ceiling((double)tr.Width / tw);
                for (int i = 0; i < n; i++)
                {
                    double t = (i + 0.5) * tw / (double)tr.Width * Doc.Info.Duration;
                    using (Bitmap f = Doc.DecodeFrame(Doc.FrameAt(t)))
                    {
                        if (f != null)
                            g.DrawImage(f, new Rectangle(i * tw, 0, tw, th));
                    }
                }
            }
        }

        double XToTime(int x)
        {
            Rectangle tr = TrackRect();
            double t = (x - tr.X) * Doc.Info.Duration / tr.Width;
            return Math.Max(0, Math.Min(Doc.Info.Duration, t));
        }

        int TimeToX(double t)
        {
            Rectangle tr = TrackRect();
            return tr.X + (int)(t * tr.Width / Doc.Info.Duration);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);
            if (Doc == null || Doc.Info == null) return;

            Rectangle tr = TrackRect();
            Rectangle sr = ScrubRect();
            EnsureStrip(tr);

            int x1 = TimeToX(Doc.State.CutStart);
            int x2 = TimeToX(Doc.State.CutEnd);

            // Отдельная широкая scrub-зона: по ней можно быстро искать кадр,
            // не сдвигая выбранный фрагмент и не попадая в trim-ручки.
            int railY = sr.Top + Theme.S(11);
            using (Pen rail = new Pen(Color.FromArgb(92, Theme.TextMuted), 1f))
                g.DrawLine(rail, sr.Left, railY, sr.Right, railY);
            using (Pen selected = new Pen(Color.FromArgb(180, Theme.Accent2), 2f))
                g.DrawLine(selected, x1, railY, x2, railY);
            using (Pen tick = new Pen(Color.FromArgb(82, Theme.TextMuted), 1f))
            {
                for (int i = 0; i <= 8; i++)
                {
                    int tx = sr.Left + i * sr.Width / 8;
                    int half = Theme.S(i % 2 == 0 ? 4 : 2);
                    g.DrawLine(tick, tx, railY - half, tx, railY + half);
                }
            }
            if (hoverScrub && dragMode == 0)
            {
                int d = Theme.S(6);
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(125, Theme.TextSoft)))
                    g.FillEllipse(hb, hoverScrubX - d / 2, railY - d / 2, d, d);
            }

            // филмстрип, затемнение всего вне CUT-отрезка
            using (GraphicsPath tp = StyledButton.Rounded(tr, Theme.S(6)))
            {
                Region old = g.Clip;
                g.SetClip(tp);
                if (strip != null) g.DrawImageUnscaled(strip, tr.X, tr.Y);
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(165, Theme.BackMain)))
                {
                    if (x1 > tr.X) g.FillRectangle(dim, tr.X, tr.Y, x1 - tr.X, tr.Height);
                    if (x2 < tr.Right) g.FillRectangle(dim, x2, tr.Y, tr.Right - x2, tr.Height);
                }
                g.Clip = old;
                using (Pen tpen = new Pen(Color.FromArgb(100, Theme.BorderHover), 1f))
                    g.DrawPath(tpen, tp);
            }

            // рамка CUT-окна
            Rectangle win = new Rectangle(x1, tr.Y, Math.Max(4, x2 - x1), tr.Height);
            using (GraphicsPath wp = StyledButton.Rounded(win, Theme.S(4)))
            using (Pen p = new Pen(Theme.Accent, 2f))
                g.DrawPath(p, wp);

            // ручки-пилюли с насечками
            int hw = Theme.S(10);
            int hh = tr.Height + Theme.S(10);
            foreach (int hx in new int[] { x1, x2 })
            {
                Rectangle hr = new Rectangle(hx - hw / 2, tr.Y - Theme.S(5), hw, hh);
                using (GraphicsPath hp = StyledButton.Rounded(hr, hw / 2))
                using (SolidBrush hb = new SolidBrush(Theme.Accent))
                    g.FillPath(hb, hp);
                using (Pen gp = new Pen(Color.FromArgb(190, Color.White), 1.4f))
                {
                    int cy = tr.Y + tr.Height / 2;
                    g.DrawLine(gp, hx - Theme.S(1), cy - Theme.S(4), hx - Theme.S(1), cy + Theme.S(4));
                    g.DrawLine(gp, hx + Theme.S(2), cy - Theme.S(4), hx + Theme.S(2), cy + Theme.S(4));
                }
            }

            // Плейхед связан с отдельным scrub-рельсом и имеет крупную точку захвата.
            int px = TimeToX(Position);
            using (Pen p = new Pen(Theme.Accent2, 1.5f))
                g.DrawLine(p, px, railY, px, tr.Bottom + Theme.S(2));
            int knob = Theme.S(10);
            Rectangle knobRect = new Rectangle(px - knob / 2, railY - knob / 2, knob, knob);
            using (SolidBrush wb = new SolidBrush(Theme.Accent2))
                g.FillEllipse(wb, knobRect);
            using (Pen ring = new Pen(Theme.Surface, Math.Max(1f, Theme.S(1))))
            {
                knobRect.Inflate(Theme.S(1), Theme.S(1));
                g.DrawEllipse(ring, knobRect);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Doc == null) return;
            Rectangle tr = TrackRect();
            Rectangle sr = ScrubRect();
            int x1 = TimeToX(Doc.State.CutStart);
            int x2 = TimeToX(Doc.State.CutEnd);

            preDrag = Doc.State.Clone();

            if (sr.Contains(e.Location))
            {
                dragMode = 4;
                Capture = true;
                if (SeekRequested != null) SeekRequested(XToTime(e.X));
                Invalidate();
                return;
            }

            int grab = Theme.S(14);
            if (Math.Abs(e.X - x1) <= grab) dragMode = 1;
            else if (Math.Abs(e.X - x2) <= grab) dragMode = 2;
            else if (e.X > x1 && e.X < x2 && tr.Contains(e.Location))
            {
                dragMode = 3;
                dragGrabOffset = XToTime(e.X) - Doc.State.CutStart;
            }
            else
            {
                dragMode = 4;
                if (SeekRequested != null) SeekRequested(XToTime(e.X));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Doc == null) return;

            if (dragMode == 0)
            {
                Rectangle sr = ScrubRect();
                if (sr.Contains(e.Location))
                {
                    bool changed = !hoverScrub || hoverScrubX != e.X;
                    hoverScrub = true;
                    hoverScrubX = Math.Max(sr.Left, Math.Min(sr.Right, e.X));
                    Cursor = Cursors.Hand;
                    if (changed) Invalidate();
                    return;
                }
                if (hoverScrub) { hoverScrub = false; Invalidate(); }
                int x1 = TimeToX(Doc.State.CutStart);
                int x2 = TimeToX(Doc.State.CutEnd);
                int grab = Theme.S(14);
                if (Math.Abs(e.X - x1) <= grab || Math.Abs(e.X - x2) <= grab)
                    Cursor = Cursors.SizeWE;
                else if (e.X > x1 && e.X < x2 && TrackRect().Contains(e.Location))
                    Cursor = Cursors.SizeAll;
                else Cursor = Cursors.Default;
                return;
            }

            double t = XToTime(e.X);
            double dur = Doc.Info.Duration;
            EditState s = Doc.State;

            if (dragMode == 1)
            {
                double lo = Math.Max(0, s.CutEnd - VideoDoc.MaxCutSeconds);
                double hi = s.CutEnd - VideoDoc.MinCutSeconds;
                s.CutStart = Math.Max(lo, Math.Min(hi, t));
            }
            else if (dragMode == 2)
            {
                double lo = s.CutStart + VideoDoc.MinCutSeconds;
                double hi = Math.Min(dur, s.CutStart + VideoDoc.MaxCutSeconds);
                s.CutEnd = Math.Max(lo, Math.Min(hi, t));
            }
            else if (dragMode == 3)
            {
                double len = s.CutEnd - s.CutStart;
                double ns = t - dragGrabOffset;
                ns = Math.Max(0, Math.Min(dur - len, ns));
                s.CutStart = ns;
                s.CutEnd = ns + len;
            }
            else if (dragMode == 4)
            {
                if (SeekRequested != null) SeekRequested(t);
                Invalidate();
                return;
            }

            if (CutChanging != null) CutChanging();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragMode >= 1 && dragMode <= 3 && preDrag != null)
            {
                bool changed = Math.Abs(preDrag.CutStart - Doc.State.CutStart) > 0.001 ||
                               Math.Abs(preDrag.CutEnd - Doc.State.CutEnd) > 0.001;
                if (changed && CutCommitted != null) CutCommitted(preDrag);
            }
            dragMode = 0;
            preDrag = null;
            Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverScrub)
            {
                hoverScrub = false;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }
    }
}
