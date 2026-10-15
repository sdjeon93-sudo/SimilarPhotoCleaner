using System.IO;
using System.Windows.Media.Imaging;

namespace SimilarPhotoCleaner.Core;

/// <summary>
/// 사진 한 장에서 비교에 필요한 특징(구도 지문, 색 분포, 선명도)과 썸네일을 만든다.
/// </summary>
public static class PhotoAnalyzer
{
    /// <summary>원본을 읽을 때 긴 변의 최소 크기. 4000px 사진이면 1/8 크기(500px)로 읽힌다.</summary>
    private const int AnalysisMinSide = 384;
    /// <summary>JPEG 내장 썸네일은 보통 파일 앞 64KB 안에 있다.</summary>
    private const int HeadSize = 256 * 1024;
    private const int ThumbnailSize = 320;
    private const int HashInput = 32;
    private const int HashSide = 8;

    private static readonly double[] Cos = BuildCosTable();

    /// <summary>
    /// 1차 분석: 묶기에 필요한 특징을 만든다. 가능하면 파일 앞부분만 읽어 내장 썸네일로 빠르게 분석하고
    /// (IsDetailed = false), 그럴 수 없으면 원본을 읽는다(IsDetailed = true).
    /// </summary>
    public static PhotoInfo Analyze(FileInfo file, bool allowQuick = true)
    {
        if (allowQuick && file.Length > HeadSize && ImageLoader.IsJpeg(file.FullName))
        {
            var head = ReadHead(file.FullName, HeadSize);
            if (ImageLoader.TryLoadEmbeddedThumbnail(head) is { } thumb)
                return Build(file, thumb, detailed: false);
        }

        var bytes = File.ReadAllBytes(file.FullName);
        // HEIC 같은 형식도 내장 썸네일이 있으면 무거운 원본 압축 풀기를 건너뛴다.
        if (allowQuick && !ImageLoader.IsJpeg(file.FullName) && file.Length > HeadSize &&
            ImageLoader.TryLoadEmbeddedThumbnail(bytes) is { } embedded)
            return Build(file, embedded, detailed: false);

        return Build(file, ImageLoader.LoadPixels(bytes, AnalysisMinSide), detailed: true);
    }

    public readonly record struct Detail(double Sharpness, float Tilt, float TiltConfidence, byte[] Thumbnail);

    /// <summary>2차 분석: 원본을 읽어 선명도·기울기와 선명한 썸네일을 만든다. 묶음에 든 사진에만 쓴다.</summary>
    public static Detail AnalyzeDetail(string path)
    {
        var px = ImageLoader.LoadPixels(File.ReadAllBytes(path), AnalysisMinSide);
        var gray = ToGray(px);
        var (tilt, confidence) = Tilt(gray, px.Width, px.Height);
        return new Detail(Sharpness(gray, px.Width, px.Height), tilt, confidence, MakeThumbnail(px));
    }

    private static byte[] ReadHead(string path, int size)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        var buffer = new byte[size];
        int read = 0;
        while (read < size)
        {
            int n = fs.Read(buffer, read, size - read);
            if (n == 0) break;
            read += n;
        }
        return read == size ? buffer : buffer[..read];
    }

    private static PhotoInfo Build(FileInfo file, ImageLoader.Pixels px, bool detailed)
    {
        var gray = ToGray(px);

        // 조금 당겨 찍은 사진과도 비교할 수 있도록 가운데 80% 영역의 지문도 만든다.
        var (tilt, tiltConfidence) = detailed ? Tilt(gray, px.Width, px.Height) : (0f, 0f);
        int cw = Math.Max(1, px.Width * 4 / 5), ch = Math.Max(1, px.Height * 4 / 5);
        var center = new float[cw * ch];
        for (int y = 0; y < ch; y++)
            Array.Copy(gray, (y + (px.Height - ch) / 2) * px.Width + (px.Width - cw) / 2, center, y * cw, cw);

        return new PhotoInfo
        {
            Path = file.FullName,
            FileSize = file.Length,
            ModifiedUtc = file.LastWriteTimeUtc,
            Width = px.OriginalWidth,
            Height = px.OriginalHeight,
            TakenAt = px.TakenAt,
            Hash = PerceptualHash(ResizeGray(gray, px.Width, px.Height, HashInput, HashInput)),
            CenterHash = PerceptualHash(ResizeGray(center, cw, ch, HashInput, HashInput)),
            ColorHistogram = ColorHistogram(px),
            IsDetailed = detailed,
            Sharpness = detailed ? Sharpness(gray, px.Width, px.Height) : 0,
            Tilt = tilt,
            TiltConfidence = tiltConfidence,
            ThumbnailJpeg = MakeThumbnail(px),
        };
    }

    public static float ColorSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Min(a[i], b[i]);
        return sum;
    }

    private static float[] ToGray(ImageLoader.Pixels px)
    {
        var gray = new float[px.Width * px.Height];
        var b = px.Bgra;
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
            gray[i] = 0.114f * b[j] + 0.587f * b[j + 1] + 0.299f * b[j + 2];
        return gray;
    }

    private static double[] ResizeGray(float[] src, int w, int h, int ow, int oh)
    {
        var dst = new double[ow * oh];
        for (int oy = 0; oy < oh; oy++)
        {
            int y0 = oy * h / oh, y1 = Math.Max(y0 + 1, (oy + 1) * h / oh);
            for (int ox = 0; ox < ow; ox++)
            {
                int x0 = ox * w / ow, x1 = Math.Max(x0 + 1, (ox + 1) * w / ow);
                double sum = 0;
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    sum += src[y * w + x];
                dst[oy * ow + ox] = sum / ((y1 - y0) * (x1 - x0));
            }
        }
        return dst;
    }

    private static double[] BuildCosTable()
    {
        var table = new double[HashSide * HashInput];
        for (int k = 0; k < HashSide; k++)
        for (int n = 0; n < HashInput; n++)
            table[k * HashInput + n] = Math.Cos(Math.PI * (2 * n + 1) * k / (2.0 * HashInput));
        return table;
    }

    /// <summary>pHash: 32×32 흑백 이미지의 저주파 DCT 8×8 계수가 중앙값보다 큰지로 64비트를 만든다.</summary>
    private static ulong PerceptualHash(double[] g)
    {
        var rows = new double[HashInput * HashSide];
        for (int y = 0; y < HashInput; y++)
        for (int u = 0; u < HashSide; u++)
        {
            double s = 0;
            for (int x = 0; x < HashInput; x++) s += g[y * HashInput + x] * Cos[u * HashInput + x];
            rows[y * HashSide + u] = s;
        }

        var coef = new double[HashSide * HashSide];
        for (int v = 0; v < HashSide; v++)
        for (int u = 0; u < HashSide; u++)
        {
            double s = 0;
            for (int y = 0; y < HashInput; y++) s += rows[y * HashSide + u] * Cos[v * HashInput + y];
            coef[v * HashSide + u] = s;
        }

        var sorted = (double[])coef.Clone();
        Array.Sort(sorted);
        double median = (sorted[31] + sorted[32]) / 2;
        ulong hash = 0;
        for (int i = 0; i < coef.Length; i++)
            if (coef[i] > median) hash |= 1UL << i;
        return hash;
    }

    private const int ChromaBins = 10;

    /// <summary>
    /// 색 분포. 밝기와 상관없는 색 비율(r, g 비율)을 10×10 칸에 부드럽게 나눠 담고,
    /// 아주 어두운 픽셀은 마지막 칸에 따로 센다. 합계는 1이다.
    /// </summary>
    private static float[] ColorHistogram(ImageLoader.Pixels px)
    {
        var hist = new float[ChromaBins * ChromaBins + 1];
        var b = px.Bgra;
        const float scale = ChromaBins - 1;
        for (int j = 0; j < b.Length; j += 4)
        {
            int sum = b[j] + b[j + 1] + b[j + 2];
            if (sum < 24)
            {
                hist[^1]++;
                continue;
            }
            float r = b[j + 2] * scale / sum, g = b[j + 1] * scale / sum;
            int r0 = Math.Min((int)r, ChromaBins - 2), g0 = Math.Min((int)g, ChromaBins - 2);
            float fr = r - r0, fg = g - g0;
            int i = r0 * ChromaBins + g0;
            hist[i] += (1 - fr) * (1 - fg);
            hist[i + ChromaBins] += fr * (1 - fg);
            hist[i + 1] += (1 - fr) * fg;
            hist[i + ChromaBins + 1] += fr * fg;
        }
        float count = px.Width * px.Height;
        for (int i = 0; i < hist.Length; i++) hist[i] /= count;
        return hist;
    }

    /// <summary>
    /// 라플라시안 분산을 밝기 분산으로 나눈 값. 흔들리거나 초점이 나간 사진일수록 작다.
    /// 나누지 않으면 밝게(대비가 크게) 찍힌 사진이 더 선명하다고 나온다.
    /// </summary>
    private static double Sharpness(float[] g, int w, int h)
    {
        if (w < 3 || h < 3) return 0;
        double gSum = 0, gSumSq = 0;
        foreach (float v in g)
        {
            gSum += v;
            gSumSq += v * v;
        }
        double contrast = gSumSq / g.Length - (gSum / g.Length) * (gSum / g.Length);
        if (contrast < 1) return 0; // 거의 한 가지 색인 사진

        double sum = 0, sumSq = 0;
        long n = 0;
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int i = y * w + x;
            double lap = g[i - 1] + g[i + 1] + g[i - w] + g[i + w] - 4 * g[i];
            sum += lap;
            sumSq += lap * lap;
            n++;
        }
        double mean = sum / n;
        return (sumSq / n - mean * mean) / contrast;
    }

    private const int TiltBinsPerDegree = 4;
    /// <summary>이보다 많이 돌아간 선은 원래 비스듬한 물체(지붕·계단 등)로 보고 기울기로 치지 않는다.</summary>
    private const int TiltSearchDegrees = 8;

    /// <summary>
    /// 기울기: 강한 가장자리의 방향을 90° 주기로 접어 모은다(가로선과 세로선이 같은 칸에 모임).
    /// 수평이 맞으면 0° 칸에 몰리고, 사진이 t°만큼 돌아가 있으면 t° 칸에 몰린다.
    /// 몰린 정도가 약하면(곧은 선이 없는 사진) 믿음 정도를 낮게 돌려준다.
    /// </summary>
    private static (float Tilt, float Confidence) Tilt(float[] g, int w, int h)
    {
        if (w < 16 || h < 16) return (0, 0);
        const int bins = 90 * TiltBinsPerDegree;
        var mag = new float[w * h];
        var ang = new float[w * h];
        double sumMag = 0;
        int count = 0;
        // 가장자리 2픽셀은 뺀다(테두리·검은 띠의 경계가 선으로 잡히지 않게).
        for (int y = 2; y < h - 2; y++)
        for (int x = 2; x < w - 2; x++)
        {
            int i = y * w + x;
            // Scharr 필터: Sobel보다 방향이 정확하다.
            float gx = 3 * (g[i - w + 1] - g[i - w - 1] + g[i + w + 1] - g[i + w - 1]) + 10 * (g[i + 1] - g[i - 1]);
            float gy = 3 * (g[i + w - 1] - g[i - w - 1] + g[i + w + 1] - g[i - w + 1]) + 10 * (g[i + w] - g[i - w]);
            float m = MathF.Sqrt(gx * gx + gy * gy);
            mag[i] = m;
            ang[i] = MathF.Atan2(gy, gx) * (180 / MathF.PI);
            sumMag += m;
            count++;
        }
        float threshold = (float)Math.Max(160, 3 * sumMag / count);

        var hist = new double[bins];
        double total = 0;
        for (int i = 0; i < mag.Length; i++)
        {
            if (mag[i] < threshold) continue;
            float a = ((ang[i] % 90) + 90) % 90;
            hist[(int)(a * TiltBinsPerDegree) % bins] += mag[i];
            total += mag[i];
        }
        if (total <= 0) return (0, 0);

        // ±1° 안의 무게가 가장 큰 곳을 찾는다(0° 둘레만).
        const int window = TiltBinsPerDegree;
        double Around(int center)
        {
            double s = 0;
            for (int k = -window; k <= window; k++) s += hist[((center + k) % bins + bins) % bins];
            return s;
        }
        int bestOffset = 0;
        double bestWeight = -1;
        for (int off = -TiltSearchDegrees * TiltBinsPerDegree; off <= TiltSearchDegrees * TiltBinsPerDegree; off++)
        {
            double weight = Around(off);
            if (weight > bestWeight) (bestWeight, bestOffset) = (weight, off);
        }

        // 봉우리 안에서 가중 평균으로 각도를 더 정확히 잡는다(칸 가운데 = +0.5칸).
        double sumW = 0, sumA = 0;
        for (int k = -window; k <= window; k++)
        {
            double weight = hist[((bestOffset + k) % bins + bins) % bins];
            sumW += weight;
            sumA += weight * (bestOffset + k + 0.5) / TiltBinsPerDegree;
        }
        float tilt = (float)Math.Abs(sumA / Math.Max(1e-9, sumW));

        // 고르게 퍼져 있을 때보다 몇 배 몰렸는지: 1.5배 이하면 믿지 않고, 5.5배 이상이면 완전히 믿는다.
        double uniform = (2 * window + 1) / (double)bins;
        float confidence = (float)Math.Clamp((bestWeight / total / uniform - 1.5) / 4, 0, 1);
        return (tilt, confidence);
    }

    private static byte[] MakeThumbnail(ImageLoader.Pixels px)
    {
        double scale = Math.Min(1.0, (double)ThumbnailSize / Math.Max(px.Width, px.Height));
        int tw = Math.Max(1, (int)Math.Round(px.Width * scale));
        int th = Math.Max(1, (int)Math.Round(px.Height * scale));

        // 박스 평균으로 줄여서 깔끔한 썸네일을 만든다.
        var dst = new byte[tw * th * 4];
        var src = px.Bgra;
        for (int oy = 0; oy < th; oy++)
        {
            int y0 = oy * px.Height / th, y1 = Math.Max(y0 + 1, (oy + 1) * px.Height / th);
            for (int ox = 0; ox < tw; ox++)
            {
                int x0 = ox * px.Width / tw, x1 = Math.Max(x0 + 1, (ox + 1) * px.Width / tw);
                int sb = 0, sg = 0, sr = 0, n = (y1 - y0) * (x1 - x0);
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * px.Width + x) * 4;
                    sb += src[i];
                    sg += src[i + 1];
                    sr += src[i + 2];
                }
                int o = (oy * tw + ox) * 4;
                dst[o] = (byte)(sb / n);
                dst[o + 1] = (byte)(sg / n);
                dst[o + 2] = (byte)(sr / n);
                dst[o + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(tw, th, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, dst, tw * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
