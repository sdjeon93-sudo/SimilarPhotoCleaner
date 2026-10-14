using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimilarPhotoCleaner.Core;

/// <summary>
/// 사진 파일을 원하는 크기로 줄여 읽는다. EXIF 회전 정보를 반영하고, 파일을 잠그지 않는다.
/// </summary>
public static class ImageLoader
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif",
    };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif";

    public sealed class Pixels
    {
        public required byte[] Bgra { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        /// <summary>회전을 반영한 원본 크기</summary>
        public required int OriginalWidth { get; init; }
        public required int OriginalHeight { get; init; }
        public DateTime? TakenAt { get; init; }

        public BitmapSource ToBitmap()
        {
            var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Bgra, Width * 4);
            bmp.Freeze();
            return bmp;
        }
    }

    /// <summary>화면 표시용: 긴 변이 minLongSide 이상인, 가장 빨리 읽히는 크기로 읽는다.</summary>
    public static BitmapSource LoadForDisplay(string path, int minLongSide) =>
        LoadPixels(File.ReadAllBytes(path), minLongSide).ToBitmap();

    public static BitmapSource LoadFromBytes(byte[] bytes, int minLongSide) => LoadPixels(bytes, minLongSide).ToBitmap();

    public static Pixels LoadPixels(byte[] bytes, int minLongSide)
    {
        int rawW, rawH, orientation = 1;
        DateTime? takenAt = null;
        using (var ms = new MemoryStream(bytes, writable: false))
        {
            var frame = BitmapDecoder.Create(ms,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            rawW = frame.PixelWidth;
            rawH = frame.PixelHeight;
            if (frame.Metadata is BitmapMetadata meta)
            {
                orientation = ReadOrientation(meta);
                takenAt = ReadTakenAt(meta);
            }
        }

        var image = new BitmapImage();
        using (var ms = new MemoryStream(bytes, writable: false))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = ms;
            int longSide = Math.Max(rawW, rawH);
            int size = DecodeSize(longSide, minLongSide);
            if (size < longSide)
            {
                if (rawW >= rawH) image.DecodePixelWidth = size;
                else image.DecodePixelHeight = size;
            }
            image.EndInit();
        }
        image.Freeze();

        return ToPixels(ApplyOrientation(image, orientation), orientation, rawW, rawH, takenAt);
    }

    /// <summary>
    /// JPEG는 1/2, 1/4, 1/8 크기로 읽을 때 압축을 푸는 계산이 크게 줄어든다.
    /// 긴 변이 minLongSide 이상이 되는 가장 작은 크기를 고른다.
    /// </summary>
    private static int DecodeSize(int longSide, int minLongSide)
    {
        foreach (int div in new[] { 8, 4, 2 })
        {
            int size = (longSide + div - 1) / div;
            if (size >= minLongSide) return size;
        }
        return longSide;
    }

    /// <summary>
    /// 사진에 들어 있는 작은 미리보기(EXIF 썸네일, 보통 160×120)를 읽는다. 파일 앞부분만 있어도 된다.
    /// 썸네일이 없거나, 너무 작거나, 원본과 모양이 맞지 않으면(편집한 사진 등) null.
    /// </summary>
    public static Pixels? TryLoadEmbeddedThumbnail(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var frame = BitmapDecoder.Create(ms,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            var thumb = frame.Thumbnail;
            if (thumb == null || Math.Max(thumb.PixelWidth, thumb.PixelHeight) < 96) return null;

            int orientation = 1;
            DateTime? takenAt = null;
            if (frame.Metadata is BitmapMetadata meta)
            {
                orientation = ReadOrientation(meta);
                takenAt = ReadTakenAt(meta);
            }
            var px = ToPixels(ApplyOrientation(thumb, orientation), orientation, frame.PixelWidth, frame.PixelHeight, takenAt);
            return CropLetterbox(px);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 썸네일 비율이 원본과 다르면(16:9 사진의 4:3 썸네일 등) 위아래나 양옆의 검은 띠를 잘라 낸다.
    /// 띠가 검지 않으면 원본과 다른 그림으로 보고 null을 돌려준다.
    /// </summary>
    private static Pixels? CropLetterbox(Pixels px)
    {
        double target = (double)px.OriginalWidth / px.OriginalHeight;
        double actual = (double)px.Width / px.Height;
        if (Math.Abs(actual - target) / target < 0.05) return px;

        int x0 = 0, y0 = 0, w = px.Width, h = px.Height;
        if (target > actual)
        {
            h = (int)Math.Round(px.Width / target);
            y0 = (px.Height - h) / 2;
        }
        else
        {
            w = (int)Math.Round(px.Height * target);
            x0 = (px.Width - w) / 2;
        }
        if (Math.Min(w, h) < 48) return null;

        int bright = 0, outside = 0;
        for (int y = 0; y < px.Height; y++)
        for (int x = 0; x < px.Width; x++)
        {
            if (x >= x0 && x < x0 + w && y >= y0 && y < y0 + h) continue;
            int i = (y * px.Width + x) * 4;
            outside++;
            if (px.Bgra[i] + px.Bgra[i + 1] + px.Bgra[i + 2] > 120) bright++;
        }
        if (outside == 0 || bright > outside * 0.03) return null;

        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Array.Copy(px.Bgra, ((y0 + y) * px.Width + x0) * 4, dst, y * w * 4, w * 4);
        return new Pixels
        {
            Bgra = dst,
            Width = w,
            Height = h,
            OriginalWidth = px.OriginalWidth,
            OriginalHeight = px.OriginalHeight,
            TakenAt = px.TakenAt,
        };
    }

    private static Pixels ToPixels(BitmapSource source, int orientation, int rawW, int rawH, DateTime? takenAt)
    {
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight;
        var bgra = new byte[w * h * 4];
        source.CopyPixels(bgra, w * 4, 0);

        bool swapped = orientation is >= 5 and <= 8;
        return new Pixels
        {
            Bgra = bgra,
            Width = w,
            Height = h,
            OriginalWidth = swapped ? rawH : rawW,
            OriginalHeight = swapped ? rawW : rawH,
            TakenAt = takenAt,
        };
    }

    private static BitmapSource ApplyOrientation(BitmapSource src, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (t == null) return src;
        var tb = new TransformedBitmap(src, t);
        tb.Freeze();
        return tb;
    }

    private static int ReadOrientation(BitmapMetadata meta)
    {
        foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (meta.GetQuery(query) is { } value)
                {
                    int o = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    if (o is >= 1 and <= 8) return o;
                }
            }
            catch { /* 형식마다 지원하는 경로가 다르다 */ }
        }
        return 1;
    }

    private static DateTime? ReadTakenAt(BitmapMetadata meta)
    {
        // 36867 = DateTimeOriginal, 36868 = DateTimeDigitized, 306 = DateTime
        foreach (var query in new[]
                 {
                     "/app1/ifd/exif/{ushort=36867}", "/ifd/exif/{ushort=36867}",
                     "/app1/ifd/exif/{ushort=36868}", "/app1/ifd/{ushort=306}", "/ifd/{ushort=306}",
                 })
        {
            try
            {
                if (meta.GetQuery(query) is string s &&
                    DateTime.TryParseExact(s.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dt))
                    return dt;
            }
            catch { }
        }
        try
        {
            if (meta.DateTaken is string text && DateTime.TryParse(text, out var dt))
                return dt;
        }
        catch { }
        return null;
    }
}
