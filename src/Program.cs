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
        Label loadLabel, landingTitle, landingSubtitle, landingDetails, appTitle, appCaption;
        PillLabel telegramBadge;
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
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            MinimumSize = new Size(Math.Min(860, work.Width), Math.Min(600, work.Height));
            ClientSize = new Size(
                Math.Max(520, Math.Min(1080, work.Width - 48)),
                Math.Max(440, Math.Min(720, work.Height - 56)));
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
            dropScreen.Paint += PaintLandingBackground;

            studioMark = new UxLiveMark();
            studioMark.Size = new Size(Theme.S(48), Theme.S(48));
            studioMark.Cursor = Cursors.Hand;
            studioMark.AccessibleRole = AccessibleRole.Link;
            studioMark.AccessibleName = "Открыть UX Live в Telegram";
            studioMark.Click += delegate { OpenChannel(); };

            appTitle = new Label();
            appTitle.AutoSize = true;
            appTitle.Text = "Sticker Studio";
            appTitle.ForeColor = Theme.TextMain;
            appTitle.Font = new Font("Segoe UI Semibold", 13.5f);

            appCaption = new Label();
            appCaption.AutoSize = true;
            appCaption.Text = "UX Live  /  видеостикеры Telegram";
            appCaption.ForeColor = Theme.TextMuted;
            appCaption.Font = new Font("Segoe UI", 9f);

            telegramBadge = new PillLabel();
            telegramBadge.Text = "WebM для Telegram";
            telegramBadge.Tone = Theme.Telegram;
            telegramBadge.Dot = false;
            telegramBadge.Size = new Size(Theme.S(150), Theme.S(30));

            landingTitle = new Label();
            landingTitle.Text = "Стикер\r\nиз видео";
            landingTitle.TextAlign = ContentAlignment.MiddleLeft;
            landingTitle.ForeColor = Theme.TextMain;
            landingTitle.Font = new Font("Segoe UI Semibold", 28f);

            landingSubtitle = new Label();
            landingSubtitle.Text = "Обрежьте кадр, удалите фон и экспортируйте стикер для Telegram. Без облака и лишних шагов.";
            landingSubtitle.TextAlign = ContentAlignment.TopLeft;
            landingSubtitle.ForeColor = Theme.TextMuted;
            landingSubtitle.Font = new Font("Segoe UI", 10.5f);

            landingDetails = new Label();
            landingDetails.Text = "512 × 512     квадратный холст\r\nДо 3 секунд   точная обрезка\r\nДо 256 КБ     готово для Telegram";
            landingDetails.TextAlign = ContentAlignment.TopLeft;
            landingDetails.ForeColor = Theme.TextSoft;
            landingDetails.Font = new Font("Segoe UI", 9.5f);

            dropArea = new DropArea();
            dropArea.Click += delegate { PickFile(); };

            loadLabel = new Label();
            loadLabel.TextAlign = ContentAlignment.MiddleCenter;
            loadLabel.ForeColor = Theme.TextMuted;
            loadLabel.Font = new Font("Segoe UI", 9f);
            loadLabel.Text = "MOV, WEBM или MP4. Обработка остаётся на вашем компьютере.";

            dropScreen.Controls.Add(studioMark);
            dropScreen.Controls.Add(appTitle);
            dropScreen.Controls.Add(appCaption);
            dropScreen.Controls.Add(telegramBadge);
            dropScreen.Controls.Add(landingTitle);
            dropScreen.Controls.Add(landingSubtitle);
            dropScreen.Controls.Add(landingDetails);
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
                    loadLabel.Text = "⚠ ffmpeg.exe не найден рядом с программой. Экспорт недоступен.";
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

            int margin = Theme.S(availW < 930 ? 32 : 46);
            int headerY = Theme.S(22);
            studioMark.Location = new Point(margin, headerY);
            appTitle.Location = new Point(studioMark.Right + Theme.S(12), Theme.S(22));
            appCaption.Location = new Point(studioMark.Right + Theme.S(13), Theme.S(49));
            telegramBadge.Location = new Point(availW - telegramBadge.Width - margin, Theme.S(29));

            int gap = Theme.S(34);
            int usable = Math.Max(Theme.S(620), availW - margin * 2 - gap);
            int leftW = Math.Min(Theme.S(350), Math.Max(Theme.S(270), usable * 42 / 100));
            int rightW = Math.Max(Theme.S(300), usable - leftW);
            int top = Theme.S(132);

            landingTitle.SetBounds(margin, top, leftW, Theme.S(106));
            landingSubtitle.SetBounds(margin, top + Theme.S(124), leftW, Theme.S(66));
            landingDetails.SetBounds(margin, top + Theme.S(222), leftW, Theme.S(110));

            int rightX = margin + leftW + gap;
            int dropH = Math.Max(Theme.S(300), Math.Min(Theme.S(390), availH - top - Theme.S(102)));
            dropArea.SetBounds(rightX, top, Math.Min(rightW, availW - rightX - margin), dropH);
            loadLabel.SetBounds(rightX, dropArea.Bottom + Theme.S(10), dropArea.Width, Theme.S(42));
            dropScreen.Invalidate();
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
            loadLabel.Text = "MOV, WEBM или MP4. Обработка остаётся на вашем компьютере.";
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
            using (GraphicsPath path = Rounded(r, Theme.S(14)))
            {
                Color fillColor = active ? Color.FromArgb(39, 24, 19) :
                    (hover ? Theme.SurfaceRaised : Theme.Surface);
                using (SolidBrush fill = new SolidBrush(fillColor))
                    g.FillPath(fill, path);
                using (Pen pen = new Pen(border, (active || Focused) ? 2f : 1.2f))
                {
                    if (!active && !Focused) pen.DashPattern = new float[] { 5f, 4f };
                    g.DrawPath(pen, path);
                }
            }

            string l1 = active ? "Отпустите для импорта" : "Перетащите видео сюда";
            string l2 = "или выберите файл с компьютера";
            int iconSide = Theme.S(48);
            int iconY = r.Top + Math.Max(Theme.S(34), (r.Height - Theme.S(260)) / 2);
            int contentOffset = pressed ? Theme.S(1) : 0;
            Rectangle iconRect = new Rectangle(r.Left + (r.Width - iconSide) / 2,
                iconY + contentOffset, iconSide, iconSide);
            IconPainter.Draw(g, StudioIcon.VideoUpload, iconRect,
                active ? Theme.AccentHover : Theme.Accent);

            using (Font f1 = new Font("Segoe UI Semibold", 16f))
            using (Font f2 = new Font("Segoe UI", 10f))
            using (Font fb = new Font("Segoe UI Semibold", 9.5f))
            using (Font fc = new Font("Segoe UI", 9f))
            {
                SizeF s1 = g.MeasureString(l1, f1);
                SizeF s2 = g.MeasureString(l2, f2);
                float y = iconRect.Bottom + Theme.S(14);
                using (SolidBrush b1 = new SolidBrush(Theme.TextMain))
                    g.DrawString(l1, f1, b1, r.Left + (r.Width - s1.Width) / 2f, y);
                y += s1.Height + Theme.S(4);
                using (SolidBrush b2 = new SolidBrush(Theme.TextMuted))
                    g.DrawString(l2, f2, b2, r.Left + (r.Width - s2.Width) / 2f, y);

                Rectangle cta = new Rectangle(r.Left + (r.Width - Theme.S(184)) / 2,
                    (int)(y + s2.Height + Theme.S(18)) + contentOffset, Theme.S(184), Theme.S(42));
                using (GraphicsPath cp = Rounded(cta, Theme.S(10)))
                using (SolidBrush cb = new SolidBrush(
                    pressed ? Theme.AccentPressed : (hover || active ? Theme.AccentHover : Theme.Accent)))
                    g.FillPath(cb, cp);
                string ctaText = "Выбрать видео";
                SizeF cs = g.MeasureString(ctaText, fb);
                using (SolidBrush cw = new SolidBrush(Color.White))
                    g.DrawString(ctaText, fb, cw, cta.Left + (cta.Width - cs.Width) / 2f,
                        cta.Top + (cta.Height - cs.Height) / 2f);

                string formats = "MOV   /   WEBM   /   MP4";
                SizeF fs = g.MeasureString(formats, fc);
                using (SolidBrush fm = new SolidBrush(Theme.TextMuted))
                    g.DrawString(formats, fc, fm, r.Left + (r.Width - fs.Width) / 2f,
                        r.Bottom - Theme.S(34));
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
