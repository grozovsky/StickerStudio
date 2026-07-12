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
        Label loadLabel;
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
            ClientSize = new Size(Theme.S(900), Theme.S(640));
            MinimumSize = new Size(Theme.S(760), Theme.S(560));
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            // --- экран дропа ---
            dropScreen = new Panel();
            dropScreen.Dock = DockStyle.Fill;
            dropScreen.BackColor = Theme.BackMain;

            dropArea = new DropArea();
            dropArea.Click += delegate { PickFile(); };

            loadLabel = new Label();
            loadLabel.Dock = DockStyle.Bottom;
            loadLabel.Height = Theme.S(40);
            loadLabel.TextAlign = ContentAlignment.MiddleCenter;
            loadLabel.ForeColor = Theme.TextMuted;
            loadLabel.Text = "Поддерживаются .mov / .webm (с альфа-каналом) и обычные .mp4";

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
            brand.Text = "Сделано в UX Live";
            brand.AutoSize = true;
            brand.LinkArea = new LinkArea(10, 7);
            brand.ForeColor = Theme.TextMuted;
            brand.LinkColor = Theme.Accent;
            brand.ActiveLinkColor = Color.White;
            brand.VisitedLinkColor = Theme.Accent;
            brand.LinkBehavior = LinkBehavior.HoverUnderline;
            brand.BackColor = Theme.BackMain;
            brand.Font = new Font("Segoe UI", 9f);
            brand.LinkClicked += delegate { OpenChannel(); };

            Image logo = LoadLogo();
            if (logo != null)
            {
                brandLogo = new PictureBox();
                brandLogo.Image = logo;
                brandLogo.SizeMode = PictureBoxSizeMode.Zoom;
                brandLogo.Size = new Size(Theme.S(22), Theme.S(22));
                brandLogo.BackColor = Theme.BackMain;
                brandLogo.Cursor = Cursors.Hand;
                brandLogo.Click += delegate { OpenChannel(); };
            }

            Controls.Add(editor);
            Controls.Add(dropScreen);
            Controls.Add(brand);
            if (brandLogo != null) Controls.Add(brandLogo);
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
            int topPad = Theme.S(64);   // под брендингом
            int availH = dropScreen.ClientSize.Height - topPad - loadLabel.Height - Theme.S(12);
            int w = Math.Min(availW - Theme.S(112), Theme.S(720));
            int h = Math.Min(availH, Theme.S(460));
            if (w < Theme.S(200)) w = Math.Max(Theme.S(120), availW - Theme.S(24));
            if (h < Theme.S(140)) h = Math.Max(Theme.S(100), availH);
            dropArea.SetBounds((availW - w) / 2, topPad + (availH - h) / 2, w, h);
        }

        void PositionBrand()
        {
            brand.Location = new Point(
                ClientSize.Width - brand.Width - Theme.S(10), Theme.S(6));
            if (brandLogo != null)
                brandLogo.Location = new Point(
                    brand.Left - brandLogo.Width - Theme.S(6),
                    Theme.S(6) + (brand.Height - brandLogo.Height) / 2);
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
            loadLabel.Text = "Поддерживаются .mov / .webm (с альфа-каналом) и обычные .mp4";
        }
    }

    // зона дропа в стиле WebmStickerPatch
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
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = ClientRectangle;
            int pad = Theme.S(6);
            r.Inflate(-pad, -pad);

            Color border = active ? Theme.Accent : (hover ? Theme.BorderHover : Theme.BorderIdle);
            using (GraphicsPath path = Rounded(r, Theme.S(14)))
            {
                if (active)
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(26, Theme.Accent)))
                        g.FillPath(fill, path);
                }
                else if (hover)
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(10, 255, 255, 255)))
                        g.FillPath(fill, path);
                }
                using (Pen pen = new Pen(border, 2f * Theme.UiScale))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, path);
                }
            }

            string icon = ""; // MDL2: Download
            string l1 = "Перетащите видео сюда";
            string l2 = "…или кликните, чтобы выбрать файл";
            using (Font fi = new Font("Segoe MDL2 Assets", 34f))
            using (Font f1 = new Font("Segoe UI Semibold", 15f))
            using (Font f2 = new Font("Segoe UI", 10f))
            {
                SizeF si = g.MeasureString(icon, fi);
                SizeF s1 = g.MeasureString(l1, f1);
                SizeF s2 = g.MeasureString(l2, f2);
                float totalH = si.Height + Theme.S(14) + s1.Height + Theme.S(6) + s2.Height;
                float y = r.Top + (r.Height - totalH) / 2f;
                using (SolidBrush bi = new SolidBrush(active ? Theme.Accent : Theme.BorderHover))
                    g.DrawString(icon, fi, bi, r.Left + (r.Width - si.Width) / 2f, y);
                y += si.Height + Theme.S(14);
                using (SolidBrush b1 = new SolidBrush(active ? Theme.Accent : Theme.TextMain))
                    g.DrawString(l1, f1, b1, r.Left + (r.Width - s1.Width) / 2f, y);
                y += s1.Height + Theme.S(6);
                using (SolidBrush b2 = new SolidBrush(Theme.TextMuted))
                    g.DrawString(l2, f2, b2, r.Left + (r.Width - s2.Width) / 2f, y);
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
