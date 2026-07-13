using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace StickerStudio
{
    static class Theme
    {
        public static float UiScale = 1f;

        // Segoe UI Variable has calmer proportions and better Cyrillic rhythm
        // than the condensed display face used in the first redesign pass.
        public static readonly string DisplayFont = ResolveFont(
            "Segoe UI Variable Display Semib", "Segoe UI Semibold");
        public static readonly string BodyFont = ResolveFont(
            "Segoe UI Variable Text", "Segoe UI");
        public static readonly string BodySemiboldFont = ResolveFont(
            "Segoe UI Variable Text Semibold", "Segoe UI Semibold");

        // UX Live 2026: ink-black editing surfaces with the flame-orange brand
        // accent sampled from the bundled UX Live mark. Telegram blue stays
        // semantic and never competes with the product's primary action.
        public static readonly Color BackMain = Color.FromArgb(11, 11, 13);
        public static readonly Color BackPanel = Color.FromArgb(18, 18, 21);
        public static readonly Color BackHeader = Color.FromArgb(24, 24, 28);
        public static readonly Color BackFooter = Color.FromArgb(14, 14, 17);
        public static readonly Color Stage = Color.FromArgb(14, 14, 17);
        public static readonly Color Surface = Color.FromArgb(23, 23, 27);
        public static readonly Color SurfaceRaised = Color.FromArgb(29, 29, 34);
        public static readonly Color SurfaceSoft = Color.FromArgb(35, 35, 41);

        public static readonly Color Accent = Color.FromArgb(255, 62, 5);
        public static readonly Color AccentHover = Color.FromArgb(255, 88, 38);
        public static readonly Color AccentPressed = Color.FromArgb(218, 47, 0);
        public static readonly Color AccentSoft = Color.FromArgb(68, 29, 18);
        public static readonly Color Accent2 = Color.FromArgb(255, 126, 73);
        public static readonly Color Telegram = Color.FromArgb(51, 169, 242);

        public static readonly Color TextMain = Color.FromArgb(249, 248, 246);
        public static readonly Color TextSoft = Color.FromArgb(215, 212, 207);
        public static readonly Color TextMuted = Color.FromArgb(159, 157, 164);
        public static readonly Color BorderIdle = Color.FromArgb(51, 51, 58);
        public static readonly Color BorderHover = Color.FromArgb(83, 82, 91);
        public static readonly Color Ok = Color.FromArgb(99, 216, 158);
        public static readonly Color Warn = Color.FromArgb(247, 197, 95);
        public static readonly Color Err = Color.FromArgb(255, 103, 128);
        public static readonly Color Checker1 = Color.FromArgb(33, 33, 38);
        public static readonly Color Checker2 = Color.FromArgb(44, 44, 50);
        public static readonly Color BtnBase = Color.FromArgb(34, 34, 39);
        public static readonly Color BtnHover = Color.FromArgb(45, 45, 51);
        public static readonly Color BtnPressed = Color.FromArgb(27, 27, 31);

        public static int S(int v)
        {
            return (int)Math.Round(v * UiScale);
        }

        static string ResolveFont(string preferred, string fallback)
        {
            try
            {
                using (Font font = new Font(preferred, 9f))
                    return string.Equals(font.Name, preferred, StringComparison.OrdinalIgnoreCase)
                        ? preferred : fallback;
            }
            catch { return fallback; }
        }
    }

    // Hex-патч длительности: 44 89 (EBML Duration) -> значение 1.0 (обход лимита 3 сек)
    static class Patcher
    {
        static readonly byte[] Legacy = new byte[] { 0x84, 0x3F, 0x80, 0x00 };
        static readonly byte[] Float1 = new byte[] { 0x3F, 0x80, 0x00, 0x00 };
        static readonly byte[] Double1 = new byte[] { 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        public const int NotFound = -1;
        public const int Applied = 0;
        public const int AlreadyPatched = 1;

        public static int PatchBytes(byte[] data)
        {
            int idx = -1;
            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == 0x44 && data[i + 1] == 0x89) { idx = i; break; }
            }
            if (idx < 0 || idx + 6 > data.Length) return NotFound;

            byte sizeByte = data[idx + 2];

            if (sizeByte == 0x84)
            {
                if (StartsWith(data, idx + 3, Float1, 3)) return AlreadyPatched;
                if (idx + 3 + 4 > data.Length) return NotFound;
                Array.Copy(Float1, 0, data, idx + 3, 4);
                return Applied;
            }

            if (sizeByte == 0x88 && idx + 3 + 8 <= data.Length)
            {
                if (StartsWith(data, idx + 3, Double1, 4)) return AlreadyPatched;
                Array.Copy(Double1, 0, data, idx + 3, 8);
                return Applied;
            }

            if (StartsWith(data, idx + 2, Legacy, 4)) return AlreadyPatched;
            Array.Copy(Legacy, 0, data, idx + 2, 4);
            return Applied;
        }

        static bool StartsWith(byte[] data, int offset, byte[] pattern, int count)
        {
            if (offset + count > data.Length) return false;
            for (int i = 0; i < count; i++)
            {
                if (data[offset + i] != pattern[i]) return false;
            }
            return true;
        }
    }

    class ProbeInfo
    {
        public bool Ok;
        public string Error;
        public double Duration;
        public int Width;
        public int Height;
        public double Fps;
        public bool HasAlpha;
    }

    static class Ffmpeg
    {
        public const long SizeLimit = 262144;      // 256 КБ — лимит Telegram
        public const long SizeTarget = 250 * 1024;

        const string ResourceName = "ffmpeg.gz";

        public static volatile Process Current;

        public static string Find()
        {
            try
            {
                string local = Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath) ?? "", "ffmpeg.exe");
                if (File.Exists(local)) return local;
            }
            catch { }

            try
            {
                string extracted = ExtractedPath();
                if (File.Exists(extracted) && (!HasEmbedded() || StampValid()))
                    return extracted;
            }
            catch { }

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in pathEnv.Split(';'))
            {
                try
                {
                    if (d.Trim().Length == 0) continue;
                    string c = Path.Combine(d.Trim(), "ffmpeg.exe");
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return null;
        }

        public static bool HasEmbedded()
        {
            try
            {
                return Assembly.GetExecutingAssembly()
                    .GetManifestResourceNames().Contains(ResourceName);
            }
            catch { return false; }
        }

        public static string EnsureAvailable(Action<string> progress)
        {
            string found = Find();
            if (found != null) return found;
            if (!HasEmbedded()) return null;

            if (progress != null)
                progress("Первый запуск: распаковка встроенного ffmpeg…");
            try
            {
                string target = ExtractedPath();
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string tmp = target + ".tmp";
                using (Stream res = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(ResourceName))
                using (GZipStream gz = new GZipStream(res, CompressionMode.Decompress))
                using (FileStream fs = new FileStream(tmp, FileMode.Create))
                {
                    gz.CopyTo(fs);
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                File.WriteAllText(StampPath(),
                    EmbeddedLength().ToString(CultureInfo.InvariantCulture));
                return target;
            }
            catch { return null; }
        }

        static string ExtractedPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StickerStudio", "ffmpeg.exe");
        }

        static string StampPath()
        {
            return ExtractedPath() + ".stamp";
        }

        static long EmbeddedLength()
        {
            using (Stream res = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName))
                return res.Length;
        }

        static bool StampValid()
        {
            try
            {
                if (!File.Exists(StampPath())) return false;
                return File.ReadAllText(StampPath()).Trim() ==
                    EmbeddedLength().ToString(CultureInfo.InvariantCulture);
            }
            catch { return false; }
        }

        public static string Run(string ffmpeg, string args, out int exitCode)
        {
            ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            using (Process p = Process.Start(psi))
            {
                Current = p;
                p.OutputDataReceived += delegate { };
                p.BeginOutputReadLine();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                exitCode = p.ExitCode;
                Current = null;
                return err;
            }
        }

        public static ProbeInfo Probe(string ffmpeg, string input)
        {
            ProbeInfo info = new ProbeInfo();
            int code;
            string log = Run(ffmpeg, "-hide_banner -i \"" + input + "\"", out code);

            Match md = Regex.Match(log, @"Duration:\s+(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!md.Success)
            {
                info.Error = "не удалось определить длительность (файл повреждён?)";
                return info;
            }
            info.Duration = int.Parse(md.Groups[1].Value) * 3600
                + int.Parse(md.Groups[2].Value) * 60
                + double.Parse(md.Groups[3].Value, CultureInfo.InvariantCulture);

            Match mv = Regex.Match(log, @"Stream #\d+:\d+.*?: Video: (.+)");
            if (!mv.Success)
            {
                info.Error = "видеопоток не найден";
                return info;
            }
            string vline = mv.Groups[1].Value;

            Match mdim = Regex.Match(vline, @"[, ](\d{2,5})x(\d{2,5})[ ,\[]");
            if (!mdim.Success)
            {
                info.Error = "не удалось определить разрешение";
                return info;
            }
            info.Width = int.Parse(mdim.Groups[1].Value);
            info.Height = int.Parse(mdim.Groups[2].Value);

            Match mfps = Regex.Match(vline, @"(\d+(?:\.\d+)?)\s*fps");
            if (mfps.Success)
                info.Fps = double.Parse(mfps.Groups[1].Value, CultureInfo.InvariantCulture);

            string[] alphaFmts = { "yuva", "rgba", "argb", "bgra", "abgr", "gbrap", "ya8", "ya16" };
            foreach (string fmt in alphaFmts)
            {
                if (vline.Contains(fmt)) { info.HasAlpha = true; break; }
            }

            if (info.Duration <= 0.05)
            {
                info.Error = "нулевая длительность";
                return info;
            }
            info.Ok = true;
            return info;
        }

        public static string LastLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "ffmpeg завершился с ошибкой";
            string[] lines = s.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Trim().Length > 0) return lines[i].Trim();
            }
            return "ffmpeg завершился с ошибкой";
        }

        public static string Inv(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
