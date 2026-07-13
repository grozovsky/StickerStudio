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
            double kr = k.ScreenColor.R, kg = k.ScreenColor.G, kb = k.ScreenColor.B;
            double kCb = -0.168736 * kr - 0.331264 * kg + 0.5 * kb;
            double kCr = 0.5 * kr - 0.418688 * kg - 0.081312 * kb;
            double K = Math.Sqrt(kCb * kCb + kCr * kCr);

            // почти серый ключ — направление оттенка не определено,
            // работаем по простому расстоянию
            bool grayKey = K < 25;
            double ux = grayKey ? 0 : kCb / K;
            double uy = grayKey ? 0 : kCr / K;

            // Gain 0..200 -> порог начала прозрачности по "ключевости" (0..1)
            double t0 = 0.90 - k.Gain * 0.00375;   // 0->0.90, 100->0.525, 200->0.15
            double t1 = Math.Min(1.02, t0 + 0.35); // полностью прозрачно
            double grayT1 = Math.Max(10.0, 120.0 - k.Gain * 0.5);
            double grayT0 = grayT1 * 0.45;

            int mainCh = 1; // green (B=0,G=1,R=2 в BGRA)
            if (kr >= kg && kr >= kb) mainCh = 2;
            else if (kb >= kg && kb >= kr) mainCh = 0;

            byte[] alpha = new byte[w * h];
            double[] keyness = new double[w * h]; // для despill

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    double b = px[i], g = px[i + 1], r = px[i + 2];
                    double cb = -0.168736 * r - 0.331264 * g + 0.5 * b;
                    double cr = 0.5 * r - 0.418688 * g - 0.081312 * b;

                    double a;
                    double score;
                    if (grayKey)
                    {
                        double d = Math.Sqrt((cb - kCb) * (cb - kCb) + (cr - kCr) * (cr - kCr));
                        score = 1.0 - Math.Min(1.0, d / Math.Max(1.0, grayT1 * 2));
                        if (d <= grayT0) a = 0;
                        else if (d >= grayT1) a = 1;
                        else a = Smooth((d - grayT0) / (grayT1 - grayT0));
                    }
                    else
                    {
                        double proj = cb * ux + cr * uy;          // вдоль оттенка ключа
                        double perp = Math.Abs(cr * ux - cb * uy); // отклонение оттенка
                        score = (proj - perp * 0.85) / K;          // ~1 = чистый ключ

                        if (score >= t1) a = 0;
                        else if (score <= t0) a = 1;
                        else a = 1.0 - Smooth((score - t0) / (t1 - t0));
                    }

                    keyness[y * w + x] = score;
                    alpha[y * w + x] = (byte)Math.Round(a * 255);
                }
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
            byte[] blurred = new byte[w * h];
            Box3x3(alpha, blurred, w, h);
            alpha = blurred;

            // применяем альфу + despill с компенсацией яркости
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    int idx = y * w + x;
                    byte a = alpha[idx];
                    int srcA = px[i + 3];

                    // Despill шире самой полупрозрачной кромки. Раньше полностью
                    // непрозрачный пиксель сразу рядом с matte не обрабатывался и
                    // после ресайза снова давал зелёную кайму.
                    double matteInfluence = 1.0 - a / 255.0;
                    double wSpill = (keyness[idx] - 0.08) / 0.55;
                    if (wSpill > 0)
                    {
                        if (wSpill > 1) wSpill = 1;
                        // На непрозрачном foreground воздействие мягкое, на matte
                        // усиливается до полного — сохраняем естественные цвета лица.
                        wSpill *= 0.35 + matteInfluence * 0.65;
                        byte c0 = px[i], c1 = px[i + 1], c2 = px[i + 2];
                        byte other = mainCh == 1 ? Math.Max(c0, c2)
                                   : mainCh == 2 ? Math.Max(c0, c1)
                                   : Math.Max(c1, c2);
                        int mi = i + mainCh;
                        int spill = px[mi] - other;
                        if (spill > 0)
                        {
                            px[mi] = (byte)(px[mi] - spill * wSpill);
                            // вернуть часть яркости нейтрально — края не темнеют
                            int comp = (int)(spill * wSpill * 0.35);
                            px[i] = ClampB(px[i] + comp);
                            px[i + 1] = ClampB(px[i + 1] + comp);
                            px[i + 2] = ClampB(px[i + 2] + comp);
                        }
                    }

                    // RGB полностью прозрачных пикселей некоторые Windows-preview
                    // показывают без alpha. Нейтральный ноль исключает зелёную заливку
                    // и не влияет на корректные декодеры Telegram.
                    if (a <= 2)
                        px[i] = px[i + 1] = px[i + 2] = 0;

                    px[i + 3] = (byte)(a * srcA / 255);
                }
            }
        }

        static double Smooth(double x)
        {
            if (x < 0) x = 0;
            if (x > 1) x = 1;
            return x * x * (3 - 2 * x);
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
