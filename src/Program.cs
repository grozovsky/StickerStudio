using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
        Label loadLabel, landingTitle, landingSubtitle, appTitle, appCaption;
        PillLabel telegramBadge;
        StudioMark studioMark;
        EditorView editor;
        LinkLabel brand;
        PictureBox brandLogo;
        string pendingFile;
        VideoDoc currentDoc;
        volatile bool loading;

        public MainForm(string startFile)
        {
            pendingFile = startFile;

            using (Graphics g = CreateGraphics())
                Theme.UiScale = g.DpiX / 96f;

            Text = "Sticker Studio — редактор телеграм-стикеров";
            BackColor = Theme.BackMain;
            ForeColor = Theme.TextMain;
            Font = new Font("Segoe UI", 9.75f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(1080), Theme.S(720));
            MinimumSize = new Size(Theme.S(900), Theme.S(640));
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            // --- экран дропа ---
            dropScreen = new Panel();
            dropScreen.Dock = DockStyle.Fill;
            dropScreen.BackColor = Theme.BackMain;
            dropScreen.Paint += PaintLandingBackground;

            studioMark = new StudioMark();
            studioMark.Size = new Size(Theme.S(40), Theme.S(40));

            appTitle = new Label();
            appTitle.AutoSize = true;
            appTitle.Text = "Sticker Studio";
            appTitle.ForeColor = Theme.TextMain;
            appTitle.Font = new Font("Segoe UI Semibold", 13.5f);

            appCaption = new Label();
            appCaption.AutoSize = true;
            appCaption.Text = "VIDEO STICKER WORKSPACE";
            appCaption.ForeColor = Theme.TextMuted;
            appCaption.Font = new Font("Segoe UI Semibold", 7.5f);

            telegramBadge = new PillLabel();
            telegramBadge.Text = "TELEGRAM READY";
            telegramBadge.Tone = Theme.Telegram;
            telegramBadge.Dot = true;
            telegramBadge.Size = new Size(Theme.S(156), Theme.S(28));

            landingTitle = new Label();
            landingTitle.Text = "Видео — в живой стикер";
            landingTitle.TextAlign = ContentAlignment.MiddleCenter;
            landingTitle.ForeColor = Theme.TextMain;
            landingTitle.Font = new Font("Segoe UI Semibold", 25f);

            landingSubtitle = new Label();
            landingSubtitle.Text = "Кроп, хромакей и экспорт под лимиты Telegram — в одном потоке.";
            landingSubtitle.TextAlign = ContentAlignment.MiddleCenter;
            landingSubtitle.ForeColor = Theme.TextMuted;
            landingSubtitle.Font = new Font("Segoe UI", 10.5f);

            dropArea = new DropArea();
            dropArea.Click += delegate { PickFile(); };

            loadLabel = new Label();
            loadLabel.TextAlign = ContentAlignment.MiddleCenter;
            loadLabel.ForeColor = Theme.TextMuted;
            loadLabel.Font = new Font("Segoe UI", 9f);
            loadLabel.Text = "MOV, WEBM или MP4  •  обработка остаётся на вашем компьютере";

            dropScreen.Controls.Add(studioMark);
            dropScreen.Controls.Add(appTitle);
            dropScreen.Controls.Add(appCaption);
            dropScreen.Controls.Add(telegramBadge);
            dropScreen.Controls.Add(landingTitle);
            dropScreen.Controls.Add(landingSubtitle);
            dropScreen.Controls.Add(dropArea);
            dropScreen.Controls.Add(loadLabel);
            // зона дропа ограничена и отцентрована: не спорит с брендингом и краями окна
            dropScreen.Resize += delegate { LayoutDropArea(); };

            // --- редактор ---
            editor = new EditorView();
            editor.Visible = false;
            editor.BackRequested += delegate { ShowDropScreen(); };

            // --- брендинг ---
            brand = new LinkLabel();
            brand.Text = "by UX Live";
            brand.AutoSize = true;
            brand.LinkArea = new LinkArea(3, 7);
            brand.ForeColor = Theme.TextMuted;
            brand.LinkColor = Theme.Accent;
            brand.ActiveLinkColor = Color.White;
            brand.VisitedLinkColor = Theme.Accent;
            brand.LinkBehavior = LinkBehavior.HoverUnderline;
            brand.BackColor = Color.Transparent;
            brand.Font = new Font("Segoe UI Semibold", 8.75f);
            brand.LinkClicked += delegate { OpenChannel(); };

            Image logo = LoadLogo();
            if (logo != null)
            {
                brandLogo = new PictureBox();
                brandLogo.Image = logo;
                brandLogo.SizeMode = PictureBoxSizeMode.Zoom;
                brandLogo.Size = new Size(Theme.S(20), Theme.S(20));
                brandLogo.BackColor = Color.Transparent;
                brandLogo.Cursor = Cursors.Hand;
                brandLogo.Click += delegate { OpenChannel(); };
            }

            Controls.Add(editor);
            Controls.Add(dropScreen);
            dropScreen.Controls.Add(brand);
            if (brandLogo != null) dropScreen.Controls.Add(brandLogo);
            brand.BringToFront();
            if (brandLogo != null) brandLogo.BringToFront();

            Resize += delegate { PositionBrand(); };
            Shown += delegate
            {
                PositionBrand();
                if (Ffmpeg.Find() == null && !Ffmpeg.HasEmbedded())
                    loadLabel.Text = "⚠ ffmpeg.exe не найден рядом с программой — она не сможет работать";
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

            studioMark.Location = new Point(Theme.S(30), Theme.S(24));
            appTitle.Location = new Point(Theme.S(84), Theme.S(22));
            appCaption.Location = new Point(Theme.S(85), Theme.S(48));
            telegramBadge.Location = new Point(availW - telegramBadge.Width - Theme.S(30), Theme.S(30));

            int titleW = Math.Min(Theme.S(850), availW - Theme.S(56));
            landingTitle.SetBounds((availW - titleW) / 2, Theme.S(88), titleW, Theme.S(52));
            landingSubtitle.SetBounds((availW - titleW) / 2, Theme.S(144), titleW, Theme.S(30));

            int w = Math.Min(availW - Theme.S(96), Theme.S(780));
            int h = Math.Min(Theme.S(390), availH - Theme.S(310));
            if (w < Theme.S(560)) w = Math.Max(Theme.S(320), availW - Theme.S(40));
            if (h < Theme.S(280)) h = Theme.S(280);
            int top = Theme.S(190);
            if (top + h + Theme.S(84) > availH)
                top = Math.Max(Theme.S(172), availH - h - Theme.S(84));
            dropArea.SetBounds((availW - w) / 2, top, w, h);
            loadLabel.SetBounds(Theme.S(24), dropArea.Bottom + Theme.S(10), availW - Theme.S(48), Theme.S(28));
            PositionBrand();
            dropScreen.Invalidate();
        }

        void PositionBrand()
        {
            if (brand == null) return;
            brand.Location = new Point(
                dropScreen.ClientSize.Width - brand.Width - Theme.S(30),
                dropScreen.ClientSize.Height - brand.Height - Theme.S(22));
            if (brandLogo != null)
                brandLogo.Location = new Point(
                    brand.Left - brandLogo.Width - Theme.S(6),
                    brand.Top + (brand.Height - brandLogo.Height) / 2);
        }

        void PaintLandingBackground(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.BackMain);
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
                    err = "ffmpeg.exe не найден — положите его рядом с программой";
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
                BeginInvoke((MethodInvoker)delegate { loadLabel.Text = s; });
            }
            catch { }
        }

        void ShowDropScreen()
        {
            editor.StopAll();
            editor.Visible = false;
            dropScreen.Visible = true;
            loadLabel.ForeColor = Theme.TextMuted;
            loadLabel.Text = "MOV, WEBM или MP4  •  обработка остаётся на вашем компьютере";
        }
    }

    // Главный импорт-кард: один ясный вход в сценарий вместо пустого поля.
    class DropArea : Panel
    {
        bool active;
        bool hover;

        public DropArea()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Hand;
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
            hover = false; Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BackMain);

            Rectangle r = ClientRectangle;
            int pad = Theme.S(4);
            r.Inflate(-pad, -pad);

            Color border = active ? Theme.Accent : (hover ? Theme.BorderHover : Theme.BorderIdle);
            using (GraphicsPath shadow = Rounded(new Rectangle(r.X, r.Y + Theme.S(8), r.Width, r.Height), Theme.S(24)))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                g.FillPath(sb, shadow);

            using (GraphicsPath path = Rounded(r, Theme.S(24)))
            {
                Color top = active ? Color.FromArgb(42, 37, 76) :
                    (hover ? Color.FromArgb(29, 32, 45) : Theme.SurfaceRaised);
                Color bottom = active ? Color.FromArgb(22, 24, 38) : Theme.Surface;
                using (LinearGradientBrush fill = new LinearGradientBrush(r, top, bottom, 90f))
                    g.FillPath(fill, path);
                using (Pen pen = new Pen(border, 2f * Theme.UiScale))
                    g.DrawPath(pen, path);
            }

            string icon = active ? "" : "";
            string l1 = active ? "Отпускайте — импортирую" : "Перетащите видео сюда";
            string l2 = "или выберите файл вручную";
            int orb = Theme.S(64);
            int orbY = r.Top + Theme.S(58);
            Rectangle orbRect = new Rectangle(r.Left + (r.Width - orb) / 2, orbY, orb, orb);
            using (GraphicsPath op = Rounded(orbRect, orb / 2))
            using (LinearGradientBrush ob = new LinearGradientBrush(orbRect,
                active ? Theme.AccentHover : Theme.Accent, Theme.Telegram, 45f))
                g.FillPath(ob, op);

            using (Font fi = new Font("Segoe MDL2 Assets", 22f))
            using (Font f1 = new Font("Segoe UI Semibold", 18f))
            using (Font f2 = new Font("Segoe UI", 10f))
            using (Font fb = new Font("Segoe UI Semibold", 9.5f))
            using (Font fc = new Font("Segoe UI Semibold", 7.75f))
            {
                SizeF si = g.MeasureString(icon, fi);
                SizeF s1 = g.MeasureString(l1, f1);
                SizeF s2 = g.MeasureString(l2, f2);
                using (SolidBrush bi = new SolidBrush(Color.White))
                    g.DrawString(icon, fi, bi, orbRect.Left + (orb - si.Width) / 2f,
                        orbRect.Top + (orb - si.Height) / 2f + Theme.S(1));

                float y = orbRect.Bottom + Theme.S(16);
                using (SolidBrush b1 = new SolidBrush(Theme.TextMain))
                    g.DrawString(l1, f1, b1, r.Left + (r.Width - s1.Width) / 2f, y);
                y += s1.Height + Theme.S(3);
                using (SolidBrush b2 = new SolidBrush(Theme.TextMuted))
                    g.DrawString(l2, f2, b2, r.Left + (r.Width - s2.Width) / 2f, y);

                Rectangle cta = new Rectangle(r.Left + (r.Width - Theme.S(184)) / 2,
                    (int)(y + s2.Height + Theme.S(14)), Theme.S(184), Theme.S(42));
                using (GraphicsPath cp = Rounded(cta, cta.Height / 2))
                using (SolidBrush cb = new SolidBrush(hover || active ? Theme.AccentHover : Theme.Accent))
                    g.FillPath(cb, cp);
                string ctaText = "Выбрать видео";
                SizeF cs = g.MeasureString(ctaText, fb);
                using (SolidBrush cw = new SolidBrush(Color.White))
                    g.DrawString(ctaText, fb, cw, cta.Left + (cta.Width - cs.Width) / 2f,
                        cta.Top + (cta.Height - cs.Height) / 2f);

                string secure = "●  ОБРАБОТКА ЛОКАЛЬНО";
                SizeF ss = g.MeasureString(secure, fc);
                using (SolidBrush sg = new SolidBrush(Theme.Accent2))
                    g.DrawString(secure, fc, sg, r.Left + (r.Width - ss.Width) / 2f, r.Top + Theme.S(24));

                string formats = "MOV    •    WEBM    •    MP4";
                SizeF fs = g.MeasureString(formats, fc);
                using (SolidBrush fm = new SolidBrush(Theme.TextMuted))
                    g.DrawString(formats, fc, fm, r.Left + (r.Width - fs.Width) / 2f,
                        r.Bottom - Theme.S(30));
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
