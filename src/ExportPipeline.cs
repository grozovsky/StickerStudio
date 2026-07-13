using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StickerStudio
{
    class ExportResult
    {
        public bool Ok;
        public string Error;
        public string OutputPath;
        public long Size;
        public bool AlphaInOutput;
        public bool FpsWarning;
    }

    static class ExportPipeline
    {
        // Полный экспорт: cut -> crop/scale -> (chroma key) -> VP9 2-pass <=256КБ -> hex-патч
        public static ExportResult Run(string ffmpeg, string sourcePath, ProbeInfo info,
            bool sourceHasAlpha, EditState state, string outputPath, Action<string> progress)
        {
            ExportResult res = new ExportResult();
            double dur = state.CutEnd - state.CutStart;
            if (dur < 0.1) { res.Error = "Слишком короткий отрезок"; return res; }

            bool keyed = state.Key != null && state.Key.Enabled;
            bool alphaOut = sourceHasAlpha || keyed;
            res.AlphaInOutput = alphaOut;
            res.FpsWarning = info.Fps > 31 && !state.Fps30;

            // юзер согласился «сделать как надо» — приводим к 30 fps;
            // иначе частота кадров исходника не трогается
            string fpsPrefix = state.Fps30 ? "fps=30," : "";

            // Геометрию делим на две части: crop до keying и scale после него.
            // Lanczos до удаления фона смешивал зелёный экран с краем объекта,
            // поэтому экспорт давал кайму, которой не было в live-preview.
            StickerFrameGeometry geometry = FrameGeometry.Create(info, state.CropRect);
            string preKeyFilter = geometry.PreKeyFilter;
            string postKeyFilter = geometry.PostKeyFilter;
            string scaleFilter = preKeyFilter.Length > 0
                ? preKeyFilter + "," + postKeyFilter
                : postKeyFilter;

            string cutArgs = " -ss " + Ffmpeg.Inv(state.CutStart) + " -t " + Ffmpeg.Inv(dur);

            string tmpDir = Path.Combine(Path.GetTempPath(),
                "sse_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string encodeInput;        // что подаём в 2-pass кодирование
                string encodeInputArgs;    // input-опции (framerate для секвенции)
                double fps = state.Fps30 ? 30 : (info.Fps > 0 ? info.Fps : 30);

                if (keyed)
                {
                    // 1) вырезанный/кропнутый кусок -> png-секвенция
                    if (progress != null) progress("Подготовка кадров…");
                    string seqDir = Path.Combine(tmpDir, "seq");
                    Directory.CreateDirectory(seqDir);
                    int c1;
                    string keyPrepFilter = fpsPrefix +
                        (preKeyFilter.Length > 0 ? preKeyFilter + "," : "") +
                        "format=rgba";
                    string e1 = Ffmpeg.Run(ffmpeg, "-y -hide_banner -loglevel error" +
                        " -i \"" + sourcePath + "\"" + cutArgs +
                        " -vf \"" + keyPrepFilter + "\"" +
                        " \"" + Path.Combine(seqDir, "f%05d.png") + "\"", out c1);
                    if (c1 != 0) { res.Error = "Ошибка обработки: " + Ffmpeg.LastLine(e1); return res; }

                    // 2) хромакей на каждом кадре — тем же кодом, что и превью
                    string[] frames = Directory.GetFiles(seqDir, "f*.png")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (frames.Length == 0) { res.Error = "Кадры не извлеклись"; return res; }
                    int done = 0;
                    foreach (string f in frames)
                    {
                        using (Bitmap bmp = LoadArgb(f))
                        {
                            ChromaKey.Apply(bmp, state.Key);
                            bmp.Save(f, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        done++;
                        if (progress != null && done % 20 == 0)
                            progress("Удаление фона… " + (done * 100 / frames.Length) + "%");
                    }

                    // Lanczos выполняем до VP9, затем расширяем foreground RGB под
                    // прозрачную кромку уже в конечных 512 px. Иначе yuva420p
                    // усредняет остаточный green в соседний непрозрачный пиксель.
                    string scaledDir = Path.Combine(tmpDir, "scaled");
                    Directory.CreateDirectory(scaledDir);
                    int c2;
                    string e2 = Ffmpeg.Run(ffmpeg,
                        "-y -hide_banner -loglevel error -framerate " + Ffmpeg.Inv(fps) +
                        " -i \"" + Path.Combine(seqDir, "f%05d.png") +
                        "\" -vf \"" + postKeyFilter + "\" \"" +
                        Path.Combine(scaledDir, "f%05d.png") + "\"", out c2);
                    if (c2 != 0)
                    {
                        res.Error = "Ошибка очистки кромки: " + Ffmpeg.LastLine(e2);
                        return res;
                    }

                    string[] scaledFrames = Directory.GetFiles(scaledDir, "f*.png")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (scaledFrames.Length == 0)
                    { res.Error = "Не удалось подготовить кромку"; return res; }
                    foreach (string f in scaledFrames)
                    {
                        using (Bitmap bmp = LoadArgb(f))
                        {
                            ChromaKey.ProtectTransparentColors(bmp, 3);
                            bmp.Save(f, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }

                    encodeInput = Path.Combine(scaledDir, "f%05d.png");
                    encodeInputArgs = "-framerate " + Ffmpeg.Inv(fps) + " -i ";
                }
                else
                {
                    encodeInput = sourcePath;
                    encodeInputArgs = "-i ";
                }

                // 3) итеративное 2-проходное кодирование под лимит размера
                string pixFmt = alphaOut ? "yuva420p" : "yuv420p";
                int kbps = (int)(Ffmpeg.SizeTarget * 8 / dur * 0.93 / 1000.0);
                if (kbps < 30) kbps = 30;

                string outTmp = Path.Combine(tmpDir, "out.webm");
                string bestTmp = Path.Combine(tmpDir, "best.webm");
                long bestSize = -1;

                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    string passLog = Path.Combine(tmpDir, "2p_" + attempt);
                    string vf = keyed ? "setsar=1" : fpsPrefix + scaleFilter;
                    string common =
                        " " + encodeInputArgs + "\"" + encodeInput + "\"" +
                        (keyed ? "" : cutArgs) +
                        " -an -sn -map_metadata -1" +
                        (keyed ? "" : " -map 0:v:0") +
                        " -c:v libvpx-vp9 -pix_fmt " + pixFmt +
                        " -b:v " + kbps + "k -minrate " + (kbps / 2) + "k -maxrate " + (kbps * 3 / 2) + "k" +
                        " -vf \"" + vf + "\"" +
                        " -deadline good -cpu-used 2 -row-mt 1 -auto-alt-ref 0" +
                        " -passlogfile \"" + passLog + "\"";

                    if (progress != null)
                        progress("Кодирование, попытка " + attempt + " (" + kbps + " кбит/с)…");
                    int code1;
                    string err1 = Ffmpeg.Run(ffmpeg, "-y -hide_banner -loglevel error" + common +
                        " -pass 1 -f null NUL", out code1);
                    if (code1 != 0) { res.Error = "Ошибка кодирования: " + Ffmpeg.LastLine(err1); return res; }

                    int code2;
                    string err2 = Ffmpeg.Run(ffmpeg, "-y -hide_banner -loglevel error" + common +
                        " -pass 2 \"" + outTmp + "\"", out code2);
                    if (code2 != 0 || !File.Exists(outTmp))
                    { res.Error = "Ошибка кодирования: " + Ffmpeg.LastLine(err2); return res; }

                    long size = new FileInfo(outTmp).Length;
                    if (size <= Ffmpeg.SizeLimit && size > bestSize)
                    {
                        File.Copy(outTmp, bestTmp, true);
                        bestSize = size;
                    }

                    if (size <= Ffmpeg.SizeLimit && size >= Ffmpeg.SizeLimit * 6 / 10) break;
                    if (size > Ffmpeg.SizeLimit)
                    {
                        kbps = (int)(kbps * (double)Ffmpeg.SizeTarget / size * 0.92);
                        if (kbps < 20) kbps = 20;
                    }
                    else
                    {
                        if (attempt >= 2) break;
                        kbps = (int)Math.Min(6000, kbps * (double)Ffmpeg.SizeTarget / Math.Max(size, 1) * 0.95);
                    }
                }

                if (bestSize < 0)
                {
                    res.Error = "Не удалось ужать в 256 КБ (слишком длинный/сложный ролик)";
                    return res;
                }

                // 4) hex-патч длительности
                byte[] data = File.ReadAllBytes(bestTmp);
                Patcher.PatchBytes(data);
                File.WriteAllBytes(outputPath, data);

                res.Ok = true;
                res.OutputPath = outputPath;
                res.Size = bestSize;
                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                return res;
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        static Bitmap LoadArgb(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream ms = new MemoryStream(bytes))
            using (Image img = Image.FromStream(ms))
            {
                Bitmap b = new Bitmap(img.Width, img.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                    g.DrawImage(img, 0, 0, img.Width, img.Height);
                return b;
            }
        }

    }
}
