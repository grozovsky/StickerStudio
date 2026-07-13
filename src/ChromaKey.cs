using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StickerStudio
{
    class KeySettings
    {
        public bool Enabled;
        public Color ScreenColor = Color.FromArgb(0, 255, 0); // зелёный по умолчанию
        public int Gain = 100;        // 0..200
        public int ShrinkGrow = 0;    // -100..+100

        public KeySettings Clone()
        {
            KeySettings k = new KeySettings();
            k.Enabled = Enabled;
            k.ScreenColor = ScreenColor;
            k.Gain = Gain;
            k.ShrinkGrow = ShrinkGrow;
            return k;
        }
    }

    // Направленный хромакей (в духе Keylight):
    //  - матовость = проекция хромы пикселя на НАПРАВЛЕНИЕ ключевого оттенка
    //    минус штраф за отклонение оттенка (перпендикуляр). Передний план
    //    другого оттенка выживает, даже если "близок" по грубому расстоянию;
    //  - smoothstep-кривая вместо линейной — мягкие края;
    //  - despill пропорционален "ключевости" пикселя + компенсация яркости,
    //    чтобы края не серели и не темнели;
    //  - морфология (shrink/grow) и финальное 3x3-перо маски.
    // Один и тот же код для превью и экспорта — WYSIWYG.
    static class ChromaKey
    {
        public static void Apply(Bitmap bmp, KeySettings k)
        {
            if (bmp == null || k == null || !k.Enabled) return;
            int w = bmp.Width, h = bmp.Height;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int bytes = Math.Abs(bd.Stride) * h;
            byte[] px = new byte[bytes];
            Marshal.Copy(bd.Scan0, px, 0, bytes);

            ApplyToBgra(px, w, h, bd.Stride, k);

            Marshal.Copy(px, 0, bd.Scan0, bytes);
            bmp.UnlockBits(bd);
        }

        public static void ApplyToBgra(byte[] px, int w, int h, int stride, KeySettings k)
        {
            int count = w * h;
            double keyR = k.ScreenColor.R / 255.0;
            double keyG = k.ScreenColor.G / 255.0;
            double keyB = k.ScreenColor.B / 255.0;
            double keyU = -0.100644 * keyR - 0.338572 * keyG + 0.439216 * keyB + 0.501961;
            double keyV = 0.439216 * keyR - 0.398942 * keyG - 0.040274 * keyB + 0.501961;

            // OBS использует similarity=0.4, smoothness=0.08 и spill=0.1.
            // Текущий Gain сохраняем как один понятный контрол допуска.
            double similarity = 0.18 + Math.Max(0, Math.Min(200, k.Gain)) * 0.0021;
            double smoothness = 0.085;
            double spillRange = 0.11;

            double[] distance = new double[count];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    double b = px[i] / 255.0;
                    double g = px[i + 1] / 255.0;
                    double r = px[i + 2] / 255.0;
                    double u = -0.100644 * r - 0.338572 * g + 0.439216 * b + 0.501961;
                    double v = 0.439216 * r - 0.398942 * g - 0.040274 * b + 0.501961;
                    double du = u - keyU, dv = v - keyV;
                    distance[y * w + x] = Math.Sqrt(du * du + dv * dv);
                }
            }

            // CPU-аналог box-filter из OBS shader: центр + четыре соседа.
            // Это стабилизирует matte на шумном H.264 и не создаёт рваную лесенку.
            double[] filtered = new double[count];
            for (int y = 0; y < h; y++)
            {
                int up = Math.Max(0, y - 1), down = Math.Min(h - 1, y + 1);
                for (int x = 0; x < w; x++)
                {
                    int left = Math.Max(0, x - 1), right = Math.Min(w - 1, x + 1);
                    int idx = y * w + x;
                    filtered[idx] = (distance[idx] + 2.0 * (
                        distance[y * w + left] + distance[y * w + right] +
                        distance[up * w + x] + distance[down * w + x])) / 9.0;
                }
            }

            byte[] alpha = new byte[count];
            double[] spillKeep = new double[count];
            for (int idx = 0; idx < count; idx++)
            {
                // Соседний фильтр не должен съедать тонкую белую проволоку или блик:
                // если сам центральный пиксель явно далёк от screen color, сохраняем его.
                double effectiveDistance = filtered[idx];
                if (distance[idx] >= similarity + smoothness)
                    effectiveDistance = Math.Max(effectiveDistance, distance[idx]);
                double baseMask = effectiveDistance - similarity;
                double matte = Math.Pow(Saturate(baseMask / smoothness), 1.5);
                alpha[idx] = (byte)Math.Round(matte * 255.0);
                spillKeep[idx] = Math.Pow(Saturate(baseMask / spillRange), 1.5);
            }

            // shrink/grow: min/max-фильтр 3x3, до 3 итераций
            int iters = Math.Min(3, Math.Abs(k.ShrinkGrow) / 34 + (Math.Abs(k.ShrinkGrow) > 0 ? 1 : 0));
            if (iters > 0)
            {
                bool grow = k.ShrinkGrow > 0;
                byte[] tmp = new byte[w * h];
                for (int it = 0; it < iters; it++)
                {
                    MinMax3x3(alpha, tmp, w, h, grow);
                    byte[] sw = alpha; alpha = tmp; tmp = sw;
                }
            }

            // перо: одно 3x3-сглаживание маски убирает "лесенку" на краях
            byte[] unblurred = alpha;
            byte[] blurred = new byte[w * h];
            Box3x3(alpha, blurred, w, h);
            alpha = new byte[w * h];
            for (int idx = 0; idx < alpha.Length; idx++)
            {
                // Сохраняем уверенный foreground, размываем только переход и
                // прозрачную сторону края. Так тонкие детали не превращаются в 1/3 alpha.
                alpha[idx] = unblurred[idx] >= 240 ? unblurred[idx] : blurred[idx];
            }

            // OBS-style despill: загрязнённые ключом цвета мягко идут к своей яркости,
            // а не просто теряют зелёный канал и не дают серо-чёрную кромку.
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    int idx = y * w + x;
                    byte a = alpha[idx];
                    int srcA = px[i + 3];

                    double keep = spillKeep[idx];
                    double b = px[i], g = px[i + 1], r = px[i + 2];
                    double luma = r * 0.2126 + g * 0.7152 + b * 0.0722;
                    px[i] = ClampB((int)Math.Round(luma * (1.0 - keep) + b * keep));
                    px[i + 1] = ClampB((int)Math.Round(luma * (1.0 - keep) + g * keep));
                    px[i + 2] = ClampB((int)Math.Round(luma * (1.0 - keep) + r * keep));

                    px[i + 3] = (byte)(a * srcA / 255);
                }
            }

            // Лёгкая защита до ресайза. Основной радиус 3 px применяется уже
            // на конечных 512x512 непосредственно перед VP9.
            ProtectTransparentColors(px, w, h, stride, 1);
        }

        public static void ProtectTransparentColors(Bitmap bitmap, int radius)
        {
            if (bitmap == null || radius <= 0) return;
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int bytes = Math.Abs(data.Stride) * bitmap.Height;
            byte[] pixels = new byte[bytes];
            Marshal.Copy(data.Scan0, pixels, 0, bytes);
            ProtectTransparentColors(pixels, bitmap.Width, bitmap.Height, data.Stride, radius);
            Marshal.Copy(pixels, 0, data.Scan0, bytes);
            bitmap.UnlockBits(data);
        }

        // VP9 yuva420p усредняет цвет 2x2 без знания alpha. Поэтому RGB прозрачной
        // стороны кромки должен продолжать foreground, иначе зелёный screen снова
        // протечёт в непрозрачный пиксель при chroma subsampling.
        public static void ProtectTransparentColors(byte[] px, int w, int h,
            int stride, int radius)
        {
            if (px == null || radius <= 0 || w <= 0 || h <= 0) return;
            int count = w * h;
            bool[] filled = new bool[count];
            byte[] blue = new byte[count], green = new byte[count], red = new byte[count];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4, idx = y * w + x;
                    blue[idx] = px[i]; green[idx] = px[i + 1]; red[idx] = px[i + 2];
                    filled[idx] = px[i + 3] >= 192;
                }
            }

            for (int pass = 0; pass < radius; pass++)
            {
                bool[] add = new bool[count];
                byte[] nb = (byte[])blue.Clone();
                byte[] ng = (byte[])green.Clone();
                byte[] nr = (byte[])red.Clone();
                for (int y = 0; y < h; y++)
                {
                    int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (filled[idx]) continue;
                        int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1);
                        int sb = 0, sg = 0, sr = 0, n = 0;
                        for (int yy = y0; yy <= y1; yy++)
                            for (int xx = x0; xx <= x1; xx++)
                            {
                                int near = yy * w + xx;
                                if (!filled[near]) continue;
                                sb += blue[near]; sg += green[near]; sr += red[near]; n++;
                            }
                        if (n == 0) continue;
                        nb[idx] = (byte)(sb / n); ng[idx] = (byte)(sg / n); nr[idx] = (byte)(sr / n);
                        add[idx] = true;
                    }
                }
                blue = nb; green = ng; red = nr;
                bool any = false;
                for (int i = 0; i < count; i++)
                    if (add[i]) { filled[i] = true; any = true; }
                if (!any) break;
            }

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4, idx = y * w + x;
                    if (filled[idx] && px[i + 3] < 192)
                    {
                        px[i] = blue[idx]; px[i + 1] = green[idx]; px[i + 2] = red[idx];
                    }
                    else if (!filled[idx] && px[i + 3] <= 2)
                    {
                        px[i] = px[i + 1] = px[i + 2] = 0;
                    }
                }
            }
        }

        static double Saturate(double value)
        {
            return value < 0 ? 0 : (value > 1 ? 1 : value);
        }

        static byte ClampB(int v)
        {
            return (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
        }

        static void MinMax3x3(byte[] src, byte[] dst, int w, int h, bool max)
        {
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1);
                    byte m = src[y * w + x];
                    for (int yy = y0; yy <= y1; yy++)
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            byte v = src[yy * w + xx];
                            if (max) { if (v > m) m = v; }
                            else { if (v < m) m = v; }
                        }
                    }
                    dst[y * w + x] = m;
                }
            }
        }

        static void Box3x3(byte[] src, byte[] dst, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1);
                    int sum = 0, n = 0;
                    for (int yy = y0; yy <= y1; yy++)
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            sum += src[yy * w + xx];
                            n++;
                        }
                    }
                    dst[y * w + x] = (byte)(sum / n);
                }
            }
        }
    }
}
