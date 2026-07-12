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
        Panel toolbar, bottomBar, footer, keyPanel, cropPanel;
        Label timeLabel, statusLabel, lbGain, lbShrink, cropHint;
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
            // ---------- верхняя панель ----------
            toolbar = new Panel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = Theme.S(62);
            toolbar.BackColor = Theme.BackMain;

            btnBack = MakeBtn("", "Другое видео", Theme.S(16), Theme.S(150));
            btnUndo = MakeBtn("", "Шаг назад", Theme.S(192), Theme.S(126));
            btnCrop = MakeBtn("", "Crop 1:1", Theme.S(330), Theme.S(120));
            btnKey = MakeBtn("", "Удалить фон", Theme.S(462), Theme.S(150));
            btnBack.Ghost = true; // навигация, не инструмент — не конкурирует за внимание
            toolbar.Controls.Add(btnBack);
            toolbar.Controls.Add(btnUndo);
            toolbar.Controls.Add(btnCrop);
            toolbar.Controls.Add(btnKey);

            btnBack.Click += delegate { if (!busy && BackRequested != null) BackRequested(); };
            btnUndo.Click += delegate { DoUndo(); };
            btnCrop.Click += delegate { StartCrop(); };
            btnKey.Click += delegate { OpenKeyPanel(); };
            tips.SetToolTip(btnUndo, "Откатить последнее действие (Ctrl+Z)");
            tips.SetToolTip(btnCrop, "Выбрать квадратную зону стикера (512×512)");
            tips.SetToolTip(btnKey, "Убрать однотонный фон (хромакей)");

            // ---------- панель хромакея ----------
            keyPanel = new Panel();
            keyPanel.Dock = DockStyle.Top;
            keyPanel.Height = Theme.S(60);
            keyPanel.BackColor = Theme.BackPanel;
            keyPanel.Visible = false;

            btnPick = MakeBtn(null, "Пипетка", Theme.S(10), Theme.S(140));
            btnPick.SwatchColor = Color.FromArgb(0, 255, 0);
            btnPick.Click += delegate { TogglePick(); };
            tips.SetToolTip(btnPick, "Кликните по цвету фона на видео (Screen colour)");

            lbGain = MakeLbl("Gain: 100", Theme.S(164), Theme.S(72));
            slGain = new NiceSlider();
            slGain.Minimum = 0; slGain.Maximum = 200; slGain.Value = 100;
            slGain.SetBounds(Theme.S(236), Theme.S(12), Theme.S(140), Theme.S(32));
            slGain.ValueChanged += delegate { OnKeyParamChanged(); };
            tips.SetToolTip(slGain, "Сила вырезания фона");

            lbShrink = MakeLbl("Shrink/Grow: 0", Theme.S(388), Theme.S(103));
            slShrink = new NiceSlider();
            slShrink.Minimum = -100; slShrink.Maximum = 100; slShrink.Value = 0;
            slShrink.SetBounds(Theme.S(491), Theme.S(12), Theme.S(140), Theme.S(32));
            slShrink.ValueChanged += delegate { OnKeyParamChanged(); };
            tips.SetToolTip(slShrink, "Поджать (−) или расширить (+) края маски");

            btnKeyApply = MakeBtn("", "Применить", 0, Theme.S(130));
            btnKeyApply.Accent = true;
            btnKeyCancel = MakeBtn("", "Отмена", 0, Theme.S(100));
            btnKeyApply.Click += delegate { ApplyKey(); };
            btnKeyCancel.Click += delegate { CancelKey(); };

            keyPanel.Controls.Add(btnPick);
            keyPanel.Controls.Add(lbGain);
            keyPanel.Controls.Add(slGain);
            keyPanel.Controls.Add(lbShrink);
            keyPanel.Controls.Add(slShrink);
            keyPanel.Controls.Add(btnKeyApply);
            keyPanel.Controls.Add(btnKeyCancel);
            keyPanel.Resize += delegate { LayoutRightButtons(keyPanel, btnKeyApply, btnKeyCancel); };

            // ---------- панель кропа ----------
            cropPanel = new Panel();
            cropPanel.Dock = DockStyle.Top;
            cropPanel.Height = Theme.S(60);
            cropPanel.BackColor = Theme.BackPanel;
            cropPanel.Visible = false;

            cropHint = MakeLbl("Выделите квадратную зону — стикер всегда будет 512×512", Theme.S(12), Theme.S(430));
            btnCropApply = MakeBtn("", "Применить кроп", 0, Theme.S(160));
            btnCropApply.Accent = true;
            btnCropCancel = MakeBtn("", "Отмена", 0, Theme.S(100));
            btnCropApply.Click += delegate { ApplyCrop(); };
            btnCropCancel.Click += delegate { CancelCrop(); };
            cropPanel.Controls.Add(cropHint);
            cropPanel.Controls.Add(btnCropApply);
            cropPanel.Controls.Add(btnCropCancel);
            cropPanel.Resize += delegate { LayoutRightButtons(cropPanel, btnCropApply, btnCropCancel); };

            // ---------- транспорт: play + таймлайн ----------
            bottomBar = new Panel();
            bottomBar.Dock = DockStyle.Bottom;
            bottomBar.Height = Theme.S(86);
            bottomBar.BackColor = Theme.BackPanel;

            btnPlay = new StyledButton();
            btnPlay.Glyph = "";
            btnPlay.GlyphSize = 13f;
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
            timeLabel.Font = new Font("Segoe UI", 8.75f);

            statusLabel = new Label();
            statusLabel.ForeColor = Theme.TextMuted;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            btnExport = new StyledButton();
            btnExport.Glyph = "";
            btnExport.Text = "Экспорт";
            btnExport.Font = new Font("Segoe UI Semibold", 10f);
            btnExport.Click += delegate { DoExport(); };

            bottomBar.Controls.Add(btnPlay);
            bottomBar.Controls.Add(timeline);
            bottomBar.Controls.Add(timeLabel);

            // ---------- подвал: статус + экспорт, отделён от рабочей зоны ----------
            footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.S(58);
            footer.BackColor = Theme.BackFooter;
            footer.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(45, 255, 255, 255), 1f))
                    e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };
            footer.Controls.Add(statusLabel);
            footer.Controls.Add(btnExport);

            bottomBar.Resize += delegate { LayoutBottomBar(); };
            footer.Resize += delegate { LayoutFooter(); };
            LayoutBottomBar();
            LayoutFooter();

            preview = new PreviewControl();
            preview.Dock = DockStyle.Fill;
            preview.ColorPicked += OnColorPicked;

            Controls.Add(preview);
            Controls.Add(bottomBar);
            Controls.Add(footer);
            Controls.Add(cropPanel);
            Controls.Add(keyPanel);
            Controls.Add(toolbar);

            playTimer = new System.Windows.Forms.Timer();
            playTimer.Interval = 15;
            playTimer.Tick += delegate { PlayTick(); };
        }

        void LayoutBottomBar()
        {
            int W = bottomBar.ClientSize.Width;
            if (W < 50) W = Theme.S(900);
            btnPlay.SetBounds(Theme.S(16), Theme.S(12), Theme.S(46), Theme.S(46));
            timeline.SetBounds(Theme.S(78), Theme.S(8),
                Math.Max(Theme.S(100), W - Theme.S(78) - Theme.S(16)), Theme.S(50));
            timeLabel.SetBounds(Theme.S(78), Theme.S(62), Theme.S(460), Theme.S(16));
        }

        void LayoutFooter()
        {
            int W = footer.ClientSize.Width;
            if (W < 50) W = Theme.S(900);
            int exportW = Theme.S(140);
            statusLabel.SetBounds(Theme.S(16), Theme.S(17),
                Math.Max(Theme.S(100), W - exportW - Theme.S(44)), Theme.S(24));
            btnExport.SetBounds(W - exportW - Theme.S(16), Theme.S(10), exportW, Theme.S(38));
        }

        void LayoutRightButtons(Panel host, StyledButton apply, StyledButton cancel)
        {
            int W = host.ClientSize.Width;
            cancel.SetBounds(W - cancel.Width - Theme.S(16), Theme.S(12), cancel.Width, Theme.S(36));
            apply.SetBounds(cancel.Left - apply.Width - Theme.S(8), Theme.S(12), apply.Width, Theme.S(36));
        }

        StyledButton MakeBtn(string glyph, string text, int x, int w)
        {
            StyledButton b = new StyledButton();
            b.Glyph = glyph;
            b.Text = text;
            b.SetBounds(x, Theme.S(13), w, Theme.S(36));
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
            keyPanel.Visible = false;
            cropPanel.Visible = false;
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
            btnExport.Invalidate();
            tips.SetToolTip(btnExport, blocked
                ? "Видео больше 512px — сначала нажмите «Crop 1:1» и выберите зону стикера"
                : "Сделать стикер: WebM ≤256 КБ + обход лимита 3 сек");

            btnCrop.SetChecked(doc != null && doc.CropApplied);
            btnKey.SetChecked(doc != null && doc.State.Key.Enabled);

            if (!busy && !showingResult)
            {
                statusLabel.ForeColor = blocked ? Theme.Warn : Theme.TextMuted;
                statusLabel.Text = statusInfo +
                    (blocked ? "  •  Crop обязателен: видео больше 512px" : "");
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
                "{0:0.0} с  •  CUT: {1:0.0}–{2:0.0} с (длина {3:0.0} с, макс 6)",
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
            cropPanel.Visible = true;
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
            cropPanel.Visible = false;
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

            keyPanel.Visible = true;
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
            lbGain.Text = "Gain: " + slGain.Value;
            lbShrink.Text = "Shrink/Grow: " + slShrink.Value;
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
            keyPanel.Visible = false;
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
            statusLabel.Text = "Экспорт…";
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
