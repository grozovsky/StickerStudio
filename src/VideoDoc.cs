using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StickerStudio
{
    // Неразрушающее состояние правок; служит снапшотом для undo
    class EditState
    {
        public Rectangle CropRect = Rectangle.Empty; // в координатах ОРИГИНАЛА; Empty = кропа нет
        public double CutStart;                      // сек
        public double CutEnd;                        // сек
        public KeySettings Key = new KeySettings();
        public bool Fps30;                           // пересчитать в 30 fps (решение юзера на экспорте)

        public EditState Clone()
        {
            EditState s = new EditState();
            s.CropRect = CropRect;
            s.CutStart = CutStart;
            s.CutEnd = CutEnd;
            s.Key = Key.Clone();
            s.Fps30 = Fps30;
            return s;
        }
    }

    class VideoDoc : IDisposable
    {
        public const double MaxInputSeconds = 180;  // лимит длины входа
        public const double MaxCutSeconds = 6;
        public const double MinCutSeconds = 0.5;
        public const int StickerSide = 512;

        public string SourcePath;
        public ProbeInfo Info;
        public bool SourceHasAlpha;

        // превью
        public List<byte[]> Frames = new List<byte[]>(); // png (альфа) или jpg
        public double PreviewFps;
        public int PreviewW, PreviewH;

        public EditState State = new EditState();
        readonly Stack<EditState> undo = new Stack<EditState>();

        public bool CropRequired
        {
            get { return Info != null && (Info.Width > StickerSide || Info.Height > StickerSide); }
        }

        public bool CropApplied
        {
            get { return !State.CropRect.IsEmpty; }
        }

        public bool CanUndo { get { return undo.Count > 0; } }

        public void PushUndo()
        {
            undo.Push(State.Clone());
        }

        // для случаев, когда снапшот "до" сделан заранее (драг таймлайна)
        public void PushUndoSnapshot(EditState snapshot)
        {
            undo.Push(snapshot);
        }

        public bool Undo()
        {
            if (undo.Count == 0) return false;
            State = undo.Pop();
            return true;
        }

        public double CutDuration { get { return State.CutEnd - State.CutStart; } }

        // Загрузка: probe + извлечение превью-кадров во временную папку -> память.
        // progress(процент 0..100, текст)
        public string Load(string ffmpeg, string path, Action<int, string> progress)
        {
            SourcePath = path;
            Info = Ffmpeg.Probe(ffmpeg, path);
            if (!Info.Ok) return "Не удалось открыть видео: " + Info.Error;
            if (Info.Duration > MaxInputSeconds)
                return "Видео длиннее " + (int)MaxInputSeconds + " секунд. Для стикера загрузите ролик покороче.";

            SourceHasAlpha = Info.HasAlpha;

            State.CutStart = 0;
            State.CutEnd = Math.Min(Info.Duration, MaxCutSeconds);
            if (State.CutEnd - State.CutStart < MinCutSeconds)
                State.CutEnd = Math.Min(Info.Duration, State.CutStart + MinCutSeconds);

            // размер превью: длинная сторона <= 400, чётные
            int pw, ph;
            if (Info.Width >= Info.Height)
            {
                pw = Math.Min(400, Info.Width);
                ph = Math.Max(2, (int)Math.Round((double)Info.Height * pw / Info.Width));
            }
            else
            {
                ph = Math.Min(400, Info.Height);
                pw = Math.Max(2, (int)Math.Round((double)Info.Width * ph / Info.Height));
            }
            if (pw % 2 != 0) pw--;
            if (ph % 2 != 0) ph--;
            PreviewW = pw;
            PreviewH = ph;

            PreviewFps = (Info.Fps > 0 && Info.Fps <= 30) ? Info.Fps : 30.0;
            int expected = (int)Math.Ceiling(Info.Duration * PreviewFps) + 2;

            string tmpDir = Path.Combine(Path.GetTempPath(),
                "sst_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string ext = SourceHasAlpha ? "png" : "jpg";
                string quality = SourceHasAlpha ? "" : " -q:v 4";
                string args = "-y -hide_banner -loglevel error -i \"" + path + "\"" +
                    " -vf \"fps=" + Ffmpeg.Inv(PreviewFps) + ",scale=" + pw + ":" + ph + "\"" +
                    quality + " \"" + Path.Combine(tmpDir, "f%05d." + ext) + "\"";

                // прогресс по числу появившихся файлов
                bool done = false;
                System.Threading.Thread watcher = null;
                if (progress != null)
                {
                    watcher = new System.Threading.Thread(delegate()
                    {
                        while (!done)
                        {
                            try
                            {
                                int n = Directory.GetFiles(tmpDir).Length;
                                int pct = Math.Min(99, n * 100 / Math.Max(1, expected));
                                progress(pct, "Подготовка превью… " + pct + "%");
                            }
                            catch { }
                            System.Threading.Thread.Sleep(200);
                        }
                    });
                    watcher.IsBackground = true;
                    watcher.Start();
                }

                int code;
                string err = Ffmpeg.Run(ffmpeg, args, out code);
                done = true;
                if (watcher != null) watcher.Join(500);
                if (code != 0)
                    return "Не удалось прочитать видео: " + Ffmpeg.LastLine(err);

                string[] files = Directory.GetFiles(tmpDir, "f*." + ext)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                if (files.Length == 0)
                    return "Не удалось извлечь кадры из видео";

                Frames.Clear();
                foreach (string f in files)
                    Frames.Add(File.ReadAllBytes(f));

                if (progress != null) progress(100, "Готово");
                return null; // успех
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        public int FrameAt(double seconds)
        {
            int idx = (int)Math.Floor(seconds * PreviewFps);
            if (idx < 0) idx = 0;
            if (idx >= Frames.Count) idx = Frames.Count - 1;
            return idx;
        }

        public double TimeOfFrame(int idx)
        {
            return idx / PreviewFps;
        }

        // Декодированный кадр БЕЗ кия (для пипетки и как база для превью)
        public Bitmap DecodeFrame(int idx)
        {
            if (idx < 0 || idx >= Frames.Count) return null;
            using (MemoryStream ms = new MemoryStream(Frames[idx]))
            using (Image img = Image.FromStream(ms))
            {
                Bitmap b = new Bitmap(PreviewW, PreviewH,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                    g.DrawImage(img, 0, 0, PreviewW, PreviewH);
                return b;
            }
        }

        public void Dispose()
        {
            Frames.Clear();
        }
    }
}
