using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimilarPhotoCleaner.Core;

// 사용법: Harness gen <outDir> | test <dir> | biggen <outDir> | bench <dir>
var thread = new Thread(() =>
{
    switch (args[0])
    {
        case "gen": Generate(args[1]); break;
        case "biggen": GenerateBig(args[1]); break;
        case "bench": Bench(args[1]); break;
        case "thumbtest": ThumbTest(args[1]); break;
        case "scaling": Scaling(args[1]); break;
        case "tilt": TiltTest(args[1]); break;
        case "quick": QuickTest(args[1]); break;
        default: Test(args[1]); break;
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

// 휴대폰 사진처럼 4032×3024, 3~4MB 크기의 JPEG을 만든다(속도 측정용).
static void GenerateBig(string outDir)
{
    Directory.CreateDirectory(outDir);
    var bases = Directory.GetFiles(@"C:\Windows\Web\Wallpaper", "*.jpg", SearchOption.AllDirectories).OrderBy(f => f).ToList();
    var rnd = new Random(1);
    int n = 0;
    for (int round = 0; n < 48; round++)
    foreach (var path in bases)
    {
        if (n >= 48) break;
        var src = LoadScaled(path, 4032);
        var scaled = new TransformedBitmap(src, new ScaleTransform(1, 3024.0 / src.PixelHeight));
        int w = scaled.PixelWidth, h = scaled.PixelHeight;
        var px = new byte[w * h * 4];
        new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i++)
            if (i % 4 != 3) px[i] = (byte)Math.Clamp(px[i] + rnd.Next(-10, 11) + round * 6, 0, 255);
        BitmapSource image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        // 4장 중 1장은 16:9로 잘라서, 카메라처럼 위아래 검은 띠가 있는 160×120 썸네일을 넣는다.
        bool wide = n % 4 == 3;
        if (wide) image = new CroppedBitmap(image, new Int32Rect(0, (h - w * 9 / 16) / 2, w, w * 9 / 16));
        Save(image, Path.Combine(outDir, $"big{n++:00}.jpg"), new DateTime(2025, 5, 3, 9, 0, 0).AddSeconds(n * 7), 92,
            ExifThumb(image, wide));
    }
    Console.WriteLine($"generated {n}");
}

// 기울기 측정 시험: 배경화면을 0~4° 돌려(빈 모서리가 안 보이게 확대) 저장하고 2차 분석으로 잰 기울기를 보여 준다.
static void TiltTest(string outDir)
{
    Directory.CreateDirectory(outDir);
    var bases = Directory.GetFiles(@"C:\Windows\Web\Wallpaper", "*.jpg", SearchOption.AllDirectories).OrderBy(f => f).ToList();
    double[] angles = [0, 1, 2.5, 4];
    var errors = new List<double>();
    int confident = 0, total = 0;
    foreach (var path in bases)
    {
        var src = LoadScaled(path, 1600);
        int w = src.PixelWidth, h = src.PixelHeight;
        var line = new List<string>();
        foreach (double a in angles)
        {
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new RotateTransform(a, w / 2.0, h / 2.0));
                dc.PushTransform(new ScaleTransform(1.15, 1.15, w / 2.0, h / 2.0));
                dc.DrawImage(src, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            string file = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(path)}_{a:0.0}.jpg");
            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);

            var d = PhotoAnalyzer.AnalyzeDetail(file);
            line.Add($"{a:0.0}°→{d.Tilt:0.0}°({d.TiltConfidence:0.00})");
            total++;
            if (d.TiltConfidence >= 0.3f) { confident++; errors.Add(Math.Abs(d.Tilt - a)); }
        }
        Console.WriteLine($"  {Path.GetFileName(path),-24} {string.Join("  ", line)}");
    }
    Console.WriteLine($"믿음 0.3 이상: {confident}/{total}장, 그중 오차 평균 {errors.DefaultIfEmpty(0).Average():0.00}° 최대 {errors.DefaultIfEmpty(0).Max():0.00}°");
}

// 빠르게 정리하기 시험: 기본 단계로 묶고 2차 분석까지 한 뒤, 묶음마다 남길 사진을 점수와 함께 보여 준다.
// gen으로 만든 사진이면 남긴 사진 중 흐림·기울임·사람 변형이 몇 장인지 센다.
static void QuickTest(string dir)
{
    var photos = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).Select(f => PhotoAnalyzer.Analyze(new FileInfo(f))).ToList();
    var bad = new Dictionary<string, int>();
    int kept = 0;
    foreach (var members in Grouper.Group(photos, 3).Select(g => g.Select(i => photos[i]).ToList()))
    {
        foreach (var p in members.Where(p => !p.IsDetailed))
        {
            var d = PhotoAnalyzer.AnalyzeDetail(p.Path);
            (p.Sharpness, p.Tilt, p.TiltConfidence, p.IsDetailed) = (d.Sharpness, d.Tilt, d.TiltConfidence, true);
        }
        var group = new SimilarPhotoCleaner.ViewModels.PhotoGroupView(0, members, dir);
        var keep = group.QuickPick();
        Console.WriteLine($"묶음 {members.Count}장 → {keep.Count}장 남김");
        foreach (var c in group.Cards.OrderByDescending(c => c.Quality))
        {
            string variant = c.FileName.Contains("__") ? c.FileName.Split("__")[1].Replace(".jpg", "") : "";
            Console.WriteLine($"  {(keep.Contains(c) ? "남김" : "    ")} {c.FileName,-22} 점수 {c.Quality:0.00}  선명 {c.SharpnessRatio:0.00}  구도 {c.Composition:0.00}  기울기 {c.Info.Tilt:0.0}°({c.Info.TiltConfidence:0.00})");
            if (!keep.Contains(c)) continue;
            kept++;
            if (variant is "blur" or "tilt" or "person" or "pan") bad[variant] = bad.GetValueOrDefault(variant) + 1;
        }
    }
    Console.WriteLine($"남긴 사진 {kept}장 중 흐림 {bad.GetValueOrDefault("blur")} · 기울임 {bad.GetValueOrDefault("tilt")} · 사람 {bad.GetValueOrDefault("person")} · 이동+사람 {bad.GetValueOrDefault("pan")}");
}

// 빠른 분석을 스레드 수별로 돌려 본다(두 번째 바퀴 시간 = JIT 제외).
static void Scaling(string dir)
{
    var files = Directory.GetFiles(dir, "*.jpg", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
    foreach (int threads in new[] { 1, 4, 8, 16, 32 })
    for (int round = 0; round < 2; round++)
    {
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = threads }, f => PhotoAnalyzer.Analyze(f));
        if (round == 1) Console.WriteLine($"  {threads,2} threads: {sw.ElapsedMilliseconds,5} ms / {files.Count}장 = {sw.Elapsed.TotalMilliseconds / files.Count:0.00} ms/장");
    }
}

static BitmapSource ExifThumb(BitmapSource image, bool letterbox)
{
    var dv = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality); // 카메라처럼 부드럽게 줄인다
    using (var dc = dv.RenderOpen())
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 160, 120));
        double h = letterbox ? 160.0 * image.PixelHeight / image.PixelWidth : 120;
        dc.DrawImage(image, new Rect(0, (120 - h) / 2, 160, h));
    }
    var rtb = new RenderTargetBitmap(160, 120, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(dv);
    return rtb;
}

static void ThumbTest(string dir)
{
    foreach (var f in Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).Take(8))
    {
        var sw = Stopwatch.StartNew();
        var head = new byte[Math.Min(256 * 1024, new FileInfo(f).Length)];
        using (var fs = File.OpenRead(f)) fs.ReadExactly(head);
        var dec = BitmapDecoder.Create(new MemoryStream(head), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = dec.Frames[0];
        var thumb = frame.Thumbnail;
        string t = thumb == null ? "none" : $"{thumb.PixelWidth}x{thumb.PixelHeight}";
        if (thumb != null) { var p = new byte[thumb.PixelWidth * thumb.PixelHeight * 4]; new FormatConvertedBitmap(thumb, PixelFormats.Bgra32, null, 0).CopyPixels(p, thumb.PixelWidth * 4, 0); }
        Console.WriteLine($"  {Path.GetFileName(f)} {frame.PixelWidth}x{frame.PixelHeight} thumb={t} {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }
}

static void Bench(string dir)
{
    var files = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).ToList();
    var bytes = files.Select(File.ReadAllBytes).ToList();
    Console.WriteLine($"{files.Count} files, avg {bytes.Average(b => b.Length) / 1048576.0:0.0} MB, {Environment.ProcessorCount} cores");

    void Time(string name, Action<byte[]> action)
    {
        action(bytes[0]);
        var sw = Stopwatch.StartNew();
        foreach (var b in bytes) action(b);
        Console.WriteLine($"  {name,-40} {sw.Elapsed.TotalMilliseconds / bytes.Count,7:0.0} ms/장");
    }

    BitmapSource Image(byte[] b, int width)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(b);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (width > 0) bi.DecodePixelWidth = width;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    Time("metadata only", b => BitmapDecoder.Create(new MemoryStream(b), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].Metadata.ToString());
    Time("BitmapImage width=512", b => Image(b, 512));
    Time("BitmapImage width=504 (1/8)", b => Image(b, 504));
    Time("BitmapImage width=1008 (1/4)", b => Image(b, 1008));
    Time("BitmapImage width=2016 (1/2)", b => Image(b, 2016));
    Time("BitmapImage width=2560", b => Image(b, 2560));
    Time("BitmapImage full", b => Image(b, 0));
    Time("ImageLoader.LoadPixels(384)", b => ImageLoader.LoadPixels(b, 384));
    Time("ImageLoader.LoadPixels(1800) 크게 보기", b => ImageLoader.LoadPixels(b, 1800));

    var infos = files.Select(f => new FileInfo(f)).ToList();
    foreach (bool quick in new[] { false, true })
    {
        string mode = quick ? "빠른 분석" : "원본 분석";
        var sw = Stopwatch.StartNew();
        foreach (var f in infos) PhotoAnalyzer.Analyze(f, quick);
        Console.WriteLine($"  {mode + " (1 thread)",-40} {sw.Elapsed.TotalMilliseconds / files.Count,7:0.0} ms/장");
        sw.Restart();
        Parallel.ForEach(infos, f => PhotoAnalyzer.Analyze(f, quick));
        Console.WriteLine($"  {mode + " (parallel)",-40} {sw.Elapsed.TotalMilliseconds / files.Count,7:0.0} ms/장");
    }

    // 빠른 분석(내장 썸네일)과 원본 분석의 구도 지문 차이
    var diffs = infos.Select(f =>
    {
        var q = PhotoAnalyzer.Analyze(f);
        var full = PhotoAnalyzer.Analyze(f, allowQuick: false);
        return (d: Grouper.Distance(q, full), c: PhotoAnalyzer.ColorSimilarity(q.ColorHistogram, full.ColorHistogram), quick: !q.IsDetailed, f.Name);
    }).ToList();
    Console.WriteLine($"  빠른 분석 사용 {diffs.Count(x => x.quick)}/{diffs.Count}장, 지문 차이 평균 {diffs.Average(x => x.d):0.0} 최대 {diffs.Max(x => x.d)}, 색 유사도 최소 {diffs.Min(x => x.c):0.00}");
}

static void Test(string dir)
{
    var files = Directory.GetFiles(dir, "*.jpg", SearchOption.AllDirectories).OrderBy(f => f).ToList();
    var sw = Stopwatch.StartNew();
    var photos = new PhotoInfo?[files.Count];
    Parallel.For(0, files.Count, i =>
    {
        try { photos[i] = PhotoAnalyzer.Analyze(new FileInfo(files[i]), Environment.GetEnvironmentVariable("FULL") == null); }
        catch (Exception ex) { Console.WriteLine($"FAIL {files[i]}: {ex.Message}"); }
    });
    var list = photos.OfType<PhotoInfo>().ToList();
    Console.WriteLine($"analyzed {list.Count} in {sw.ElapsedMilliseconds} ms (빠른 분석 {list.Count(p => !p.IsDetailed)}장)");
    foreach (var p in list.Where(p => p.IsDetailed).Take(5)) Console.WriteLine($"  원본으로 분석: {p.FileName} {new FileInfo(p.Path).Length / 1024} KB");

    // 같은 base(파일명 앞부분)끼리 vs 다른 base 거리 분포
    string Base(PhotoInfo p) => p.FileName.Split("__")[0];
    var same = new List<(int d, float c, string a, string b)>();
    var diff = new List<(int d, float c, string a, string b)>();
    for (int i = 0; i < list.Count; i++)
    for (int j = i + 1; j < list.Count; j++)
    {
        int d = Grouper.Distance(list[i], list[j]);
        float c = PhotoAnalyzer.ColorSimilarity(list[i].ColorHistogram, list[j].ColorHistogram);
        (Base(list[i]) == Base(list[j]) ? same : diff).Add((d, c, list[i].FileName, list[j].FileName));
    }
    Console.WriteLine("== same scene: worst 12 (by distance)");
    foreach (var x in same.OrderByDescending(x => x.d).Take(12)) Console.WriteLine($"  d={x.d,2} c={x.c:F2}  {x.a} ~ {x.b}");
    Console.WriteLine("== same scene: worst 6 (by color)");
    foreach (var x in same.OrderBy(x => x.c).Take(6)) Console.WriteLine($"  d={x.d,2} c={x.c:F2}  {x.a} ~ {x.b}");
    Console.WriteLine("== different scenes: closest 15");
    foreach (var x in diff.OrderBy(x => x.d).Take(15)) Console.WriteLine($"  d={x.d,2} c={x.c:F2}  {x.a} ~ {x.b}");
    foreach (var p in list.Where(p => p.FileName.Contains("__pan")))
    {
        var best = list.Where(o => o != p && Base(o) == Base(p))
            .Select(o => (d: Grouper.Distance(p, o), c: PhotoAnalyzer.ColorSimilarity(p.ColorHistogram, o.ColorHistogram), o.FileName))
            .OrderBy(x => x.d).First();
        Console.WriteLine($"  pan {p.FileName}: nearest d={best.d} c={best.c:0.00} ({best.FileName})");
    }
    var ds = diff.Select(x => x.d).OrderBy(x => x).ToList();
    Console.WriteLine($"diff distance percentiles: p1={ds[ds.Count / 100]} p5={ds[ds.Count / 20]} p50={ds[ds.Count / 2]}");

    for (int level = 1; level <= 5; level++)
    {
        sw.Restart();
        var groups = Grouper.Group(list, level);
        int pure = groups.Count(g => g.Select(i => Base(list[i])).Distinct().Count() == 1);
        var bases = list.GroupBy(Base).Where(g => g.Count() >= 2).ToList();
        int complete = bases.Count(b => groups.Any(g => g.Count(i => Base(list[i]) == b.Key) == b.Count()));
        Console.WriteLine($"level {level}: {groups.Count} groups ({sw.ElapsedMilliseconds} ms), pure={pure}, scenes fully grouped={complete}/{bases.Count}");
        foreach (var g in groups.Where(g => g.Select(i => Base(list[i])).Distinct().Count() > 1))
            Console.WriteLine("   mixed: " + string.Join(", ", g.Select(i => list[i].FileName)));
        foreach (var b in bases.Where(b => !groups.Any(g => g.Count(i => Base(list[i]) == b.Key) == b.Count())))
        {
            var missing = b.Where(p => !groups.Any(g => g.Count >= 2 && g.Any(i => list[i] == p) && g.Count(i => Base(list[i]) == b.Key) > 1));
            Console.WriteLine($"   split: {b.Key} -> alone: {string.Join(", ", missing.Select(p => p.FileName.Split("__")[1]))}");
        }
    }
}

static void Generate(string outDir)
{
    Directory.CreateDirectory(outDir);
    var bases = Directory.GetFiles(@"C:\Windows\Web", "*.jpg", SearchOption.AllDirectories)
        .Where(f => !f.Contains("touchkeyboard", StringComparison.OrdinalIgnoreCase))
        .GroupBy(f => new FileInfo(f).Length).Select(g => g.First()) // 동일 파일 제거
        .Where(f => !f.Contains("_1920x1200")).OrderBy(f => f).ToList();

    int k = 0;
    foreach (var path in bases)
    {
        string name = $"s{k:00}";
        // 장면마다 10분 간격. 단, 12~15번은 서로 다른 장면을 20초 간격으로 찍은 것처럼 만든다.
        var time = k is >= 12 and <= 15
            ? new DateTime(2025, 5, 3, 15, 0, 0).AddSeconds((k - 12) * 20)
            : new DateTime(2025, 5, 3, 9, 0, 0).AddMinutes(k * 10);
        k++;
        int v = 0;
        // 카메라처럼 EXIF 썸네일을 넣고, 파일이 256KB보다 커서 빠른 분석 경로를 타도록 크게 저장한다.
        void Out(BitmapSource bmp, string suffix) => Save(bmp, Path.Combine(outDir, $"{name}__{suffix}.jpg"), time.AddSeconds(4 * v++),
            95, ExifThumb(bmp, letterbox: true));

        var src = LoadScaled(path, 3000);
        Out(src, "orig");
        if (k > 12) continue; // 앞의 12장만 변형을 만든다(나머지는 다른 장면 비교용)

        int w = src.PixelWidth, h = src.PixelHeight;
        Out(new CroppedBitmap(src, new Int32Rect(0, 0, (int)(w * 0.92), (int)(h * 0.92))), "cropTL");
        Out(new CroppedBitmap(src, new Int32Rect((int)(w * 0.08), (int)(h * 0.06), (int)(w * 0.92), (int)(h * 0.94))), "cropBR");
        Out(new CroppedBitmap(src, new Int32Rect((int)(w * 0.15), (int)(h * 0.10), (int)(w * 0.80), (int)(h * 0.80))), "zoom");
        Out(Brightness(src, 1.18), "bright");
        Out(Brightness(src, 0.80), "dark");
        Out(Draw(src, (dc, W, H) => Person(dc, W * 0.3, H)), "person");
        Out(Draw(new CroppedBitmap(src, new Int32Rect((int)(w * 0.15), 0, (int)(w * 0.85), h)), (dc, W, H) => Person(dc, W * 0.6, H)), "pan");
        Out(Draw(src, (dc, W, H) =>
        {
            dc.PushTransform(new RotateTransform(2.5, W / 2, H / 2));
            dc.PushTransform(new ScaleTransform(1.08, 1.08, W / 2, H / 2));
            dc.DrawImage(src, new Rect(0, 0, W, H));
        }, drawBase: false), "tilt");
        var small = new TransformedBitmap(src, new ScaleTransform(1 / 8.0, 1 / 8.0));
        Out(new TransformedBitmap(small, new ScaleTransform(8, 8)), "blur");
    }
    Console.WriteLine($"generated from {bases.Count} bases");
}

static void Person(DrawingContext dc, double x, double H)
{
    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(40, 30, 30)), null, new Point(x, H * 0.75), H * 0.11, H * 0.22);
    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(230, 190, 160)), null, new Point(x, H * 0.47), H * 0.055, H * 0.06);
}

static BitmapSource LoadScaled(string path, int width)
{
    var bi = new BitmapImage();
    bi.BeginInit();
    bi.UriSource = new Uri(path);
    bi.DecodePixelWidth = width;
    bi.CacheOption = BitmapCacheOption.OnLoad;
    bi.EndInit();
    bi.Freeze();
    return new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
}

static BitmapSource Brightness(BitmapSource src, double f)
{
    int w = src.PixelWidth, h = src.PixelHeight;
    var px = new byte[w * h * 4];
    src.CopyPixels(px, w * 4, 0);
    for (int i = 0; i < px.Length; i++)
        if (i % 4 != 3) px[i] = (byte)Math.Clamp(px[i] * f, 0, 255);
    return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
}

static BitmapSource Draw(BitmapSource src, Action<DrawingContext, double, double> draw, bool drawBase = true)
{
    int w = src.PixelWidth, h = src.PixelHeight;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        if (drawBase) dc.DrawImage(src, new Rect(0, 0, w, h));
        draw(dc, w, h);
    }
    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(dv);
    return rtb;
}

static void Save(BitmapSource bmp, string path, DateTime taken, int quality = 88, BitmapSource? thumbnail = null)
{
    var enc = new JpegBitmapEncoder { QualityLevel = quality };
    var meta = new BitmapMetadata("jpg");
    meta.SetQuery("/app1/ifd/exif/{ushort=36867}", taken.ToString("yyyy:MM:dd HH:mm:ss"));
    enc.Frames.Add(BitmapFrame.Create(bmp, thumbnail, meta, null));
    using var fs = File.Create(path);
    enc.Save(fs);
}

