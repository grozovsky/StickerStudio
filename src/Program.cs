using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Sticker Studio")]
[assembly: AssemblyProduct("Sticker Studio by UX Live")]
[assembly: AssemblyDescription("Mini-redaktor telegram-stikerov")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace StickerStudio
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // CLI-режим для автотестов:
            // /export "<in>" "<out>" [crop=x:y:size] [cut=a:b] [key=RRGGBB:gain:shrink]
            if (args.Length >= 3 && string.Equals(args[0], "/export", StringComparison.OrdinalIgnoreCase))
                return CliExport(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string startFile = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;
            Application.Run(new MainForm(startFile));
            return 0;
        }

        static int CliExport(string[] args)
        {
            try
            {
                string input = args[1];
                string output = args[2];
                EditState st = new EditState();

                string ffmpeg = Ffmpeg.EnsureAvailable(null);
                if (ffmpeg == null) return 2;
                ProbeInfo info = Ffmpeg.Probe(ffmpeg, input);
                if (!info.Ok) return 3;

                st.CutStart = 0;
                st.CutEnd = Math.Min(info.Duration, VideoDoc.MaxCutSeconds);

                for (int i = 3; i < args.Length; i++)
                {
                    string a = args[i];
                    if (a.StartsWith("crop=", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = a.Substring(5).Split(':');
                        int x = int.Parse(p[0]), y = int.Parse(p[1]), s = int.Parse(p[2]);
                        st.CropRect = new Rectangle(x, y, s, s);
                    }
                    else if (a.StartsWith("cut=", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = a.Substring(4).Split(':');
                        st.CutStart = double.Parse(p[0], CultureInfo.InvariantCulture);
                        st.CutEnd = double.Parse(p[1], CultureInfo.InvariantCulture);
                        double len = st.CutEnd - st.CutStart;
                        if (len > VideoDoc.MaxCutSeconds)
                            st.CutEnd = st.CutStart + VideoDoc.MaxCutSeconds;
                    }
                    else if (string.Equals(a, "fps30", StringComparison.OrdinalIgnoreCase))
                    {
                        st.Fps30 = true;
                    }
                    else if (a.StartsWith("key=", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = a.Substring(4).Split(':');
                        int rgb = int.Parse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        st.Key.Enabled = true;
                        st.Key.ScreenColor = Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
                        if (p.Length > 1) st.Key.Gain = int.Parse(p[1]);
                        if (p.Length > 2) st.Key.ShrinkGrow = int.Parse(p[2]);
                    }
                }

                ExportResult r = ExportPipeline.Run(ffmpeg, input, info, info.HasAlpha,
                    st, output, null);
                return r.Ok ? 0 : 1;
            }
            catch
            {
                return 4;
            }
        }
    }

    class MainForm : Form
    {
        Panel dropScreen;
        DropArea dropArea;
        Label loadLabel, appTitle, appCaption;
        Label telegramBadge;
        LandingHero landingHero;
        UxLiveMark studioMark;
        EditorView editor;
        string pendingFile;
        VideoDoc currentDoc;
        volatile bool loading;

        public MainForm(string startFile)
        {
            pendingFile = startFile;

            using (Graphics g = CreateGraphics())
                Theme.UiScale = g.DpiX / 96f;

            Text = "UX Live Sticker Studio";
            BackColor = Theme.BackMain;
            ForeColor = Theme.TextMain;
            Font = new Font(Theme.BodyFont, 9.75f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            MinimumSize = new Size(Math.Min(1024, work.Width), Math.Min(700, work.Height));
            ClientSize = new Size(
                Math.Max(760, Math.Min(1440, work.Width - 64)),
                Math.Max(560, Math.Min(900, work.Height - 64)));
            MaximizedBounds = work;
            WindowState = FormWindowState.Maximized;
            AllowDrop = true;
            try
            {
                using (Image logoIcon = LoadLogo())
                using (Bitmap iconBitmap = new Bitmap(logoIcon, new Size(32, 32)))
                {
                    IntPtr handle = iconBitmap.GetHicon();
                    using (Icon temp = Icon.FromHandle(handle)) Icon = (Icon)temp.Clone();
                    DestroyIcon(handle);
                }
            }
            catch { }

            // --- экран дропа ---
            dropScreen = new Panel();
            dropScreen.Dock = DockStyle.Fill;
            dropScreen.BackColor = Theme.BackMain;
            dropScreen.AutoScroll = true;
            dropScreen.Paint += PaintLandingBackground;

            studioMark = new UxLiveMark();
            studioMark.Size = new Size(Theme.S(52), Theme.S(52));
            studioMark.Cursor = Cursors.Hand;
            studioMark.AccessibleRole = AccessibleRole.Link;
            studioMark.AccessibleName = "Открыть UX Live в Telegram";
            studioMark.Click += delegate { OpenChannel(); };

            appTitle = new Label();
            appTitle.AutoSize = true;
            appTitle.Text = "Sticker Studio";
            appTitle.ForeColor = Theme.TextMain;
            appTitle.Font = new Font(Theme.DisplayFont, 14f);
            appTitle.BackColor = Color.Transparent;

            appCaption = new Label();
            appCaption.AutoSize = true;
            appCaption.Text = "uxlive  /  видеостикеры Telegram";
            appCaption.ForeColor = Theme.TextMuted;
            appCaption.Font = new Font(Theme.BodyFont, 9f);
            appCaption.BackColor = Color.Transparent;

            telegramBadge = new Label();
            telegramBadge.Text = "Telegram WebM   /   обработка локально";
            telegramBadge.TextAlign = ContentAlignment.MiddleRight;
            telegramBadge.ForeColor = Theme.TextSoft;
            telegramBadge.Font = new Font(Theme.BodySemiboldFont, 9f);
            telegramBadge.BackColor = Color.Transparent;
            telegramBadge.Size = new Size(Theme.S(250), Theme.S(30));

            landingHero = new LandingHero();

            dropArea = new DropArea();
            dropArea.Click += delegate { PickFile(); };

            loadLabel = new Label();
            loadLabel.TextAlign = ContentAlignment.MiddleCenter;
            loadLabel.ForeColor = Theme.TextMuted;
            loadLabel.Font = new Font(Theme.BodyFont, 9.25f);
            loadLabel.BackColor = Color.Transparent;
            loadLabel.Text = "Файл остаётся на этом компьютере";
            loadLabel.Visible = false;

            dropScreen.Controls.Add(studioMark);
            dropScreen.Controls.Add(appTitle);
            dropScreen.Controls.Add(appCaption);
            dropScreen.Controls.Add(telegramBadge);
            dropScreen.Controls.Add(landingHero);
            dropScreen.Controls.Add(dropArea);
            dropScreen.Controls.Add(loadLabel);
            // зона дропа ограничена и отцентрована: не спорит с брендингом и краями окна
            dropScreen.Resize += delegate { LayoutDropArea(); };

            // --- редактор ---
            editor = new EditorView();
            editor.Visible = false;
            editor.BackRequested += delegate { ShowDropScreen(); };

            Controls.Add(editor);
            Controls.Add(dropScreen);
            Shown += delegate
            {
                if (Ffmpeg.Find() == null && !Ffmpeg.HasEmbedded())
                {
                    loadLabel.Text = "⚠ ffmpeg.exe не найден рядом с программой. Экспорт недоступен.";
                    loadLabel.Visible = true;
                }
                if (pendingFile != null)
                {
                    string f = pendingFile;
                    pendingFile = null;
                    LoadVideo(f);
                }
            };

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            dropArea.AllowDrop = true;
            dropArea.DragEnter += OnDragEnter;
            dropArea.DragDrop += OnDragDrop;
            dropArea.DragLeave += delegate { dropArea.SetActive(false); };

            FormClosing += delegate
            {
                Process cur = Ffmpeg.Current;
                if (cur != null) { try { cur.Kill(); } catch { } }
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (editor != null && editor.Visible && editor.HandleKey(keyData))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void LayoutDropArea()
        {
            int availW = dropScreen.ClientSize.Width;
            int availH = dropScreen.ClientSize.Height;

            int edge = Theme.S(availW < Theme.S(1120) ? 28 : 48);
            int contentW = Math.Min(Theme.S(1320), Math.Max(Theme.S(320), availW - edge * 2));
            int contentX = Math.Max(edge, (availW - contentW) / 2);
            int headerY = Theme.S(20);
            studioMark.Location = new Point(contentX, headerY);
            appTitle.Location = new Point(studioMark.Right + Theme.S(13), headerY + Theme.S(2));
            appCaption.Location = new Point(studioMark.Right + Theme.S(14), headerY + Theme.S(30));
            telegramBadge.Visible = contentW >= Theme.S(760);
            if (telegramBadge.Visible)
                telegramBadge.Location = new Point(contentX + contentW - telegramBadge.Width,
                    headerY + Theme.S(11));

            bool stacked = contentW < Theme.S(720);
            int top = availH < Theme.S(720) ? Theme.S(118) :
                Math.Max(Theme.S(138), (availH - Theme.S(680)) / 2);

            if (stacked)
            {
                int heroH = Theme.S(398);
                landingHero.SetBounds(contentX, top, contentW, heroH);
                int dropY = landingHero.Bottom + Theme.S(24);
                dropArea.SetBounds(contentX, dropY, contentW, Theme.S(360));
                loadLabel.SetBounds(contentX, dropArea.Bottom + Theme.S(12), contentW, Theme.S(38));
                dropScreen.AutoScrollMinSize = new Size(0, dropArea.Bottom + Theme.S(34));
            }
            else
            {
                dropScreen.AutoScrollMinSize = Size.Empty;
                int gap = Theme.S(contentW < Theme.S(1100) ? 32 : 72);
                int availableColumns = Math.Max(1, contentW - gap);
                int rightMin = Theme.S(contentW < Theme.S(950) ? 420 : 470);
                int leftTarget = contentW < Theme.S(1100)
                    ? availableColumns * 42 / 100
                    : Theme.S(500);
                int leftW = Math.Max(Theme.S(300),
                    Math.Min(leftTarget, availableColumns - rightMin));
                int rightW = availableColumns - leftW;
                int availableH = Math.Max(Theme.S(340), availH - top - Theme.S(24));
                int heroH = Math.Min(Theme.S(448), availableH);
                int dropH = Math.Min(Theme.S(500), availableH);

                landingHero.SetBounds(contentX, top, leftW, heroH);
                int rightX = contentX + leftW + gap;
                dropArea.SetBounds(rightX, top, rightW, dropH);
                loadLabel.SetBounds(rightX, dropArea.Bottom + Theme.S(12), rightW, Theme.S(38));
            }
            dropScreen.Invalidate();
        }

        void PaintLandingBackground(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.BackMain);

            if (dropArea != null && dropArea.Width > 0)
            {
                Rectangle glow = new Rectangle(
                    dropArea.Left - Theme.S(120),
                    dropArea.Top - Theme.S(130),
                    dropArea.Width + Theme.S(240),
                    dropArea.Height + Theme.S(260));
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(glow);
                    using (PathGradientBrush gb = new PathGradientBrush(gp))
                    {
                        gb.CenterPoint = new PointF(dropArea.Left + dropArea.Width * .64f,
                            dropArea.Top + dropArea.Height * .46f);
                        gb.CenterColor = Color.FromArgb(28, Theme.Accent);
                        gb.SurroundColors = new Color[] { Color.FromArgb(0, Theme.Accent) };
                        g.FillPath(gb, gp);
                    }
                }
            }

            using (Pen divider = new Pen(Color.FromArgb(26, Color.White), 1f))
                g.DrawLine(divider, Theme.S(40), Theme.S(96),
                    Math.Max(Theme.S(40), dropScreen.Width - Theme.S(40)), Theme.S(96));
        }

        static void OpenChannel()
        {
            try { Process.Start("https://t.me/uxlive"); }
            catch { }
        }

        static Image LoadLogo()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("uxlive.png"))
                {
                    if (s == null) return null;
                    MemoryStream ms = new MemoryStream();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    return Image.FromStream(ms);
                }
            }
            catch { return null; }
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        void OnDragEnter(object s, DragEventArgs e)
        {
            if (!loading && dropScreen.Visible && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                dropArea.SetActive(true);
            }
            else e.Effect = DragDropEffects.None;
        }

        void OnDragDrop(object s, DragEventArgs e)
        {
            dropArea.SetActive(false);
            if (loading || !dropScreen.Visible) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
                LoadVideo(files[0]);
        }

        void PickFile()
        {
            if (loading) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Выберите видео";
                d.Filter = "Видео (*.mov;*.webm;*.mp4)|*.mov;*.webm;*.mp4;*.m4v;*.avi;*.mkv|Все файлы (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK)
                    LoadVideo(d.FileName);
            }
        }

        void LoadVideo(string path)
        {
            if (loading) return;
            loading = true;
            loadLabel.ForeColor = Theme.TextMuted;
            loadLabel.Text = "Открываю видео…";
            loadLabel.Visible = true;

            Thread t = new Thread(delegate()
            {
                string err = null;
                VideoDoc doc = new VideoDoc();
                string ffmpeg = Ffmpeg.EnsureAvailable(delegate(string s)
                {
                    SafeLabel(s);
                });
                if (ffmpeg == null)
                {
                    err = "ffmpeg.exe не найден. Положите его рядом с программой.";
                }
                else
                {
                    err = doc.Load(ffmpeg, path, delegate(int pct, string s)
                    {
                        SafeLabel(s);
                    });
                }

                BeginInvoke((MethodInvoker)delegate
                {
                    loading = false;
                    if (err != null)
                    {
                        loadLabel.ForeColor = Theme.Err;
                        loadLabel.Text = "✗ " + err;
                        return;
                    }
                    if (currentDoc != null) currentDoc.Dispose();
                    currentDoc = doc;
                    editor.LoadDoc(doc, ffmpeg);
                    dropScreen.Visible = false;
                    editor.Visible = true;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void SafeLabel(string s)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate { loadLabel.Text = s; loadLabel.Visible = true; });
            }
            catch { }
        }

        void ShowDropScreen()
        {
            editor.StopAll();
            editor.Visible = false;
            dropScreen.Visible = true;
            loadLabel.ForeColor = Theme.TextMuted;
            loadLabel.Text = "Файл остаётся на этом компьютере";
            loadLabel.Visible = false;
        }
    }

    class LandingHero : Control
    {
        const string HeadingLine1 = "Соберите стикер";
        const string HeadingLine2 = "из любого видео";
        const string Summary = "Точная обрезка, чистый фон и готовый WebM для Telegram. Всё локально, без лишних шагов.";

        public LandingHero()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = HeadingLine1 + " " + HeadingLine2 + ". " + Summary +
                " Параметры: 512 на 512, до 6 секунд, до 256 килобайт.";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            string[] values = { "512 × 512", "До 6 секунд", "До 256 КБ" };
            string[] captions = { "квадратный холст", "точный фрагмент", "лимит Telegram" };
            using (Font headingFont = CreateHeadingFont(g))
            using (Font summaryFont = new Font(Theme.BodyFont, 11f))
            using (Font valueFont = new Font(Theme.BodySemiboldFont, 9.5f))
            using (Font captionFont = new Font(Theme.BodyFont, 9.5f))
            using (SolidBrush headingBrush = new SolidBrush(Theme.TextMain))
            using (SolidBrush summaryBrush = new SolidBrush(Theme.TextMuted))
            using (StringFormat headingFormat = new StringFormat(StringFormat.GenericTypographic))
            {
                headingFormat.FormatFlags = StringFormatFlags.NoWrap;
                float lineH = headingFont.GetHeight(g) * 1.08f;
                g.DrawString(HeadingLine1, headingFont, headingBrush, 0, 0, headingFormat);
                g.DrawString(HeadingLine2, headingFont, headingBrush, 0, lineH, headingFormat);

                bool compact = Width < Theme.S(430) || Height < Theme.S(420);
                float summaryY = lineH * 2f + Theme.S(compact ? 20 : 30);
                g.DrawString(Summary, summaryFont, summaryBrush,
                    new RectangleF(0, summaryY, Math.Max(1, Width - Theme.S(8)),
                        Theme.S(compact ? 68 : 76)));

                int rowH = Theme.S(compact ? 40 : 44);
                int idealBenefitsY = Theme.S(compact ? 248 : 284);
                int latestBenefitsY = Height - rowH * values.Length - Theme.S(4);
                int minBenefitsY = (int)Math.Ceiling(summaryY) + Theme.S(compact ? 82 : 96);
                int benefitsY = Math.Max(minBenefitsY,
                    Math.Min(idealBenefitsY, latestBenefitsY));
                int iconSide = Theme.S(18);
                int valueX = Theme.S(compact ? 28 : 32);
                int captionX = Theme.S(compact ? 136 : 150);
                for (int i = 0; i < values.Length; i++)
                {
                    int y = benefitsY + i * rowH;
                    Rectangle icon = new Rectangle(0, y + (rowH - iconSide) / 2, iconSide, iconSide);
                    IconPainter.Draw(g, StudioIcon.Check,
                        new RectangleF(icon.X, icon.Y, icon.Width, icon.Height), Theme.Accent2);
                    TextRenderer.DrawText(g, values[i], valueFont,
                        new Rectangle(valueX, y, Math.Max(1, captionX - valueX - Theme.S(8)), rowH),
                        Theme.TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, captions[i], captionFont,
                        new Rectangle(captionX, y, Math.Max(1, Width - captionX), rowH),
                        Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }
        }

        Font CreateHeadingFont(Graphics g)
        {
            float size = Width < Theme.S(430) ? 31f : 35f;
            float maxWidth = Math.Max(1, Width - Theme.S(4));
            while (size > 24f)
            {
                Font candidate = new Font(Theme.DisplayFont, size);
                float widest = Math.Max(
                    g.MeasureString(HeadingLine1, candidate).Width,
                    g.MeasureString(HeadingLine2, candidate).Width);
                if (widest <= maxWidth) return candidate;
                candidate.Dispose();
                size -= 1f;
            }
            return new Font(Theme.DisplayFont, 24f);
        }
    }

    // Главный импорт-кард: один ясный вход в сценарий вместо пустого поля.
    class DropArea : Button
    {
        bool active;
        bool hover;
        bool pressed;

        public DropArea()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Выбрать видео";
            AccessibleDescription = "Перетащите видео или нажмите, чтобы выбрать файл";
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        public void SetActive(bool v)
        {
            if (active != v) { active = v; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true; Invalidate(); base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false; Invalidate(); base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BackMain);

            Rectangle r = ClientRectangle;
            int pad = Theme.S(3);
            r.Inflate(-pad, -pad);

            Color border = active ? Theme.Accent : (hover ? Theme.BorderHover : Theme.BorderIdle);
            using (GraphicsPath path = Rounded(r, Theme.S(16)))
            {
                Color fillColor = active ? Color.FromArgb(42, 25, 20) :
                    (hover ? Theme.SurfaceRaised : Color.FromArgb(21, 21, 25));
                using (SolidBrush fill = new SolidBrush(fillColor))
                    g.FillPath(fill, path);
                using (Pen pen = new Pen(border, (active || Focused) ? 2f : 1f))
                    g.DrawPath(pen, path);
            }

            string l1 = active ? "Отпустите для импорта" : "Перетащите видео сюда";
            string l2 = active ? "Файл откроется сразу после загрузки" : "или выберите файл с компьютера";
            int iconTile = Theme.S(76);
            int iconY = r.Top + Math.Max(Theme.S(38), (r.Height - Theme.S(330)) / 2);
            Rectangle iconTileRect = new Rectangle(r.Left + (r.Width - iconTile) / 2,
                iconY, iconTile, iconTile);
            using (GraphicsPath ip = Rounded(iconTileRect, Theme.S(16)))
            {
                using (SolidBrush ib = new SolidBrush(active
                    ? Color.FromArgb(104, 47, 24) : Color.FromArgb(65, 35, 25)))
                    g.FillPath(ib, ip);
                using (Pen outline = new Pen(Color.FromArgb(84, Theme.Accent), 1f))
                    g.DrawPath(outline, ip);
            }
            IconPainter.Draw(g, StudioIcon.FileVideo,
                new RectangleF(iconTileRect.X + Theme.S(20), iconTileRect.Y + Theme.S(20),
                    iconTileRect.Width - Theme.S(40), iconTileRect.Height - Theme.S(40)),
                active ? Theme.AccentHover : Theme.Accent2);

            using (Font f1 = new Font(Theme.DisplayFont, 17.5f))
            using (Font f2 = new Font(Theme.BodyFont, 10.5f))
            using (Font fb = new Font(Theme.BodySemiboldFont, 10f))
            using (Font fc = new Font(Theme.BodyFont, 9f))
            {
                SizeF s1 = g.MeasureString(l1, f1);
                SizeF s2 = g.MeasureString(l2, f2);
                float y = iconTileRect.Bottom + Theme.S(20);
                using (SolidBrush b1 = new SolidBrush(Theme.TextMain))
                    g.DrawString(l1, f1, b1, r.Left + (r.Width - s1.Width) / 2f, y);
                y += s1.Height + Theme.S(5);
                using (SolidBrush b2 = new SolidBrush(Theme.TextMuted))
                    g.DrawString(l2, f2, b2, r.Left + (r.Width - s2.Width) / 2f, y);

                Rectangle cta = new Rectangle(r.Left + (r.Width - Theme.S(228)) / 2,
                    (int)(y + s2.Height + Theme.S(22)), Theme.S(228), Theme.S(50));
                using (GraphicsPath cp = Rounded(cta, Theme.S(10)))
                using (SolidBrush cb = new SolidBrush(
                    pressed ? Theme.AccentPressed : (hover || active ? Theme.AccentHover : Theme.Accent)))
                    g.FillPath(cb, cp);
                string ctaText = "Выбрать видео";
                Size cs = TextRenderer.MeasureText(g, ctaText, fb, Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int ctaIcon = Theme.S(18);
                float groupW = ctaIcon + Theme.S(8) + cs.Width;
                float groupX = cta.Left + (cta.Width - groupW) / 2f;
                IconPainter.Draw(g, StudioIcon.VideoUpload,
                    new RectangleF(groupX, cta.Top + (cta.Height - ctaIcon) / 2f, ctaIcon, ctaIcon),
                    Color.White);
                TextRenderer.DrawText(g, ctaText, fb,
                    new Rectangle((int)(groupX + ctaIcon + Theme.S(8)), cta.Top,
                        cs.Width + Theme.S(2), cta.Height), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                string formats = "MOV    /    WEBM    /    MP4";
                SizeF fs = g.MeasureString(formats, fc);
                using (SolidBrush fm = new SolidBrush(Theme.TextMuted))
                    g.DrawString(formats, fc, fm, r.Left + (r.Width - fs.Width) / 2f,
                        r.Bottom - Theme.S(38));
            }
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
