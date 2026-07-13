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
        Panel toolRailDivider, inspectorDivider;
        Panel inspector, inspectorDefault, keyPanel, cropPanel;
        SurfacePanel readinessCard, sourceChip;
        UxLiveMark studioMark;
        StatusRow readinessBadge, cropBadge, keyBadge;
        Label timeLabel, statusLabel, lbGain, lbShrink, cropHint;
        Label fileLabel, fileMetaLabel, appLabel, appMetaLabel;
        Label stageTitle, stageMeta, inspectorTitle, inspectorCaption;
        Label readinessTitle, readinessDetail, sourceInfo, timelineTitle;
        NiceSlider slGain, slShrink;
        ToolTip tips;

        KeySettings editingKey;

        System.Windows.Forms.Timer playTimer;
        Stopwatch playClock = new Stopwatch();
        double playOffset;
        bool playing;
        bool busy;
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
            toolbar.Dock = DockStyle.None;
            toolbar.Height = Theme.S(76);
            toolbar.BackColor = Theme.BackMain;
            toolbar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(30, Color.White), 1f))
                    e.Graphics.DrawLine(p, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
            };

            studioMark = new UxLiveMark();
            studioMark.SetBounds(Theme.S(18), Theme.S(17), Theme.S(42), Theme.S(42));
            toolbar.Controls.Add(studioMark);

            appLabel = new SmoothLabel();
            appLabel.AutoSize = true;
            appLabel.Text = "uxlive";
            appLabel.ForeColor = Theme.TextMain;
            appLabel.Font = new Font(Theme.DisplayFont, 13f);
            appLabel.Location = new Point(Theme.S(71), Theme.S(15));
            toolbar.Controls.Add(appLabel);

            appMetaLabel = new SmoothLabel();
            appMetaLabel.AutoSize = true;
            appMetaLabel.Text = "Sticker Studio  /  Telegram";
            appMetaLabel.ForeColor = Theme.TextMuted;
            appMetaLabel.Font = new Font(Theme.BodyFont, 8.75f);
            appMetaLabel.Location = new Point(Theme.S(72), Theme.S(41));
            toolbar.Controls.Add(appMetaLabel);

            sourceChip = new SurfacePanel();
            sourceChip.FillColor = Theme.Surface;
            sourceChip.BackColor = Theme.Surface;
            sourceChip.StrokeColor = Theme.BorderIdle;
            sourceChip.Radius = 10;
            toolbar.Controls.Add(sourceChip);

            fileLabel = new SmoothLabel();
            fileLabel.ForeColor = Theme.TextMain;
            fileLabel.TextAlign = ContentAlignment.MiddleLeft;
            fileLabel.Font = new Font(Theme.BodySemiboldFont, 9f);
            fileLabel.AutoEllipsis = true;
            sourceChip.Controls.Add(fileLabel);

            fileMetaLabel = new SmoothLabel();
            fileMetaLabel.ForeColor = Theme.TextMuted;
            fileMetaLabel.TextAlign = ContentAlignment.MiddleLeft;
            fileMetaLabel.Font = new Font(Theme.BodyFont, 8.25f);
            fileMetaLabel.AutoEllipsis = true;
            sourceChip.Controls.Add(fileMetaLabel);

            btnBack = MakeBtn(StudioIcon.Back, "Новое видео", 0, Theme.S(132));
            btnUndo = MakeBtn(StudioIcon.Undo, "Отменить", 0, Theme.S(112));
            btnBack.Ghost = true;
            btnUndo.Border = true;
            toolbar.Controls.Add(btnBack);
            toolbar.Controls.Add(btnUndo);

            btnBack.Click += delegate { if (!busy && BackRequested != null) BackRequested(); };
            btnUndo.Click += delegate { DoUndo(); };
            tips.SetToolTip(btnBack, "Открыть другое видео (Ctrl+O)");
            tips.SetToolTip(btnUndo, "Откатить последнее действие (Ctrl+Z)");

            // ---------- workspace shell ----------
            content = new Panel();
            content.Dock = DockStyle.None;
            content.BackColor = Theme.BackMain;

            toolRail = new Panel();
            toolRail.Dock = DockStyle.None;
            toolRail.Width = Theme.S(84);
            toolRail.BackColor = Theme.BackPanel;

            btnCrop = MakeToolBtn(StudioIcon.Crop, "Обрезать", Theme.S(14));
            btnKey = MakeToolBtn(StudioIcon.Background, "Убрать фон", Theme.S(90));
            btnCrop.Click += delegate { StartCrop(); };
            btnKey.Click += delegate { OpenKeyPanel(); };
            toolRail.Controls.Add(btnCrop);
            toolRail.Controls.Add(btnKey);
            toolRailDivider = new Panel();
            toolRailDivider.Dock = DockStyle.Right;
            toolRailDivider.Width = Math.Max(1, Theme.S(1));
            toolRailDivider.BackColor = Color.FromArgb(42, Color.White);
            toolRail.Controls.Add(toolRailDivider);
            toolRailDivider.BringToFront();
            tips.SetToolTip(btnCrop, "Выбрать квадратную зону стикера 512 × 512 (C)");
            tips.SetToolTip(btnKey, "Убрать однотонный фон (B)");

            mainColumn = new Panel();
            mainColumn.Dock = DockStyle.None;
            mainColumn.BackColor = Theme.BackMain;

            stageHost = new Panel();
            stageHost.Dock = DockStyle.Fill;
            stageHost.BackColor = Theme.BackMain;
            stageHost.Resize += delegate { LayoutStage(); };

            stageTitle = new SmoothLabel();
            stageTitle.Text = "Предпросмотр";
            stageTitle.ForeColor = Theme.TextMain;
            stageTitle.Font = new Font(Theme.BodySemiboldFont, 10.5f);
            stageHost.Controls.Add(stageTitle);

            stageMeta = new SmoothLabel();
            stageMeta.ForeColor = Theme.TextMuted;
            stageMeta.TextAlign = ContentAlignment.MiddleRight;
            stageMeta.Font = new Font(Theme.BodyFont, 9f);
            stageHost.Controls.Add(stageMeta);

            preview = new PreviewControl();
            preview.ColorPicked += OnColorPicked;
            stageHost.Controls.Add(preview);

            bottomBar = new Panel();
            bottomBar.Dock = DockStyle.Bottom;
            bottomBar.Height = Theme.S(140);
            bottomBar.BackColor = Theme.Surface;
            bottomBar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(36, Color.White), 1f))
                    e.Graphics.DrawLine(p, 0, 0, bottomBar.Width, 0);
            };

            timelineTitle = new SmoothLabel();
            timelineTitle.Text = "Фрагмент";
            timelineTitle.ForeColor = Theme.TextMuted;
            timelineTitle.Font = new Font(Theme.BodySemiboldFont, 9f);
            bottomBar.Controls.Add(timelineTitle);

            btnPlay = new StyledButton();
            btnPlay.Icon = StudioIcon.Play;
            btnPlay.RoundFull = true;
            btnPlay.Accent = true;
            btnPlay.Click += delegate { TogglePlay(); };
            tips.SetToolTip(btnPlay, "Воспроизведение / пауза (Пробел). Без звука.");

            timeline = new TimelineControl();
            timeline.SeekRequested += OnSeek;
            timeline.CutChanging += delegate { UpdateTimeLabel(); };
            timeline.CutCommitted += OnCutCommitted;

            timeLabel = new SmoothLabel();
            timeLabel.ForeColor = Theme.TextMuted;
            timeLabel.TextAlign = ContentAlignment.MiddleRight;
            timeLabel.Font = new Font("Consolas", 9f);

            bottomBar.Controls.Add(btnPlay);
            bottomBar.Controls.Add(timeline);
            bottomBar.Controls.Add(timeLabel);

            // ---------- readiness / tool inspector ----------
            inspector = new Panel();
            inspector.Dock = DockStyle.None;
            inspector.Width = Theme.S(340);
            inspector.BackColor = Theme.BackPanel;

            inspectorTitle = new SmoothLabel();
            inspectorTitle.Text = "Готовность";
            inspectorTitle.ForeColor = Theme.TextMain;
            inspectorTitle.Font = new Font(Theme.DisplayFont, 14f);
            inspector.Controls.Add(inspectorTitle);

            inspectorCaption = new SmoothLabel();
            inspectorCaption.Text = "Проверка перед экспортом";
            inspectorCaption.ForeColor = Theme.TextMuted;
            inspectorCaption.Font = new Font(Theme.BodyFont, 9f);
            inspector.Controls.Add(inspectorCaption);

            BuildDefaultInspector();
            BuildCropInspector();
            BuildKeyInspector();

            statusLabel = new SmoothLabel();
            statusLabel.ForeColor = Theme.TextMuted;
            statusLabel.TextAlign = ContentAlignment.TopLeft;
            statusLabel.Font = new Font(Theme.BodyFont, 9f);
            inspector.Controls.Add(statusLabel);

            btnExport = new StyledButton();
            btnExport.Icon = StudioIcon.Export;
            btnExport.Text = "Экспортировать WebM";
            btnExport.Font = new Font(Theme.BodySemiboldFont, 10f);
            btnExport.Click += delegate { DoExport(); };
            inspector.Controls.Add(btnExport);

            inspectorDivider = new Panel();
            inspectorDivider.Dock = DockStyle.Left;
            inspectorDivider.Width = Math.Max(1, Theme.S(1));
            inspectorDivider.BackColor = Color.FromArgb(44, Color.White);
            inspector.Controls.Add(inspectorDivider);
            inspectorDivider.BringToFront();

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

            Resize += delegate { LayoutShell(); };
            LayoutShell();

            LayoutToolbar();
            LayoutStage();
            LayoutBottomBar();
            LayoutInspector();

            playTimer = new System.Windows.Forms.Timer();
            playTimer.Interval = 15;
            playTimer.Tick += delegate { PlayTick(); };
        }

        void LayoutShell()
        {
            toolbar.SetBounds(0, 0, ClientSize.Width, Theme.S(76));
            content.SetBounds(0, toolbar.Bottom, ClientSize.Width,
                Math.Max(0, ClientSize.Height - toolbar.Height));
            if (inspector != null && toolRail != null && mainColumn != null)
            {
                int railW = Theme.S(84);
                int inspectorW = Theme.S(ClientSize.Width >= Theme.S(1280) ? 340 : 320);
                int workspaceH = content.ClientSize.Height;
                int workspaceW = content.ClientSize.Width;
                toolRail.SetBounds(0, 0, railW, workspaceH);
                inspector.SetBounds(Math.Max(railW, workspaceW - inspectorW), 0,
                    inspectorW, workspaceH);
                mainColumn.SetBounds(railW, 0,
                    Math.Max(1, workspaceW - railW - inspectorW), workspaceH);
            }
        }

        void LayoutBottomBar()
        {
            int W = bottomBar.ClientSize.Width;
            if (W < 50) W = Theme.S(900);
            timelineTitle.SetBounds(Theme.S(24), Theme.S(14), Theme.S(100), Theme.S(22));
            timeLabel.SetBounds(Theme.S(130), Theme.S(12),
                Math.Max(Theme.S(180), W - Theme.S(154)), Theme.S(24));
            btnPlay.SetBounds(Theme.S(22), Theme.S(64), Theme.S(44), Theme.S(44));
            timeline.SetBounds(Theme.S(78), Theme.S(38),
                Math.Max(Theme.S(100), W - Theme.S(102)), Theme.S(86));
        }

        void LayoutToolbar()
        {
            int W = toolbar.ClientSize.Width;
            btnBack.SetBounds(W - Theme.S(292), Theme.S(17), Theme.S(140), Theme.S(42));
            btnUndo.SetBounds(W - Theme.S(140), Theme.S(17), Theme.S(120), Theme.S(42));
            int sourceX = Theme.S(266);
            int sourceW = Math.Max(Theme.S(180), btnBack.Left - Theme.S(16) - sourceX);
            sourceChip.SetBounds(sourceX, Theme.S(15), sourceW, Theme.S(46));
            fileLabel.SetBounds(Theme.S(12), Theme.S(4),
                Math.Max(Theme.S(80), sourceChip.Width - Theme.S(24)), Theme.S(20));
            fileMetaLabel.SetBounds(Theme.S(12), Theme.S(23),
                Math.Max(Theme.S(80), sourceChip.Width - Theme.S(24)), Theme.S(17));
        }

        void LayoutStage()
        {
            int W = stageHost.ClientSize.Width;
            int H = stageHost.ClientSize.Height;
            stageTitle.SetBounds(Theme.S(24), Theme.S(18), Theme.S(180), Theme.S(24));
            stageMeta.Visible = W >= Theme.S(360);
            if (stageMeta.Visible)
            {
                int metaX = Math.Max(Theme.S(190), W - Theme.S(220));
                stageMeta.SetBounds(metaX, Theme.S(18),
                    Math.Max(Theme.S(94), W - metaX - Theme.S(24)), Theme.S(24));
            }
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
            int dividerW = Math.Max(1, Theme.S(1));
            inspectorDefault.SetBounds(dividerW, panelTop, Math.Max(1, W - dividerW),
                Math.Max(Theme.S(180), statusY - panelTop));
            cropPanel.SetBounds(dividerW, panelTop, Math.Max(1, W - dividerW),
                Math.Max(Theme.S(210), H - panelTop - Theme.S(20)));
            keyPanel.SetBounds(dividerW, panelTop, Math.Max(1, W - dividerW),
                Math.Max(Theme.S(310), H - panelTop - Theme.S(20)));
            statusLabel.SetBounds(pad, statusY, W - pad * 2, Theme.S(46));
            btnExport.SetBounds(pad, exportY, W - pad * 2, exportH);

            readinessCard.SetBounds(pad, Theme.S(4), W - pad * 2, Theme.S(132));
            readinessBadge.SetBounds(Theme.S(16), Theme.S(14),
                readinessCard.Width - Theme.S(32), Theme.S(42));
            readinessTitle.SetBounds(Theme.S(16), Theme.S(66),
                readinessCard.Width - Theme.S(32), Theme.S(25));
            readinessDetail.SetBounds(Theme.S(16), Theme.S(92),
                readinessCard.Width - Theme.S(32), Theme.S(30));
            cropBadge.SetBounds(pad, Theme.S(152), W - pad * 2, Theme.S(46));
            keyBadge.SetBounds(pad, Theme.S(208), W - pad * 2, Theme.S(46));
            sourceInfo.SetBounds(pad, Theme.S(276), W - pad * 2, Theme.S(88));

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
            readinessCard.Radius = 12;

            readinessBadge = new StatusRow();
            readinessBadge.Strong = true;
            readinessBadge.SetStatus("Статус", "проверка", StudioIcon.Check, Theme.Accent);

            readinessTitle = new SmoothLabel();
            readinessTitle.Text = "Подготовка проекта";
            readinessTitle.ForeColor = Theme.TextMain;
            readinessTitle.Font = new Font(Theme.BodySemiboldFont, 10.25f);

            readinessDetail = new SmoothLabel();
            readinessDetail.Text = "Проверяю параметры исходника";
            readinessDetail.ForeColor = Theme.TextMuted;
            readinessDetail.Font = new Font(Theme.BodyFont, 9f);

            readinessCard.Controls.Add(readinessBadge);
            readinessCard.Controls.Add(readinessTitle);
            readinessCard.Controls.Add(readinessDetail);

            cropBadge = new StatusRow();
            cropBadge.SetStatus("Квадрат 1:1", "проверка", StudioIcon.Crop, Theme.TextMuted);

            keyBadge = new StatusRow();
            keyBadge.SetStatus("Фон", "без обработки", StudioIcon.Background, Theme.TextMuted);

            sourceInfo = new SmoothLabel();
            sourceInfo.ForeColor = Theme.TextMuted;
            sourceInfo.Font = new Font(Theme.BodyFont, 9f);
            sourceInfo.Text = "Исходник\nНет данных";

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

            cropHint = MakeInspectorLabel(
                "Перемещайте рамку за центр и тяните за углы. Результат будет 512 × 512.",
                9f, Theme.TextMuted, false);
            cropHint.SetBounds(Theme.S(20), Theme.S(10), Theme.S(270), Theme.S(58));

            PillLabel ratio = new PillLabel();
            ratio.Text = "Выход  /  512 × 512";
            ratio.Tone = Theme.Accent;
            ratio.Dot = false;
            ratio.SetBounds(Theme.S(20), Theme.S(82), Theme.S(180), Theme.S(30));

            btnCropApply = new StyledButton();
            btnCropApply.Icon = StudioIcon.Check;
            btnCropApply.Text = "Применить обрезку";
            btnCropApply.Accent = true;
            btnCropApply.Font = new Font(Theme.BodySemiboldFont, 9.5f);
            btnCropCancel = new StyledButton();
            btnCropCancel.Icon = StudioIcon.Close;
            btnCropCancel.Text = "Отмена";
            btnCropCancel.Ghost = true;
            btnCropCancel.Border = true;
            btnCropApply.Click += delegate { ApplyCrop(); };
            btnCropCancel.Click += delegate { CancelCrop(); };

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

            Label hint = MakeInspectorLabel(
                "Выберите цвет на видео, затем уточните края маски.",
                9f, Theme.TextMuted, false);
            hint.SetBounds(Theme.S(20), Theme.S(10), Theme.S(270), Theme.S(42));

            btnPick = new StyledButton();
            btnPick.Text = "Выбрать цвет на видео";
            btnPick.Icon = StudioIcon.Eyedropper;
            btnPick.Border = true;
            btnPick.SwatchColor = Color.FromArgb(0, 255, 0);
            btnPick.Click += delegate { TogglePick(); };
            tips.SetToolTip(btnPick, "Кликните по цвету фона на видео");

            lbGain = MakeInspectorLabel("Сила удаления: 100", 9f, Theme.TextSoft, true);
            slGain = new NiceSlider();
            slGain.Minimum = 0; slGain.Maximum = 200; slGain.Value = 100;
            slGain.AccessibleName = "Сила удаления фона";
            slGain.ValueChanged += delegate { OnKeyParamChanged(); };
            tips.SetToolTip(slGain, "Сила вырезания фона");

            lbShrink = MakeInspectorLabel("Край маски: 0", 9f, Theme.TextSoft, true);
            slShrink = new NiceSlider();
            slShrink.Minimum = -100; slShrink.Maximum = 100; slShrink.Value = 0;
            slShrink.AccessibleName = "Край маски";
            slShrink.ValueChanged += delegate { OnKeyParamChanged(); };
            tips.SetToolTip(slShrink, "Поджать (−) или расширить (+) края маски");

            btnKeyApply = new StyledButton();
            btnKeyApply.Icon = StudioIcon.Check;
            btnKeyApply.Text = "Применить фон";
            btnKeyApply.Accent = true;
            btnKeyApply.Font = new Font(Theme.BodySemiboldFont, 9.5f);
            btnKeyCancel = new StyledButton();
            btnKeyCancel.Icon = StudioIcon.Close;
            btnKeyCancel.Text = "Отмена";
            btnKeyCancel.Ghost = true;
            btnKeyCancel.Border = true;
            btnKeyApply.Click += delegate { ApplyKey(); };
            btnKeyCancel.Click += delegate { CancelKey(); };

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
            Label l = new SmoothLabel();
            l.Text = text;
            l.ForeColor = color;
            l.Font = new Font(semibold ? Theme.BodySemiboldFont : Theme.BodyFont, size);
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
            btnPick.SetBounds(pad, Theme.S(54), innerW, Theme.S(40));
            lbGain.SetBounds(pad, Theme.S(104), innerW, Theme.S(22));
            slGain.SetBounds(pad, Theme.S(126), innerW, Theme.S(28));
            lbShrink.SetBounds(pad, Theme.S(160), innerW, Theme.S(22));
            slShrink.SetBounds(pad, Theme.S(182), innerW, Theme.S(28));
            int groupY = Math.Max(Theme.S(218), H - Theme.S(90));
            btnKeyApply.SetBounds(pad, groupY, innerW, Theme.S(44));
            btnKeyCancel.SetBounds(pad, btnKeyApply.Bottom + Theme.S(8),
                innerW, Theme.S(38));
        }

        StyledButton MakeBtn(StudioIcon icon, string text, int x, int w)
        {
            StyledButton b = new StyledButton();
            b.Icon = icon;
            b.Text = text;
            b.SetBounds(x, Theme.S(13), w, Theme.S(36));
            b.AccessibleName = text;
            return b;
        }

        StyledButton MakeToolBtn(StudioIcon icon, string text, int y)
        {
            StyledButton b = new StyledButton();
            b.Icon = icon;
            b.Text = text;
            b.Vertical = true;
            b.Ghost = true;
            b.Font = new Font(Theme.BodySemiboldFont, 9f);
            b.SetBounds(Theme.S(7), y, Theme.S(70), Theme.S(70));
            b.AccessibleName = text;
            return b;
        }

        Label MakeLbl(string text, int x, int w)
        {
            Label l = new SmoothLabel();
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
            btnPlay.Icon = StudioIcon.Play;
            btnPlay.Invalidate();
            busy = false;
            showingResult = false;

            fileLabel.Text = Path.GetFileName(d.SourcePath);
            fileMetaLabel.Text = d.Info.Width + " × " + d.Info.Height + "  /  " +
                d.Info.Duration.ToString("0.0") + " с  /  " +
                (d.Info.Fps > 0 ? d.Info.Fps.ToString("0.##") + " fps" : "fps: нет данных");
            stageMeta.Text = "Холст  /  " + (d.CropApplied ? "512 × 512" : d.Info.Width + " × " + d.Info.Height);
            sourceInfo.Text = "Исходник\n" +
                d.Info.Width + " × " + d.Info.Height + "  /  " +
                d.Info.Duration.ToString("0.0") + " с  /  " +
                (d.Info.Fps > 0 ? d.Info.Fps.ToString("0.##") + " fps" : "fps: нет данных");

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
            btnExport.Icon = blocked ? StudioIcon.Lock : StudioIcon.Export;
            btnExport.Text = busy ? "Экспортирую…" : (blocked ? "Сначала обрезать кадр" : "Экспортировать WebM");
            btnExport.Invalidate();
            tips.SetToolTip(btnExport, blocked
                ? "Видео больше 512 px. Сначала выберите квадратную область."
                : "Экспортировать WebM до 256 КБ (Ctrl+E)");

            btnCrop.SetChecked(doc != null && doc.CropApplied);
            btnKey.SetChecked(doc != null && doc.State.Key.Enabled);

            readinessBadge.SetStatus("Статус",
                blocked ? "нужна обрезка" : "можно экспортировать",
                blocked ? StudioIcon.Lock : StudioIcon.Check,
                blocked ? Theme.Warn : Theme.Ok);
            readinessTitle.Text = blocked ? "Остался один шаг" : "Все проверки пройдены";
            readinessDetail.Text = blocked
                ? "Выберите квадрат 1:1 для стикера."
                : "WebM будет собран под лимит 256 КБ.";

            cropBadge.SetStatus("Квадрат 1:1",
                doc != null && doc.CropApplied
                    ? "выбран"
                    : (blocked ? "обязателен" : "не требуется"),
                StudioIcon.Crop,
                doc != null && doc.CropApplied ? Theme.Ok : (blocked ? Theme.Warn : Theme.TextMuted));

            if (doc != null && doc.SourceHasAlpha)
            {
                keyBadge.SetStatus("Альфа-канал", "сохранится", StudioIcon.Check, Theme.Ok);
            }
            else
            {
                bool backgroundRemoved = doc != null && doc.State.Key.Enabled;
                keyBadge.SetStatus("Фон",
                    backgroundRemoved ? "удалён" : "без обработки",
                    backgroundRemoved ? StudioIcon.Check : StudioIcon.Background,
                    backgroundRemoved ? Theme.Ok : Theme.TextMuted);
            }
            stageMeta.Text = "Холст  /  " + (doc != null && doc.CropApplied
                ? "512 × 512" : (doc != null ? doc.Info.Width + " × " + doc.Info.Height : "Нет данных"));

            if (!busy && !showingResult)
            {
                statusLabel.ForeColor = blocked ? Theme.Warn : Theme.TextMuted;
                statusLabel.Text = blocked
                    ? "Обрезка обязательна: исходник больше 512 px."
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
                "{0:0.0} с   /   {1:0.0}-{2:0.0}   /   {3:0.0} с",
                timeline.Position, doc.State.CutStart, doc.State.CutEnd, doc.CutDuration);
        }

        // ---------------- playback ----------------
        void TogglePlay()
        {
            if (doc == null || busy) return;
            playing = !playing;
            btnPlay.Icon = playing ? StudioIcon.Pause : StudioIcon.Play;
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
            lbGain.Text = "Сила удаления: " + slGain.Value;
            lbShrink.Text = "Край маски: " + (slShrink.Value > 0 ? "+" : "") + slShrink.Value;
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
            inspectorTitle.Text = "Обрезка 1:1";
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
                    "Видео больше 512 px, поэтому нужно выбрать квадратную зону стикера.\n" +
                    "Нажмите «Обрезать», выделите зону и примените обрезку.",
                    "Сначала обрезка", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (playing) TogglePlay();

            EditState snapshot = doc.State.Clone();

            // fps выше лимита Telegram - спрашиваем один раз на экспорте.
            // «Нет» - на нет и суда нет: оставляем частоту исходника как есть.
            if (doc.Info.Fps > 31)
            {
                DialogResult fr = MessageBox.Show(this,
                    "У видео " + doc.Info.Fps.ToString("0.##") + " fps, это выше лимита Telegram (30).\n" +
                    "Telegram может отклонить такой стикер.\n\nПересчитать видео в 30 fps?",
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
                (r.FpsWarning ? ". Частота выше 30 fps, Telegram может отклонить файл" : "");

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
            if (keyData == (Keys.Control | Keys.O))
            {
                if (BackRequested != null) BackRequested();
                return true;
            }
            if (keyData == (Keys.Control | Keys.E)) { DoExport(); return true; }
            if (keyData == Keys.C) { StartCrop(); return true; }
            if (keyData == Keys.B && btnKey.Visible) { OpenKeyPanel(); return true; }
            if (keyData == Keys.Escape)
            {
                if (preview.CropMode) { CancelCrop(); return true; }
                if (keyPanel.Visible) { CloseKeyPanel(false); return true; }
                if (playing) { TogglePlay(); return true; }
                return false;
            }
            if (keyData == Keys.Space)
            {
                if (btnBack.Focused || btnUndo.Focused || btnCrop.Focused || btnKey.Focused ||
                    btnPlay.Focused || btnExport.Focused || btnPick.Focused ||
                    btnCropApply.Focused || btnCropCancel.Focused ||
                    btnKeyApply.Focused || btnKeyCancel.Focused ||
                    slGain.Focused || slShrink.Focused)
                    return false;
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
