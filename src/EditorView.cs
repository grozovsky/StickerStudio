using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace StickerStudio
{
    class EditorView : Panel
    {
        public event Action BackRequested;

        VideoDoc doc;
        string ffmpegPath;

        PreviewControl preview;
        TimelineControl timeline;
        StyledButton btnBack, btnUndo, btnCrop, btnKey, btnPlay, btnExport;
        StyledButton btnKeyApply, btnKeyCancel, btnCropApply, btnCropCancel, btnPick;
        Panel toolbar, content, toolRail, mainColumn, stageHost, bottomBar;
        Panel inspector, inspectorDefault, keyPanel, cropPanel;
        SurfacePanel readinessCard;
        StudioMark studioMark;
        PillLabel readinessBadge, cropBadge, keyBadge;
        Label timeLabel, statusLabel, lbGain, lbShrink, cropHint;
        Label fileLabel, stageTitle, stageMeta, inspectorTitle, inspectorCaption;
        Label readinessTitle, readinessDetail, sourceInfo, timelineTitle;
        NiceSlider slGain, slShrink;
        ToolTip tips;

        KeySettings editingKey;

        System.Windows.Forms.Timer playTimer;
        Stopwatch playClock = new Stopwatch();
        double playOffset;
        bool playing;
        bool busy;
        string statusInfo = "";
        bool showingResult;

        public EditorView()
        {
            BackColor = Theme.BackMain;
            Dock = DockStyle.Fill;
            tips = new ToolTip();
            BuildUi();
        }

        void BuildUi()
        {
            // ---------- top command bar ----------
            toolbar = new Panel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = Theme.S(72);
            toolbar.BackColor = Theme.BackMain;
            toolbar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(30, Color.White), 1f))
                    e.Graphics.DrawLine(p, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
            };

            studioMark = new StudioMark();
            studioMark.SetBounds(Theme.S(20), Theme.S(16), Theme.S(40), Theme.S(40));
            toolbar.Controls.Add(studioMark);

            Label app = new Label();
            app.AutoSize = true;
            app.Text = "Sticker Studio";
            app.ForeColor = Theme.TextMain;
            app.Font = new Font("Segoe UI Semibold", 12.5f);
            app.Location = new Point(Theme.S(72), Theme.S(15));
            toolbar.Controls.Add(app);

            Label appMeta = new Label();
            appMeta.AutoSize = true;
            appMeta.Text = "CREATIVE WORKSPACE";
            appMeta.ForeColor = Theme.TextMuted;
            appMeta.Font = new Font("Segoe UI Semibold", 7.25f);
            appMeta.Location = new Point(Theme.S(73), Theme.S(40));
            toolbar.Controls.Add(appMeta);

            fileLabel = new Label();
            fileLabel.ForeColor = Theme.TextMuted;
            fileLabel.TextAlign = ContentAlignment.MiddleLeft;
            fileLabel.Font = new Font("Segoe UI", 9f);
            toolbar.Controls.Add(fileLabel);

            btnBack = MakeBtn("", "Новое видео", 0, Theme.S(132));
            btnUndo = MakeBtn("", "Отменить", 0, Theme.S(112));
            btnBack.Ghost = true;
            btnUndo.Border = true;
            toolbar.Controls.Add(btnBack);
            toolbar.Controls.Add(btnUndo);

            btnBack.Click += delegate { if (!busy && BackRequested != null) BackRequested(); };
            btnUndo.Click += delegate { DoUndo(); };
            tips.SetToolTip(btnUndo, "Откатить последнее действие (Ctrl+Z)");

            // ---------- workspace shell ----------
            content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.BackMain;

            toolRail = new Panel();
            toolRail.Dock = DockStyle.Left;
            toolRail.Width = Theme.S(88);
            toolRail.BackColor = Theme.BackPanel;
            toolRail.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(28, Color.White), 1f))
                    e.Graphics.DrawLine(p, toolRail.Width - 1, 0, toolRail.Width - 1, toolRail.Height);
            };

            Label toolsLabel = new Label();
            toolsLabel.Text = "TOOLS";
            toolsLabel.TextAlign = ContentAlignment.MiddleCenter;
            toolsLabel.ForeColor = Theme.TextMuted;
            toolsLabel.Font = new Font("Segoe UI Semibold", 7.25f);
            toolsLabel.SetBounds(0, Theme.S(15), toolRail.Width, Theme.S(20));
            toolRail.Controls.Add(toolsLabel);

            btnCrop = MakeToolBtn("", "Кроп", Theme.S(45));
            btnKey = MakeToolBtn("", "Фон", Theme.S(119));
            btnCrop.Click += delegate { StartCrop(); };
            btnKey.Click += delegate { OpenKeyPanel(); };
            toolRail.Controls.Add(btnCrop);
            toolRail.Controls.Add(btnKey);
            tips.SetToolTip(btnCrop, "Выбрать квадратную зону стикера (512×512)");
            tips.SetToolTip(btnKey, "Убрать однотонный фон (хромакей)");

            mainColumn = new Panel();
            mainColumn.Dock = DockStyle.Fill;
            mainColumn.BackColor = Theme.BackMain;

            stageHost = new Panel();
            stageHost.Dock = DockStyle.Fill;
            stageHost.BackColor = Theme.BackMain;
            stageHost.Resize += delegate { LayoutStage(); };

            stageTitle = new Label();
            stageTitle.Text = "Предпросмотр";
            stageTitle.ForeColor = Theme.TextMain;
            stageTitle.Font = new Font("Segoe UI Semibold", 10.5f);
            stageHost.Controls.Add(stageTitle);

            stageMeta = new Label();
            stageMeta.ForeColor = Theme.TextMuted;
            stageMeta.TextAlign = ContentAlignment.MiddleRight;
            stageMeta.Font = new Font("Segoe UI", 8.5f);
            stageHost.Controls.Add(stageMeta);

            preview = new PreviewControl();
            preview.ColorPicked += OnColorPicked;
            stageHost.Controls.Add(preview);

            bottomBar = new Panel();
            bottomBar.Dock = DockStyle.Bottom;
            bottomBar.Height = Theme.S(132);
            bottomBar.BackColor = Theme.Surface;
            bottomBar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(36, Color.White), 1f))
                    e.Graphics.DrawLine(p, 0, 0, bottomBar.Width, 0);
            };

            timelineTitle = new Label();
            timelineTitle.Text = "ФРАГМЕНТ";
            timelineTitle.ForeColor = Theme.TextMuted;
            timelineTitle.Font = new Font("Segoe UI Semibold", 7.25f);
            bottomBar.Controls.Add(timelineTitle);

            btnPlay = new StyledButton();
            btnPlay.Glyph = "";
            btnPlay.GlyphSize = 14f;
            btnPlay.RoundFull = true;
            btnPlay.Accent = true;
            btnPlay.GlyphNudge = new Point(Theme.S(1), 0); // оптический центр стрелки
            btnPlay.Click += delegate { TogglePlay(); };
            tips.SetToolTip(btnPlay, "Play/Pause (Пробел) — зациклено внутри CUT, без звука");

            timeline = new TimelineControl();
            timeline.SeekRequested += OnSeek;
            timeline.CutChanging += delegate { UpdateTimeLabel(); };
            timeline.CutCommitted += OnCutCommitted;

            timeLabel = new Label();
            timeLabel.ForeColor = Theme.TextMuted;
            timeLabel.TextAlign = ContentAlignment.MiddleRight;
            timeLabel.Font = new Font("Consolas", 8.75f);

            bottomBar.Controls.Add(btnPlay);
            bottomBar.Controls.Add(timeline);
            bottomBar.Controls.Add(timeLabel);

            // ---------- readiness / tool inspector ----------
            inspector = new Panel();
            inspector.Dock = DockStyle.Right;
            inspector.Width = Theme.S(310);
            inspector.BackColor = Theme.BackPanel;
            inspector.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(30, Color.White), 1f))
                    e.Graphics.DrawLine(p, 0, 0, 0, inspector.Height);
            };

            inspectorTitle = new Label();
            inspectorTitle.Text = "Готовность";
            inspectorTitle.ForeColor = Theme.TextMain;
            inspectorTitle.Font = new Font("Segoe UI Semibold", 13f);
            inspector.Controls.Add(inspectorTitle);

            inspectorCaption = new Label();
            inspectorCaption.Text = "Проверка перед экспортом";
            inspectorCaption.ForeColor = Theme.TextMuted;
            inspectorCaption.Font = new Font("Segoe UI", 8.5f);
            inspector.Controls.Add(inspectorCaption);

            BuildDefaultInspector();
            BuildCropInspector();
            BuildKeyInspector();

            statusLabel = new Label();
            statusLabel.ForeColor = Theme.TextMuted;
            statusLabel.TextAlign = ContentAlignment.TopLeft;
            statusLabel.Font = new Font("Segoe UI", 8.5f);
            inspector.Controls.Add(statusLabel);

            btnExport = new StyledButton();
            btnExport.Glyph = "";
            btnExport.Text = "Собрать WebM";
            btnExport.Font = new Font("Segoe UI Semibold", 10f);
            btnExport.Click += delegate { DoExport(); };
            inspector.Controls.Add(btnExport);

            bottomBar.Resize += delegate { LayoutBottomBar(); };
            inspector.Resize += delegate { LayoutInspector(); };
            toolbar.Resize += delegate { LayoutToolbar(); };

            mainColumn.Controls.Add(stageHost);
            mainColumn.Controls.Add(bottomBar);
            content.Controls.Add(mainColumn);
            content.Controls.Add(inspector);
            content.Controls.Add(toolRail);
            Controls.Add(content);
            Controls.Add(toolbar);

            LayoutToolbar();
            LayoutStage();
            LayoutBottomBar();
            LayoutInspector();

            playTimer = new System.Windows.Forms.Timer();
            playTimer.Interval = 15;
            playTimer.Tick += delegate { PlayTick(); };
        }

        void LayoutBottomBar()
        {
            int W = bottomBar.ClientSize.Width;
            if (W < 50) W = Theme.S(900);
            timelineTitle.SetBounds(Theme.S(24), Theme.S(14), Theme.S(100), Theme.S(22));
            timeLabel.SetBounds(Theme.S(130), Theme.S(12),
                Math.Max(Theme.S(180), W - Theme.S(154)), Theme.S(24));
            btnPlay.SetBounds(Theme.S(22), Theme.S(53), Theme.S(50), Theme.S(50));
            timeline.SetBounds(Theme.S(88), Theme.S(45),
                Math.Max(Theme.S(100), W - Theme.S(112)), Theme.S(66));
        }

        void LayoutToolbar()
        {
            int W = toolbar.ClientSize.Width;
            btnBack.SetBounds(W - Theme.S(276), Theme.S(18), Theme.S(132), Theme.S(38));
            btnUndo.SetBounds(W - Theme.S(132), Theme.S(18), Theme.S(112), Theme.S(38));
            fileLabel.SetBounds(Theme.S(230), Theme.S(17),
                Math.Max(Theme.S(120), W - Theme.S(530)), Theme.S(38));
        }

        void LayoutStage()
        {
            int W = stageHost.ClientSize.Width;
            int H = stageHost.ClientSize.Height;
            stageTitle.SetBounds(Theme.S(24), Theme.S(18), Theme.S(180), Theme.S(24));
            stageMeta.SetBounds(Math.Max(Theme.S(210), W - Theme.S(250)), Theme.S(18),
                Theme.S(226), Theme.S(24));
            preview.SetBounds(Theme.S(14), Theme.S(48),
                Math.Max(Theme.S(80), W - Theme.S(28)), Math.Max(Theme.S(80), H - Theme.S(62)));
        }

        void LayoutInspector()
        {
            int W = inspector.ClientSize.Width;
            int H = inspector.ClientSize.Height;
            int pad = Theme.S(20);
            inspectorTitle.SetBounds(pad, Theme.S(20), W - pad * 2, Theme.S(28));
            inspectorCaption.SetBounds(pad, Theme.S(48), W - pad * 2, Theme.S(22));

            int panelTop = Theme.S(82);
            int exportH = Theme.S(50);
            int exportY = H - exportH - Theme.S(20);
            int statusY = exportY - Theme.S(56);
            inspectorDefault.SetBounds(0, panelTop, W, Math.Max(Theme.S(180), statusY - panelTop));
            cropPanel.SetBounds(0, panelTop, W, Math.Max(Theme.S(210), H - panelTop - Theme.S(20)));
            keyPanel.SetBounds(0, panelTop, W, Math.Max(Theme.S(310), H - panelTop - Theme.S(20)));
            statusLabel.SetBounds(pad, statusY, W - pad * 2, Theme.S(46));
            btnExport.SetBounds(pad, exportY, W - pad * 2, exportH);

            readinessCard.SetBounds(pad, Theme.S(4), W - pad * 2, Theme.S(116));
            readinessBadge.SetBounds(Theme.S(16), Theme.S(14),
                readinessCard.Width - Theme.S(32), Theme.S(26));
            readinessTitle.SetBounds(Theme.S(16), Theme.S(48),
                readinessCard.Width - Theme.S(32), Theme.S(25));
            readinessDetail.SetBounds(Theme.S(16), Theme.S(74),
                readinessCard.Width - Theme.S(32), Theme.S(32));
            cropBadge.SetBounds(pad, Theme.S(138), W - pad * 2, Theme.S(30));
            keyBadge.SetBounds(pad, Theme.S(176), W - pad * 2, Theme.S(30));
            sourceInfo.SetBounds(pad, Theme.S(224), W - pad * 2, Theme.S(76));

            LayoutCropPanel(W, cropPanel.Height);
            LayoutKeyPanel(W, keyPanel.Height);
        }

        void BuildDefaultInspector()
        {
            inspectorDefault = new Panel();
            inspectorDefault.BackColor = Theme.BackPanel;

            readinessCard = new SurfacePanel();
            readinessCard.FillColor = Theme.SurfaceRaised;
            readinessCard.BackColor = Theme.SurfaceRaised;
            readinessCard.StrokeColor = Theme.BorderIdle;
            readinessCard.Radius = 16;

            readinessBadge = new PillLabel();
            readinessBadge.Text = "ПРОВЕРЯЮ";
            readinessBadge.Tone = Theme.Accent;
            readinessBadge.Dot = true;
            readinessBadge.Strong = true;

            readinessTitle = new Label();
            readinessTitle.Text = "Подготовка проекта";
            readinessTitle.ForeColor = Theme.TextMain;
            readinessTitle.Font = new Font("Segoe UI Semibold", 10.25f);

            readinessDetail = new Label();
            readinessDetail.Text = "Проверяю параметры исходника";
            readinessDetail.ForeColor = Theme.TextMuted;
            readinessDetail.Font = new Font("Segoe UI", 8.25f);

            readinessCard.Controls.Add(readinessBadge);
            readinessCard.Controls.Add(readinessTitle);
            readinessCard.Controls.Add(readinessDetail);

            cropBadge = new PillLabel();
            cropBadge.Text = "Кроп 1:1";
            cropBadge.Dot = true;
            cropBadge.Tone = Theme.TextMuted;

            keyBadge = new PillLabel();
            keyBadge.Text = "Фон";
            keyBadge.Dot = true;
            keyBadge.Tone = Theme.TextMuted;

            sourceInfo = new Label();
            sourceInfo.ForeColor = Theme.TextMuted;
            sourceInfo.Font = new Font("Segoe UI", 8.5f);
            sourceInfo.Text = "ИСХОДНИК\n—";

            inspectorDefault.Controls.Add(readinessCard);
            inspectorDefault.Controls.Add(cropBadge);
            inspectorDefault.Controls.Add(keyBadge);
            inspectorDefault.Controls.Add(sourceInfo);
            inspector.Controls.Add(inspectorDefault);
        }

        void BuildCropInspector()
        {
            cropPanel = new Panel();
            cropPanel.BackColor = Theme.BackPanel;
            cropPanel.Visible = false;

            Label eyebrow = MakeInspectorLabel("ИНСТРУМЕНТ", 7.25f, Theme.Accent, true);
            eyebrow.SetBounds(Theme.S(20), Theme.S(8), Theme.S(250), Theme.S(20));
            Label title = MakeInspectorLabel("Квадратный кроп", 14f, Theme.TextMain, true);
            title.SetBounds(Theme.S(20), Theme.S(32), Theme.S(270), Theme.S(30));
            cropHint = MakeInspectorLabel(
                "Перемещайте рамку за центр и тяните за углы. Итог всегда будет 512 × 512.",
                8.75f, Theme.TextMuted, false);
            cropHint.SetBounds(Theme.S(20), Theme.S(70), Theme.S(270), Theme.S(58));

            PillLabel ratio = new PillLabel();
            ratio.Text = "1 : 1   •   512 PX";
            ratio.Tone = Theme.Accent;
            ratio.Dot = true;
            ratio.SetBounds(Theme.S(20), Theme.S(142), Theme.S(170), Theme.S(30));

            btnCropApply = new StyledButton();
            btnCropApply.Glyph = "";
            btnCropApply.Text = "Применить кроп";
            btnCropApply.Accent = true;
            btnCropApply.Font = new Font("Segoe UI Semibold", 9.5f);
            btnCropCancel = new StyledButton();
            btnCropCancel.Glyph = "";
            btnCropCancel.Text = "Отмена";
            btnCropCancel.Ghost = true;
            btnCropCancel.Border = true;
            btnCropApply.Click += delegate { ApplyCrop(); };
            btnCropCancel.Click += delegate { CancelCrop(); };

            cropPanel.Controls.Add(eyebrow);
            cropPanel.Controls.Add(title);
            cropPanel.Controls.Add(cropHint);
            cropPanel.Controls.Add(ratio);
            cropPanel.Controls.Add(btnCropApply);
            cropPanel.Controls.Add(btnCropCancel);
            inspector.Controls.Add(cropPanel);
        }

        void BuildKeyInspector()
        {
            keyPanel = new Panel();
            keyPanel.BackColor = Theme.BackPanel;
            keyPanel.Visible = false;

            Label eyebrow = MakeInspectorLabel("ИНСТРУМЕНТ", 7.25f, Theme.Accent2, true);
            eyebrow.SetBounds(Theme.S(20), Theme.S(8), Theme.S(250), Theme.S(20));
            Label title = MakeInspectorLabel("Удаление фона", 14f, Theme.TextMain, true);
            title.SetBounds(Theme.S(20), Theme.S(32), Theme.S(270), Theme.S(30));
            Label hint = MakeInspectorLabel(
                "Выберите цвет на видео, затем уточните края маски.",
                8.75f, Theme.TextMuted, false);
            hint.SetBounds(Theme.S(20), Theme.S(68), Theme.S(270), Theme.S(42));

            btnPick = new StyledButton();
            btnPick.Text = "Выбрать цвет на видео";
            btnPick.Border = true;
            btnPick.SwatchColor = Color.FromArgb(0, 255, 0);
            btnPick.Click += delegate { TogglePick(); };
            tips.SetToolTip(btnPick, "Кликните по цвету фона на видео");

            lbGain = MakeInspectorLabel("Сила удаления  ·  100", 8.75f, Theme.TextSoft, true);
            slGain = new NiceSlider();
            slGain.Minimum = 0; slGain.Maximum = 200; slGain.Value = 100;
            slGain.ValueChanged += delegate { OnKeyParamChanged(); };
            tips.SetToolTip(slGain, "Сила вырезания фона");

            lbShrink = MakeInspectorLabel("Край маски  ·  0", 8.75f, Theme.TextSoft, true);
            slShrink = new NiceSlider();
            slShrink.Minimum = -100; slShrink.Maximum = 100; slShrink.Value = 0;
            slShrink.ValueChanged += delegate { OnKeyParamChanged(); };
            tips.SetToolTip(slShrink, "Поджать (−) или расширить (+) края маски");

            btnKeyApply = new StyledButton();
            btnKeyApply.Glyph = "";
            btnKeyApply.Text = "Применить фон";
            btnKeyApply.Accent = true;
            btnKeyApply.Font = new Font("Segoe UI Semibold", 9.5f);
            btnKeyCancel = new StyledButton();
            btnKeyCancel.Glyph = "";
            btnKeyCancel.Text = "Отмена";
            btnKeyCancel.Ghost = true;
            btnKeyCancel.Border = true;
            btnKeyApply.Click += delegate { ApplyKey(); };
            btnKeyCancel.Click += delegate { CancelKey(); };

            keyPanel.Controls.Add(eyebrow);
            keyPanel.Controls.Add(title);
            keyPanel.Controls.Add(hint);
            keyPanel.Controls.Add(btnPick);
            keyPanel.Controls.Add(lbGain);
            keyPanel.Controls.Add(slGain);
            keyPanel.Controls.Add(lbShrink);
            keyPanel.Controls.Add(slShrink);
            keyPanel.Controls.Add(btnKeyApply);
            keyPanel.Controls.Add(btnKeyCancel);
            inspector.Controls.Add(keyPanel);
        }

        Label MakeInspectorLabel(string text, float size, Color color, bool semibold)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.Font = new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", size);
            return l;
        }

        void LayoutCropPanel(int W, int H)
        {
            int pad = Theme.S(20);
            btnCropApply.SetBounds(pad, Math.Max(Theme.S(196), H - Theme.S(102)),
                W - pad * 2, Theme.S(44));
            btnCropCancel.SetBounds(pad, btnCropApply.Bottom + Theme.S(8),
                W - pad * 2, Theme.S(38));
        }

        void LayoutKeyPanel(int W, int H)
        {
            int pad = Theme.S(20);
            int innerW = W - pad * 2;
            btnPick.SetBounds(pad, Theme.S(112), innerW, Theme.S(42));
            lbGain.SetBounds(pad, Theme.S(166), innerW, Theme.S(24));
            slGain.SetBounds(pad, Theme.S(190), innerW, Theme.S(32));
            lbShrink.SetBounds(pad, Theme.S(226), innerW, Theme.S(24));
            slShrink.SetBounds(pad, Theme.S(250), innerW, Theme.S(32));
            btnKeyApply.SetBounds(pad, Math.Max(Theme.S(292), H - Theme.S(102)),
                innerW, Theme.S(44));
            btnKeyCancel.SetBounds(pad, btnKeyApply.Bottom + Theme.S(8),
                innerW, Theme.S(38));
        }

        StyledButton MakeBtn(string glyph, string text, int x, int w)
        {
            StyledButton b = new StyledButton();
            b.Glyph = glyph;
            b.Text = text;
            b.SetBounds(x, Theme.S(13), w, Theme.S(36));
            return b;
        }

        StyledButton MakeToolBtn(string glyph, string text, int y)
        {
            StyledButton b = new StyledButton();
            b.Glyph = glyph;
            b.GlyphSize = 15f;
            b.Text = text;
            b.Vertical = true;
            b.Ghost = true;
            b.Font = new Font("Segoe UI Semibold", 8.25f);
            b.SetBounds(Theme.S(10), y, Theme.S(68), Theme.S(66));
            return b;
        }

        Label MakeLbl(string text, int x, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Theme.TextMuted;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(x, Theme.S(12), w, Theme.S(32));
            return l;
        }

        // ------------------------------------------------------------
        public void LoadDoc(VideoDoc d, string ffmpeg)
        {
            doc = d;
            ffmpegPath = ffmpeg;
            preview.Doc = d;
            preview.ActiveKey = d.State.Key;
            preview.AppliedCropPreview = CropToPreview(d.State.CropRect);
            preview.CropMode = false;
            preview.PickMode = false;
            preview.ResetCaches();
            timeline.Doc = d;
            timeline.Position = d.State.CutStart;
            timeline.ResetStrip();

            btnKey.Visible = !d.SourceHasAlpha;
            ShowDefaultInspector();
            playing = false;
            playOffset = 0;
            playClock.Reset();
            btnPlay.Glyph = "";
            btnPlay.Invalidate();
            busy = false;
            showingResult = false;

            statusInfo = d.Info.Width + "×" + d.Info.Height +
                "  •  " + d.Info.Duration.ToString("0.0") + " с  •  " +
                (d.SourceHasAlpha ? "с альфа-каналом" : "без альфа-канала");
            fileLabel.Text = Path.GetFileName(d.SourcePath) + "   •   " + statusInfo;
            stageMeta.Text = "CANVAS  ·  " + (d.CropApplied ? "512 × 512" : d.Info.Width + " × " + d.Info.Height);
            sourceInfo.Text = "ИСХОДНИК\n" + Path.GetFileName(d.SourcePath) + "\n" +
                d.Info.Width + " × " + d.Info.Height + "   ·   " +
                d.Info.Duration.ToString("0.0") + " с   ·   " +
                (d.Info.Fps > 0 ? d.Info.Fps.ToString("0.##") + " fps" : "fps —");

            preview.SetFrame(d.FrameAt(d.State.CutStart));
            UpdateButtons();
            UpdateTimeLabel();
        }

        Rectangle CropToPreview(Rectangle orig)
        {
            if (orig.IsEmpty || doc == null) return Rectangle.Empty;
            double k = (double)doc.PreviewW / doc.Info.Width;
            return new Rectangle(
                (int)Math.Round(orig.X * k), (int)Math.Round(orig.Y * k),
                (int)Math.Round(orig.Width * k), (int)Math.Round(orig.Height * k));
        }

        Rectangle PreviewToCrop(RectangleF sel)
        {
            double k = (double)doc.Info.Width / doc.PreviewW;
            int size = (int)Math.Round(sel.Width * k);
            int x = (int)Math.Round(sel.X * k);
            int y = (int)Math.Round(sel.Y * k);
            size = Math.Min(size, Math.Min(doc.Info.Width, doc.Info.Height));
            x = Math.Max(0, Math.Min(doc.Info.Width - size, x));
            y = Math.Max(0, Math.Min(doc.Info.Height - size, y));
            return new Rectangle(x, y, size, size);
        }

        // ------------------------------------------------------------
        void UpdateButtons()
        {
            btnUndo.Enabled = doc != null && doc.CanUndo && !busy;

            bool blocked = ExportBlocked();
            btnExport.Accent = !blocked && !busy;
            btnExport.Enabled = !busy;
            // замок объясняет блокировку с первого взгляда (E72E Lock / E74E Save)
            btnExport.Glyph = blocked ? "" : "";
            btnExport.Text = busy ? "Экспортирую…" : (blocked ? "Сначала выбрать кроп" : "Собрать WebM");
            btnExport.Invalidate();
            tips.SetToolTip(btnExport, blocked
                ? "Видео больше 512px — сначала нажмите «Crop 1:1» и выберите зону стикера"
                : "Сделать стикер: WebM ≤256 КБ + обход лимита 3 сек");

            btnCrop.SetChecked(doc != null && doc.CropApplied);
            btnKey.SetChecked(doc != null && doc.State.Key.Enabled);

            readinessBadge.Text = blocked ? "НУЖЕН КРОП" : "ГОТОВО К ЭКСПОРТУ";
            readinessBadge.Tone = blocked ? Theme.Warn : Theme.Ok;
            readinessBadge.Invalidate();
            readinessTitle.Text = blocked ? "Остался один шаг" : "Все проверки пройдены";
            readinessDetail.Text = blocked
                ? "Выберите квадратную область для стикера."
                : "WebM будет собран под лимит 256 КБ.";

            cropBadge.Text = doc != null && doc.CropApplied
                ? "Кроп 1:1  ·  выбран"
                : (blocked ? "Кроп 1:1  ·  обязателен" : "Кроп 1:1  ·  не требуется");
            cropBadge.Tone = doc != null && doc.CropApplied ? Theme.Ok : (blocked ? Theme.Warn : Theme.TextMuted);
            cropBadge.Invalidate();

            if (doc != null && doc.SourceHasAlpha)
            {
                keyBadge.Text = "Альфа-канал  ·  сохранится";
                keyBadge.Tone = Theme.Ok;
            }
            else
            {
                keyBadge.Text = doc != null && doc.State.Key.Enabled
                    ? "Фон  ·  удаляется"
                    : "Фон  ·  без обработки";
                keyBadge.Tone = doc != null && doc.State.Key.Enabled ? Theme.Accent2 : Theme.TextMuted;
            }
            keyBadge.Invalidate();
            stageMeta.Text = "CANVAS  ·  " + (doc != null && doc.CropApplied
                ? "512 × 512" : (doc != null ? doc.Info.Width + " × " + doc.Info.Height : "—"));

            if (!busy && !showingResult)
            {
                statusLabel.ForeColor = blocked ? Theme.Warn : Theme.TextMuted;
                statusLabel.Text = blocked
                    ? "Кроп обязателен: исходник больше 512 px."
                    : "Готово к экспорту. Результат появится рядом с исходником.";
            }
        }

        bool ExportBlocked()
        {
            return doc != null && doc.CropRequired && !doc.CropApplied;
        }

        void UpdateTimeLabel()
        {
            if (doc == null) return;
            timeLabel.Text = string.Format(CultureInfo.InvariantCulture,
                "{0:0.0} с   /   {1:0.0}–{2:0.0}   ·   {3:0.0} с",
                timeline.Position, doc.State.CutStart, doc.State.CutEnd, doc.CutDuration);
        }

        // ---------------- playback ----------------
        void TogglePlay()
        {
            if (doc == null || busy) return;
            playing = !playing;
            btnPlay.Glyph = playing ? "" : "";
            btnPlay.Invalidate();
            if (playing)
            {
                playClock.Restart();
                playTimer.Start();
            }
            else
            {
                playOffset = CurrentPlayTime() - doc.State.CutStart;
                playClock.Stop();
                playTimer.Stop();
            }
        }

        double CurrentPlayTime()
        {
            double len = Math.Max(0.05, doc.CutDuration);
            double t = (playOffset + playClock.Elapsed.TotalSeconds) % len;
            return doc.State.CutStart + t;
        }

        void PlayTick()
        {
            if (doc == null || !playing) return;
            double t = CurrentPlayTime();
            timeline.Position = t;
            timeline.Invalidate();
            preview.SetFrame(doc.FrameAt(t));
            UpdateTimeLabel();
        }

        void OnSeek(double t)
        {
            if (doc == null) return;
            if (playing) TogglePlay();
            timeline.Position = t;
            playOffset = Math.Max(0, Math.Min(doc.CutDuration, t - doc.State.CutStart));
            timeline.Invalidate();
            preview.SetFrame(doc.FrameAt(t));
            UpdateTimeLabel();
        }

        void OnCutCommitted(EditState pre)
        {
            doc.PushUndoSnapshot(pre);
            showingResult = false;
            UpdateButtons();
            UpdateTimeLabel();
        }

        // ---------------- crop ----------------
        void StartCrop()
        {
            if (doc == null || busy) return;
            if (playing) TogglePlay();
            CloseKeyPanel(false);

            RectangleF init;
            if (doc.CropApplied)
                init = CropToPreview(doc.State.CropRect);
            else
            {
                float s = Math.Min(doc.PreviewW, doc.PreviewH);
                init = new RectangleF((doc.PreviewW - s) / 2f, (doc.PreviewH - s) / 2f, s, s);
            }
            preview.CropSel = init;
            preview.CropMode = true;
            preview.AppliedCropPreview = Rectangle.Empty;
            ShowCropInspector();
            preview.Invalidate();
        }

        void ApplyCrop()
        {
            doc.PushUndo();
            doc.State.CropRect = PreviewToCrop(preview.CropSel);
            showingResult = false;
            EndCropMode();
        }

        void CancelCrop()
        {
            EndCropMode();
        }

        void EndCropMode()
        {
            preview.CropMode = false;
            preview.AppliedCropPreview = CropToPreview(doc.State.CropRect);
            ShowDefaultInspector();
            preview.Invalidate();
            UpdateButtons();
        }

        // ---------------- chroma key ----------------
        void OpenKeyPanel()
        {
            if (doc == null || busy) return;
            if (preview.CropMode) CancelCrop();

            editingKey = doc.State.Key.Enabled ? doc.State.Key.Clone() : new KeySettings();
            editingKey.Enabled = true;
            slGain.Value = editingKey.Gain;
            slShrink.Value = editingKey.ShrinkGrow;
            UpdateKeyLabels();
            btnPick.SwatchColor = editingKey.ScreenColor;
            btnPick.Invalidate();

            ShowKeyInspector();
            preview.ActiveKey = editingKey;
            preview.BumpKeyVersion();
        }

        void TogglePick()
        {
            preview.PickMode = !preview.PickMode;
            btnPick.SetChecked(preview.PickMode);
        }

        void OnColorPicked(Color c)
        {
            preview.PickMode = false;
            btnPick.SetChecked(false);
            if (editingKey == null) return;
            editingKey.ScreenColor = c;
            btnPick.SwatchColor = c;
            btnPick.Invalidate();
            preview.BumpKeyVersion();
        }

        void OnKeyParamChanged()
        {
            if (editingKey == null) return;
            editingKey.Gain = slGain.Value;
            editingKey.ShrinkGrow = slShrink.Value;
            UpdateKeyLabels();
            preview.BumpKeyVersion();
        }

        void UpdateKeyLabels()
        {
            lbGain.Text = "Сила удаления  ·  " + slGain.Value;
            lbShrink.Text = "Край маски  ·  " + (slShrink.Value > 0 ? "+" : "") + slShrink.Value;
        }

        void ApplyKey()
        {
            doc.PushUndo();
            doc.State.Key = editingKey;
            showingResult = false;
            CloseKeyPanel(true);
        }

        void CancelKey()
        {
            CloseKeyPanel(false);
        }

        void CloseKeyPanel(bool applied)
        {
            ShowDefaultInspector();
            preview.PickMode = false;
            btnPick.SetChecked(false);
            editingKey = null;
            if (doc != null)
            {
                preview.ActiveKey = doc.State.Key;
                preview.BumpKeyVersion();
            }
            UpdateButtons();
        }

        void ShowDefaultInspector()
        {
            inspectorDefault.Visible = true;
            cropPanel.Visible = false;
            keyPanel.Visible = false;
            statusLabel.Visible = true;
            btnExport.Visible = true;
            inspectorTitle.Text = "Готовность";
            inspectorCaption.Text = "Проверка перед экспортом";
        }

        void ShowCropInspector()
        {
            inspectorDefault.Visible = false;
            cropPanel.Visible = true;
            keyPanel.Visible = false;
            statusLabel.Visible = false;
            btnExport.Visible = false;
            inspectorTitle.Text = "Кроп 1:1";
            inspectorCaption.Text = "Композиция будущего стикера";
            btnCrop.SetChecked(true);
        }

        void ShowKeyInspector()
        {
            inspectorDefault.Visible = false;
            cropPanel.Visible = false;
            keyPanel.Visible = true;
            statusLabel.Visible = false;
            btnExport.Visible = false;
            inspectorTitle.Text = "Удаление фона";
            inspectorCaption.Text = "Живой предпросмотр маски";
            btnKey.SetChecked(true);
        }

        // ---------------- undo ----------------
        void DoUndo()
        {
            if (doc == null || busy || !doc.CanUndo) return;
            if (preview.CropMode) CancelCrop();
            CloseKeyPanel(false);
            doc.Undo();
            showingResult = false;
            preview.ActiveKey = doc.State.Key;
            preview.AppliedCropPreview = CropToPreview(doc.State.CropRect);
            preview.BumpKeyVersion();
            timeline.Invalidate();
            UpdateButtons();
            UpdateTimeLabel();
        }

        // ---------------- export ----------------
        void DoExport()
        {
            if (doc == null || busy) return;
            if (ExportBlocked())
            {
                MessageBox.Show(this,
                    "Видео больше 512px, поэтому нужно выбрать квадратную зону стикера.\n" +
                    "Нажмите «Crop 1:1», выделите зону и нажмите «Применить кроп».",
                    "Сначала кроп", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (playing) TogglePlay();

            EditState snapshot = doc.State.Clone();

            // fps выше лимита Telegram — спрашиваем один раз на экспорте.
            // «Нет» — на нет и суда нет: оставляем частоту исходника как есть.
            if (doc.Info.Fps > 31)
            {
                DialogResult fr = MessageBox.Show(this,
                    "У видео " + doc.Info.Fps.ToString("0.##") + " fps — выше лимита Telegram (30).\n" +
                    "Telegram может отклонить такой стикер.\n\nСделать как надо — пересчитать в 30 fps?",
                    "FPS выше 30", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                snapshot.Fps30 = (fr == DialogResult.Yes);
            }

            string outPath = Path.Combine(
                Path.GetDirectoryName(doc.SourcePath) ?? "",
                Path.GetFileNameWithoutExtension(doc.SourcePath) + "_sticker.webm");

            busy = true;
            showingResult = false;
            UpdateButtons();
            statusLabel.ForeColor = Theme.TextMuted;
            statusLabel.Text = "Собираю WebM и подгоняю размер…";
            ProbeInfo info = doc.Info;
            bool srcAlpha = doc.SourceHasAlpha;
            string src = doc.SourcePath;

            Thread t = new Thread(delegate()
            {
                Action<string> prog = delegate(string s)
                {
                    try { BeginInvoke((MethodInvoker)delegate { statusLabel.Text = s; }); }
                    catch { }
                };
                ExportResult r = ExportPipeline.Run(ffmpegPath, src, info, srcAlpha,
                    snapshot, outPath, prog);
                try
                {
                    BeginInvoke((MethodInvoker)delegate { ExportDone(r); });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void ExportDone(ExportResult r)
        {
            busy = false;
            showingResult = true;
            UpdateButtons();
            if (!r.Ok)
            {
                statusLabel.ForeColor = Theme.Err;
                statusLabel.Text = "✗ " + r.Error;
                return;
            }
            string kb = (r.Size / 1024.0).ToString("0") + " КБ";
            statusLabel.ForeColor = r.FpsWarning ? Theme.Warn : Theme.Ok;
            statusLabel.Text = (r.FpsWarning ? "⚠ " : "✓ ") + "Готово → " +
                Path.GetFileName(r.OutputPath) + " (" + kb +
                (r.AlphaInOutput ? ", с альфой" : ", без альфы") + ")" +
                (r.FpsWarning ? " — fps выше 30, Telegram может отклонить" : "");

            DialogResult d = MessageBox.Show(this,
                "Стикер готов: " + Path.GetFileName(r.OutputPath) + " (" + kb + ")\n\nПоказать в Проводнике?",
                "Экспорт завершён", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (d == DialogResult.Yes)
            {
                try { Process.Start("explorer.exe", "/select,\"" + r.OutputPath + "\""); }
                catch { }
            }
        }

        public void StopAll()
        {
            playTimer.Stop();
            playing = false;
        }

        // Горячие клавиши редактора (форвардятся из MainForm.ProcessCmdKey)
        public bool HandleKey(Keys keyData)
        {
            if (doc == null || busy) return false;
            if (keyData == Keys.Space)
            {
                // не воруем пробел у слайдеров/кнопок в фокусе — но у нас
                // нет текстовых полей, так что глобальный play/pause безопасен
                TogglePlay();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Z)) { DoUndo(); return true; }
            if (keyData == Keys.Left || keyData == Keys.Right)
            {
                // стрелки отдаём слайдерам, когда фокус на них
                if (slGain.Focused || slShrink.Focused) return false;
                StepFrame(keyData == Keys.Right ? 1 : -1);
                return true;
            }
            return false;
        }

        void StepFrame(int dir)
        {
            if (playing) TogglePlay();
            int f = preview.CurrentFrame + dir;
            f = Math.Max(0, Math.Min(doc.Frames.Count - 1, f));
            double t = doc.TimeOfFrame(f);
            timeline.Position = t;
            playOffset = Math.Max(0, Math.Min(doc.CutDuration, t - doc.State.CutStart));
            timeline.Invalidate();
            preview.SetFrame(f);
            UpdateTimeLabel();
        }
    }
}
