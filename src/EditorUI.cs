using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace StickerStudio
{
    sealed class ExactPreviewRequest
    {
        public long Revision;
        public string FfmpegPath;
        public string SourcePath;
        public ProbeInfo Info;
        public double Time;
        public Rectangle CropRect;
        public KeySettings Key;
        public bool DecodeVp9Alpha;
    }

    // Быстрые JPEG-кадры остаются резервом на время подготовки HQ-cache. Когда пользователь остановился,
    // этот worker строит один точный кадр тем же путём, что и export:
    // native crop -> chroma key -> Lanczos 512.
    sealed class ExactPreviewRenderer : IDisposable
    {
        readonly object sync = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        ExactPreviewRequest pending;
        System.Diagnostics.Process currentProcess;
        long cancellationGeneration;
        bool stopped;

        public event Action<long, Bitmap> Completed;

        public ExactPreviewRenderer()
        {
            worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Name = "StickerStudio exact preview";
            worker.Start();
        }

        public void Request(ExactPreviewRequest request)
        {
            if (request == null) return;
            lock (sync)
            {
                if (stopped) return;
                pending = request;
            }
            wake.Set();
        }

        public void CancelPending()
        {
            System.Diagnostics.Process process = null;
            lock (sync)
            {
                pending = null;
                cancellationGeneration++;
                process = currentProcess;
            }
            try
            {
                if (process != null && !process.HasExited) process.Kill();
            }
            catch { }
        }

        void WorkerLoop()
        {
            while (true)
            {
                wake.WaitOne();
                while (true)
                {
                    ExactPreviewRequest request;
                    long generation;
                    lock (sync)
                    {
                        if (stopped) return;
                        request = pending;
                        pending = null;
                        generation = cancellationGeneration;
                    }
                    if (request == null) break;

                    Bitmap bitmap = Render(request, generation);
                    if (bitmap == null)
                    {
                        bool retry;
                        lock (sync)
                        {
                            retry = !stopped && pending == null &&
                                generation == cancellationGeneration;
                        }
                        if (retry) bitmap = Render(request, generation);
                    }
                    if (bitmap != null)
                    {
                        Action<long, Bitmap> handler = Completed;
                        if (handler != null) handler(request.Revision, bitmap);
                        else bitmap.Dispose();
                    }
                }
            }
        }

        Bitmap Render(ExactPreviewRequest request, long generation)
        {
            if (request.Info == null || string.IsNullOrEmpty(request.FfmpegPath) ||
                string.IsNullOrEmpty(request.SourcePath)) return null;

            string tempDir = Path.Combine(Path.GetTempPath(),
                "ssp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                StickerFrameGeometry geometry = FrameGeometry.Create(
                    request.Info, request.CropRect);
                string nativePath = Path.Combine(tempDir, "native.png");
                string finalPath = Path.Combine(tempDir, "final.png");
                string prepFilter = (geometry.PreKeyFilter.Length > 0
                    ? geometry.PreKeyFilter + "," : "") + "format=rgba";

                int code;
                RunIsolated(request.FfmpegPath,
                    "-y -hide_banner -loglevel error " +
                    (request.DecodeVp9Alpha ? "-c:v libvpx-vp9 " : "") +
                    "-i \"" + request.SourcePath +
                    "\" -ss " + Ffmpeg.Inv(request.Time) +
                    " -frames:v 1 -vf \"" + prepFilter + "\" \"" + nativePath + "\"",
                    generation, out code);
                if (code != 0 || !File.Exists(nativePath)) return null;

                if (request.Key != null && request.Key.Enabled)
                {
                    using (Bitmap source = LoadArgb(nativePath))
                    {
                        ChromaKey.Apply(source, request.Key);
                        source.Save(nativePath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }

                RunIsolated(request.FfmpegPath,
                    "-y -hide_banner -loglevel error -i \"" + nativePath +
                    "\" -frames:v 1 -vf \"" + geometry.PostKeyFilter +
                    "\" \"" + finalPath + "\"", generation, out code);
                if (code != 0 || !File.Exists(finalPath)) return null;
                Bitmap final = LoadArgb(finalPath);
                if (request.Key != null && request.Key.Enabled)
                    ChromaKey.PrepareForVp9(final, 3);
                return final;
            }
            catch { return null; }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        string RunIsolated(string executable, string arguments, long generation,
            out int exitCode)
        {
            System.Diagnostics.ProcessStartInfo psi =
                new System.Diagnostics.ProcessStartInfo(executable, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            System.Diagnostics.Process process = new System.Diagnostics.Process();
            process.StartInfo = psi;
            try
            {
                // Start + publication are atomic relative to CancelPending().
                // A cancelled request can no longer start after Kill() missed it.
                lock (sync)
                {
                    if (stopped || generation != cancellationGeneration)
                    { exitCode = -1; return ""; }
                    process.Start();
                    currentProcess = process;
                }
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exitCode = process.ExitCode;
                return error;
            }
            catch
            {
                exitCode = -1;
                return "";
            }
            finally
            {
                lock (sync)
                {
                    if (object.ReferenceEquals(currentProcess, process))
                        currentProcess = null;
                }
                process.Dispose();
            }
        }

        static Bitmap LoadArgb(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream))
            {
                Bitmap bitmap = new Bitmap(image.Width, image.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    graphics.DrawImage(image, 0, 0, image.Width, image.Height);
                return bitmap;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (stopped) return;
                stopped = true;
                pending = null;
            }
            CancelPending();
            wake.Set();
            try { worker.Join(1000); } catch { }
            wake.Dispose();
        }
    }

    sealed class PlaybackPreviewRequest
    {
        public long Revision;
        public string FfmpegPath;
        public string SourcePath;
        public ProbeInfo Info;
        public double Start;
        public double End;
        public double Fps;
        public Rectangle CropRect;
        public KeySettings Key;
    }

    // Кадры лежат единым BGRA-файлом на диске: 6 секунд при 30 fps занимают
    // до 180 МБ на диске, но в памяти остаются только один кадр и read-buffer.
    sealed class PlaybackPreviewCache : IDisposable
    {
        readonly object sync = new object();
        readonly string directory;
        readonly string rawPath;
        FileStream lease;
        FileStream stream;
        byte[] buffer;
        bool disposed;

        public long Revision;
        public double Start;
        public double End;
        public double Fps;
        public int Width;
        public int Height;
        public int FrameCount;

        public PlaybackPreviewCache(string dir, string path, FileStream leaseStream)
        {
            directory = dir;
            rawPath = path;
            lease = leaseStream;
        }

        public int FrameIndexAt(double time)
        {
            if (FrameCount <= 0 || Fps <= 0) return -1;
            int index = (int)Math.Floor((time - Start) * Fps + 0.0001);
            return Math.Max(0, Math.Min(FrameCount - 1, index));
        }

        public Bitmap DecodeFrame(int index)
        {
            if (index < 0 || index >= FrameCount) return null;
            int frameBytes = checked(Width * Height * 4);
            lock (sync)
            {
                if (disposed) return null;
                if (stream == null)
                    stream = new FileStream(rawPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, frameBytes, FileOptions.RandomAccess);
                if (buffer == null || buffer.Length != frameBytes)
                    buffer = new byte[frameBytes];

                stream.Position = (long)index * frameBytes;
                int read = 0;
                while (read < frameBytes)
                {
                    int n = stream.Read(buffer, read, frameBytes - read);
                    if (n <= 0) return null;
                    read += n;
                }

                Bitmap bitmap = new Bitmap(Width, Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
                    new Rectangle(0, 0, Width, Height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int rowBytes = Width * 4;
                    if (data.Stride == rowBytes)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(buffer, 0,
                            data.Scan0, frameBytes);
                    }
                    else
                    {
                        for (int y = 0; y < Height; y++)
                        {
                            IntPtr row = new IntPtr(data.Scan0.ToInt64() +
                                (long)y * data.Stride);
                            System.Runtime.InteropServices.Marshal.Copy(buffer,
                                y * rowBytes, row, rowBytes);
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }
                return bitmap;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (stream != null) stream.Dispose();
                if (lease != null) lease.Dispose();
                stream = null;
                lease = null;
                buffer = null;
            }
            PlaybackPreviewRenderer.DeleteCacheDirectory(directory);
        }
    }

    // Чтение 1 МБ BGRA-кадра и создание Bitmap выполняются вне UI-потока.
    // Новые запросы вытесняют старые, поэтому decoder не накапливает очередь.
    sealed class PlaybackFrameDecoder : IDisposable
    {
        readonly object sync = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        PlaybackPreviewCache pendingCache;
        long pendingRevision;
        int pendingIndex = -1;
        long generation;
        bool stopped;

        public event Action<long, int, Bitmap> Completed;
        public event Action<long, int> Failed;

        public PlaybackFrameDecoder()
        {
            worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Name = "StickerStudio playback decoder";
            worker.Start();
        }

        public void Request(PlaybackPreviewCache cache, long revision, int index)
        {
            if (cache == null || index < 0) return;
            lock (sync)
            {
                if (stopped) return;
                generation++;
                pendingCache = cache;
                pendingRevision = revision;
                pendingIndex = index;
            }
            wake.Set();
        }

        public void CancelPending()
        {
            lock (sync)
            {
                generation++;
                pendingCache = null;
                pendingIndex = -1;
            }
        }

        void WorkerLoop()
        {
            while (true)
            {
                wake.WaitOne();
                while (true)
                {
                    PlaybackPreviewCache cache;
                    long revision;
                    long requestGeneration;
                    int index;
                    lock (sync)
                    {
                        if (stopped) return;
                        cache = pendingCache;
                        revision = pendingRevision;
                        index = pendingIndex;
                        requestGeneration = generation;
                        pendingCache = null;
                        pendingIndex = -1;
                    }
                    if (cache == null || index < 0) break;

                    Bitmap bitmap = null;
                    try { bitmap = cache.DecodeFrame(index); }
                    catch { }
                    lock (sync)
                    {
                        if (stopped || requestGeneration != generation)
                        {
                            if (bitmap != null) bitmap.Dispose();
                            continue;
                        }
                    }
                    if (bitmap != null)
                    {
                        Action<long, int, Bitmap> handler = Completed;
                        if (handler != null) handler(revision, index, bitmap);
                        else bitmap.Dispose();
                    }
                    else
                    {
                        Action<long, int> handler = Failed;
                        if (handler != null) handler(revision, index);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (stopped) return;
                stopped = true;
                pendingCache = null;
            }
            wake.Set();
            bool joined = false;
            try { joined = worker.Join(1000); } catch { }
            if (joined) wake.Dispose();
        }
    }

    // Фоновый producer строит выбранные пользователем 0.5–6 секунд тем же
    // порядком операций, что экспорт: native crop -> C# key -> Lanczos -> BGRA.
    sealed class PlaybackPreviewRenderer : IDisposable
    {
        readonly object sync = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        PlaybackPreviewRequest pending;
        System.Diagnostics.Process currentProcess;
        long cancellationGeneration;
        bool stopped;

        public event Action<long, PlaybackPreviewCache> Completed;
        public event Action<long, string> Failed;

        public PlaybackPreviewRenderer()
        {
            CleanupOldCaches();
            worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Name = "StickerStudio HQ playback";
            worker.Start();
        }

        public void Request(PlaybackPreviewRequest request)
        {
            if (request == null) return;
            lock (sync)
            {
                if (stopped) return;
                pending = request;
            }
            wake.Set();
        }

        public void CancelPending()
        {
            System.Diagnostics.Process process;
            lock (sync)
            {
                pending = null;
                cancellationGeneration++;
                process = currentProcess;
            }
            try
            {
                if (process != null && !process.HasExited) process.Kill();
            }
            catch { }
        }

        void WorkerLoop()
        {
            while (true)
            {
                wake.WaitOne();
                while (true)
                {
                    PlaybackPreviewRequest request;
                    long generation;
                    lock (sync)
                    {
                        if (stopped) return;
                        request = pending;
                        pending = null;
                        generation = cancellationGeneration;
                    }
                    if (request == null) break;

                    string error;
                    PlaybackPreviewCache cache = Build(request, generation, out error);
                    bool current;
                    lock (sync)
                    {
                        current = !stopped && generation == cancellationGeneration;
                    }
                    if (!current)
                    {
                        if (cache != null) cache.Dispose();
                        continue;
                    }

                    if (cache != null)
                    {
                        Action<long, PlaybackPreviewCache> handler = Completed;
                        if (handler != null) handler(request.Revision, cache);
                        else cache.Dispose();
                    }
                    else
                    {
                        Action<long, string> handler = Failed;
                        if (handler != null) handler(request.Revision, error);
                    }
                }
            }
        }

        PlaybackPreviewCache Build(PlaybackPreviewRequest request, long generation,
            out string error)
        {
            error = "Не удалось подготовить качественное воспроизведение";
            if (request.Info == null || request.End - request.Start < 0.05 ||
                string.IsNullOrEmpty(request.FfmpegPath) ||
                string.IsNullOrEmpty(request.SourcePath)) return null;

            string tempDir = Path.Combine(Path.GetTempPath(),
                "ssplay_" + Guid.NewGuid().ToString("N"));
            FileStream lease = null;
            bool keep = false;
            try
            {
                Directory.CreateDirectory(tempDir);
                lease = new FileStream(Path.Combine(tempDir, "active.lock"),
                    FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                StickerFrameGeometry geometry = FrameGeometry.Create(
                    request.Info, request.CropRect);
                string rawPath = Path.Combine(tempDir, "frames.bgra");
                string fps = Ffmpeg.Inv(request.Fps);
                string cut = " -ss " + Ffmpeg.Inv(request.Start) +
                    " -t " + Ffmpeg.Inv(request.End - request.Start);
                int code;
                string log;

                if (request.Key != null && request.Key.Enabled)
                {
                    string seqDir = Path.Combine(tempDir, "native");
                    Directory.CreateDirectory(seqDir);
                    string prep = "fps=" + fps + "," +
                        (geometry.PreKeyFilter.Length > 0
                            ? geometry.PreKeyFilter + "," : "") + "format=rgba";
                    log = RunIsolated(request.FfmpegPath,
                        "-y -hide_banner -loglevel error -i \"" + request.SourcePath +
                        "\"" + cut + " -vf \"" + prep + "\" \"" +
                        Path.Combine(seqDir, "f%05d.png") + "\"", generation, out code);
                    if (code != 0)
                    {
                        error = "Подготовка кадров: " + Ffmpeg.LastLine(log);
                        return null;
                    }

                    string[] frames = Directory.GetFiles(seqDir, "f*.png")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (frames.Length == 0) return null;
                    foreach (string frame in frames)
                    {
                        if (IsCancelled(generation)) return null;
                        using (Bitmap bitmap = LoadArgb(frame))
                        {
                            ChromaKey.Apply(bitmap, request.Key);
                            bitmap.Save(frame, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }

                    log = RunIsolated(request.FfmpegPath,
                        "-y -hide_banner -loglevel error -framerate " + fps +
                        " -i \"" + Path.Combine(seqDir, "f%05d.png") +
                        "\" -vf \"" + geometry.PostKeyFilter +
                        ",format=bgra\" -an -sn -f rawvideo \"" + rawPath + "\"",
                        generation, out code);
                    DeleteCacheDirectory(seqDir);
                }
                else
                {
                    string filters = "fps=" + fps + "," +
                        (geometry.PreKeyFilter.Length > 0
                            ? geometry.PreKeyFilter + "," : "") +
                        geometry.PostKeyFilter + ",format=bgra";
                    log = RunIsolated(request.FfmpegPath,
                        "-y -hide_banner -loglevel error -i \"" + request.SourcePath +
                        "\"" + cut + " -vf \"" + filters +
                        "\" -an -sn -f rawvideo \"" + rawPath + "\"",
                        generation, out code);
                }

                if (code != 0 || !File.Exists(rawPath))
                {
                    error = "Кэш воспроизведения: " + Ffmpeg.LastLine(log);
                    return null;
                }

                if (request.Key != null && request.Key.Enabled &&
                    !ProtectRawFrames(rawPath, geometry.OutputSize.Width,
                        geometry.OutputSize.Height, generation))
                    return null;

                int frameBytes = checked(geometry.OutputSize.Width *
                    geometry.OutputSize.Height * 4);
                long length = new FileInfo(rawPath).Length;
                int count = (int)(length / frameBytes);
                if (count <= 0) return null;

                PlaybackPreviewCache cache = new PlaybackPreviewCache(tempDir, rawPath, lease);
                lease = null;
                cache.Revision = request.Revision;
                cache.Start = request.Start;
                cache.End = request.End;
                cache.Fps = request.Fps;
                cache.Width = geometry.OutputSize.Width;
                cache.Height = geometry.OutputSize.Height;
                cache.FrameCount = count;
                keep = true;
                return cache;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                if (lease != null) lease.Dispose();
                if (!keep)
                    DeleteCacheDirectory(tempDir);
            }
        }

        static int cleanupStarted;

        static void CleanupOldCaches()
        {
            if (Interlocked.Exchange(ref cleanupStarted, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string temp = Path.GetTempPath();
                    foreach (string dir in Directory.GetDirectories(temp, "ssplay_*"))
                    {
                        try
                        {
                            if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-2))
                            {
                                string lockPath = Path.Combine(dir, "active.lock");
                                using (FileStream lease = new FileStream(lockPath,
                                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                                { }
                                DeleteCacheDirectory(dir);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }

        public static void DeleteCacheDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                return;
            }
            catch { }

            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Thread.Sleep(250 * (attempt + 1));
                    try
                    {
                        if (Directory.Exists(directory)) Directory.Delete(directory, true);
                        return;
                    }
                    catch { }
                }
            });
        }

        bool IsCancelled(long generation)
        {
            lock (sync)
                return stopped || generation != cancellationGeneration;
        }

        bool ProtectRawFrames(string path, int width, int height, long generation)
        {
            int frameBytes = checked(width * height * 4);
            byte[] frame = new byte[frameBytes];
            using (FileStream stream = new FileStream(path, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None, frameBytes))
            {
                long frames = stream.Length / frameBytes;
                for (long index = 0; index < frames; index++)
                {
                    if (IsCancelled(generation)) return false;
                    stream.Position = index * frameBytes;
                    int read = 0;
                    while (read < frameBytes)
                    {
                        int n = stream.Read(frame, read, frameBytes - read);
                        if (n <= 0) return false;
                        read += n;
                    }
                    ChromaKey.PrepareForVp9(frame, width, height,
                        width * 4, 3);
                    stream.Position = index * frameBytes;
                    stream.Write(frame, 0, frameBytes);
                }
            }
            return true;
        }

        string RunIsolated(string executable, string arguments, long generation,
            out int exitCode)
        {
            System.Diagnostics.ProcessStartInfo psi =
                new System.Diagnostics.ProcessStartInfo(executable, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            System.Diagnostics.Process process = new System.Diagnostics.Process();
            process.StartInfo = psi;
            try
            {
                lock (sync)
                {
                    if (stopped || generation != cancellationGeneration)
                    { exitCode = -1; return ""; }
                    process.Start();
                    currentProcess = process;
                }
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exitCode = process.ExitCode;
                return error;
            }
            catch
            {
                exitCode = -1;
                return "";
            }
            finally
            {
                lock (sync)
                {
                    if (object.ReferenceEquals(currentProcess, process))
                        currentProcess = null;
                }
                process.Dispose();
            }
        }

        static Bitmap LoadArgb(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream))
            {
                Bitmap bitmap = new Bitmap(image.Width, image.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    graphics.DrawImage(image, 0, 0, image.Width, image.Height);
                return bitmap;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (stopped) return;
                stopped = true;
                pending = null;
            }
            CancelPending();
            wake.Set();
            bool joined = false;
            try { joined = worker.Join(1500); } catch { }
            if (joined) wake.Dispose();
        }
    }

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
        Bitmap exactBmp;
        Bitmap playbackBmp;
        bool processing;
        string processingTitle = "Готовим чёткое воспроизведение";
        string processingDetail = "Подготавливаем кадры 512 × 512";
        int processingPhase;
        System.Windows.Forms.Timer processingTimer;

        // drag
        int dragCorner = -1;      // 0..3 = ручки; 4 = перемещение
        PointF dragAnchor;        // противоположный угол при ресайзе
        PointF dragOffset;

        const float MinSel = 32f;

        public PreviewControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            processingTimer = new System.Windows.Forms.Timer();
            processingTimer.Interval = 70;
            processingTimer.Tick += delegate
            {
                processingPhase = (processingPhase + 14) % 360;
                if (processing) Invalidate();
            };
        }

        public void SetProcessing(bool value, string title, string detail)
        {
            processing = value;
            if (!string.IsNullOrEmpty(title)) processingTitle = title;
            if (!string.IsNullOrEmpty(detail)) processingDetail = detail;
            if (processing)
            {
                processingPhase = 0;
                processingTimer.Start();
                AccessibleDescription = processingTitle + ". " + processingDetail;
            }
            else
            {
                processingTimer.Stop();
                AccessibleDescription = null;
            }
            Invalidate();
        }

        public void SetFrame(int idx)
        {
            if (idx != frameIdx) { frameIdx = idx; Invalidate(); }
        }

        public int CurrentFrame { get { return frameIdx; } }

        public void SetExactBitmap(Bitmap bitmap)
        {
            if (object.ReferenceEquals(exactBmp, bitmap)) return;
            if (exactBmp != null) exactBmp.Dispose();
            exactBmp = bitmap;
            Invalidate();
        }

        public void ClearExactBitmap()
        {
            if (exactBmp == null) return;
            exactBmp.Dispose();
            exactBmp = null;
            Invalidate();
        }

        public void SetPlaybackBitmap(Bitmap bitmap)
        {
            if (object.ReferenceEquals(playbackBmp, bitmap)) return;
            if (playbackBmp != null) playbackBmp.Dispose();
            playbackBmp = bitmap;
            Invalidate();
        }

        public void ClearPlaybackBitmap()
        {
            if (playbackBmp == null) return;
            playbackBmp.Dispose();
            playbackBmp = null;
            Invalidate();
        }

        public void BumpKeyVersion()
        {
            keyVersion++;
            Invalidate();
        }

        public void ResetCaches()
        {
            baseIdx = -1;
            keyedIdx = -1;
            ClearExactBitmap();
            ClearPlaybackBitmap();
            Invalidate();
        }

        Size ContentSize()
        {
            if (Doc == null) return new Size(4, 3);
            if (!CropMode && exactBmp != null) return exactBmp.Size;
            if (!CropMode && playbackBmp != null) return playbackBmp.Size;
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
            bool useExact = !CropMode && exactBmp != null;
            bool usePlayback = !useExact && !CropMode && playbackBmp != null;
            Bitmap bmp = useExact ? exactBmp : (usePlayback ? playbackBmp : CurrentBitmap());
            if (bmp == null) return;

            RectangleF sel = CropMode ? ImageToScreen(CropSel) : RectangleF.Empty;
            using (GraphicsPath frame = StyledButton.Rounded(r, Theme.S(10)))
            {
                // Изображение, прозрачная шахматка и crop-overlay используют один
                // контур. Обводка больше не маскирует квадратные углы видео.
                GraphicsState imageState = g.Save();
                g.SetClip(frame, CombineMode.Intersect);

                int cell = Theme.S(10);
                using (SolidBrush b1 = new SolidBrush(Theme.Checker1))
                using (SolidBrush b2 = new SolidBrush(Theme.Checker2))
                {
                    g.FillRectangle(b1, r);
                    for (int yy = r.Y, ry = 0; yy < r.Bottom; yy += cell, ry++)
                    {
                        for (int xx = r.X + ((ry % 2) * cell); xx < r.Right; xx += cell * 2)
                            g.FillRectangle(b2, xx, yy, cell, cell);
                    }
                }

                g.InterpolationMode = useExact || usePlayback
                    ? InterpolationMode.HighQualityBicubic
                    : InterpolationMode.Bilinear;
                if (!useExact && !usePlayback && !CropMode && !AppliedCropPreview.IsEmpty)
                    g.DrawImage(bmp, r, AppliedCropPreview, GraphicsUnit.Pixel);
                else
                    g.DrawImage(bmp, r);

                if (CropMode)
                {
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
                }

                g.Restore(imageState);
                using (Pen fp = new Pen(Color.FromArgb(72, Color.White), 1f))
                    g.DrawPath(fp, frame);

                if (!CropMode)
                {
                    if (processing) DrawProcessingOverlay(g, r);
                    return;
                }

                // Ручки остаются поверх клипа, чтобы их не обрезало у края кадра.
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

        void DrawProcessingOverlay(Graphics g, Rectangle imageRect)
        {
            Theme.PrepareText(g);
            int sidePad = Theme.S(18);
            int width = Math.Max(1, Math.Min(Theme.S(360),
                imageRect.Width - sidePad * 2));
            int height = Theme.S(68);
            Rectangle panel = new Rectangle(
                imageRect.X + (imageRect.Width - width) / 2,
                imageRect.Y + (imageRect.Height - height) / 2,
                width, height);

            using (GraphicsPath path = StyledButton.Rounded(panel, Theme.S(12)))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(242, Theme.SurfaceRaised)))
            using (Pen border = new Pen(Color.FromArgb(90, Theme.BorderHover), 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            int spinnerSide = Theme.S(24);
            Rectangle spinner = new Rectangle(panel.X + Theme.S(18),
                panel.Y + (panel.Height - spinnerSide) / 2, spinnerSide, spinnerSide);
            using (Pen track = new Pen(Color.FromArgb(52, Theme.TextSoft), Theme.S(2)))
            using (Pen arc = new Pen(Theme.Accent2, Theme.S(2)))
            {
                track.StartCap = track.EndCap = LineCap.Round;
                arc.StartCap = arc.EndCap = LineCap.Round;
                g.DrawEllipse(track, spinner);
                g.DrawArc(arc, spinner, processingPhase, 105);
            }

            int textX = spinner.Right + Theme.S(13);
            int textW = Math.Max(1, panel.Right - Theme.S(14) - textX);
            using (Font titleFont = new Font(Theme.BodySemiboldFont, 9.25f))
            using (Font detailFont = new Font(Theme.BodyFont, 8.25f))
            using (SolidBrush titleBrush = new SolidBrush(Theme.TextMain))
            using (SolidBrush detailBrush = new SolidBrush(Theme.TextMuted))
            using (StringFormat format = new StringFormat())
            {
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(processingTitle, titleFont, titleBrush,
                    new RectangleF(textX, panel.Y + Theme.S(13), textW, Theme.S(22)), format);
                g.DrawString(processingDetail, detailFont, detailBrush,
                    new RectangleF(textX, panel.Y + Theme.S(36), textW, Theme.S(20)), format);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (processingTimer != null)
                {
                    processingTimer.Stop();
                    processingTimer.Dispose();
                    processingTimer = null;
                }
                if (baseBmp != null) baseBmp.Dispose();
                if (keyedBmp != null) keyedBmp.Dispose();
                if (exactBmp != null) exactBmp.Dispose();
                if (playbackBmp != null) playbackBmp.Dispose();
                baseBmp = keyedBmp = exactBmp = playbackBmp = null;
            }
            base.Dispose(disposing);
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
