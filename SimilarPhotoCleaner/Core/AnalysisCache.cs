using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SimilarPhotoCleaner.Core;

/// <summary>
/// 분석 결과를 폴더별로 저장해 두었다가, 같은 파일(이름·크기·수정 시각이 같음)을 다시 분석하지 않게 한다.
/// 저장 위치: %LocalAppData%\SimilarPhotoCleaner\cache
/// </summary>
public sealed class AnalysisCache
{
    /// <summary>분석 방식이 바뀌면 올려서 예전 결과를 버린다.</summary>
    private const int FormatVersion = 1;
    private static readonly TimeSpan KeepUnused = TimeSpan.FromDays(90);

    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimilarPhotoCleaner", "cache");
    private readonly Dictionary<string, Folder> _folders = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Folder(string path)
    {
        public string Path { get; } = path;
        public Dictionary<string, PhotoInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Dirty { get; set; }
    }

    /// <summary>저장된 결과가 있고 파일이 그대로면 돌려준다. 여러 스레드에서 불러도 된다.</summary>
    public PhotoInfo? TryGet(FileInfo file)
    {
        var folder = GetFolder(file.DirectoryName!);
        lock (folder)
        {
            if (!folder.Items.TryGetValue(file.Name, out var photo) ||
                photo.FileSize != file.Length || photo.ModifiedUtc != file.LastWriteTimeUtc)
                return null;
            photo.ResetSessionState();
            return photo;
        }
    }

    public void Put(PhotoInfo photo)
    {
        var folder = GetFolder(Path.GetDirectoryName(photo.Path)!);
        lock (folder)
        {
            folder.Items[photo.FileName] = photo;
            folder.Dirty = true;
        }
    }

    /// <summary>폴더를 새로 훑었을 때, 그 폴더에 더 이상 없는 파일의 결과를 버린다.</summary>
    public void Prune(string folderPath, IEnumerable<string> presentNames)
    {
        var present = new HashSet<string>(presentNames, StringComparer.OrdinalIgnoreCase);
        var folder = GetFolder(folderPath);
        lock (folder)
        {
            foreach (var name in folder.Items.Keys.Where(n => !present.Contains(n)).ToList())
            {
                folder.Items.Remove(name);
                folder.Dirty = true;
            }
        }
    }

    /// <summary>2차 분석 결과처럼 사진 내용이 바뀌었을 때 저장 대상으로 표시한다.</summary>
    public void MarkChanged(PhotoInfo photo)
    {
        var folder = GetFolder(Path.GetDirectoryName(photo.Path)!);
        lock (folder) folder.Dirty = true;
    }

    /// <summary>바뀐 폴더만 디스크에 쓴다. 지운 사진은 빼고 쓴다.</summary>
    public void Save()
    {
        List<Folder> dirty;
        lock (_folders) dirty = _folders.Values.Where(f => f.Dirty).ToList();
        foreach (var folder in dirty)
        {
            List<PhotoInfo> items;
            lock (folder)
            {
                foreach (var name in folder.Items.Where(p => p.Value.IsRemoved).Select(p => p.Key).ToList())
                    folder.Items.Remove(name);
                items = folder.Items.Values.ToList();
                folder.Dirty = false;
            }
            try { Write(folder.Path, items); }
            catch { /* 저장을 못 해도 다음에 다시 분석하면 된다 */ }
        }
    }

    /// <summary>오래 쓰지 않은 저장 파일을 지운다.</summary>
    public void CleanUp()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var file in new DirectoryInfo(_dir).EnumerateFiles("*.bin"))
                if (DateTime.UtcNow - file.LastWriteTimeUtc > KeepUnused) file.Delete();
        }
        catch { }
    }

    private Folder GetFolder(string path)
    {
        lock (_folders)
        {
            if (_folders.TryGetValue(path, out var folder)) return folder;
            folder = new Folder(path);
            try { Read(folder); }
            catch { folder.Items.Clear(); }
            _folders[path] = folder;
            return folder;
        }
    }

    private string FileFor(string folderPath)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant()));
        return Path.Combine(_dir, Convert.ToHexString(hash)[..20] + ".bin");
    }

    private void Read(Folder folder)
    {
        string file = FileFor(folder.Path);
        if (!File.Exists(file)) return;
        using var r = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
        if (r.ReadInt32() != FormatVersion) return;
        if (!string.Equals(r.ReadString(), folder.Path, StringComparison.OrdinalIgnoreCase)) return;
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            long size = r.ReadInt64();
            var modified = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            int width = r.ReadInt32(), height = r.ReadInt32();
            DateTime? taken = r.ReadBoolean() ? new DateTime(r.ReadInt64()) : null;
            ulong hash = r.ReadUInt64(), center = r.ReadUInt64();
            var hist = new float[r.ReadInt32()];
            for (int k = 0; k < hist.Length; k++) hist[k] = r.ReadSingle();
            bool detailed = r.ReadBoolean();
            double sharpness = r.ReadDouble();
            var thumb = r.ReadBytes(r.ReadInt32());
            folder.Items[name] = new PhotoInfo
            {
                Path = Path.Combine(folder.Path, name),
                FileSize = size,
                ModifiedUtc = modified,
                Width = width,
                Height = height,
                TakenAt = taken,
                Hash = hash,
                CenterHash = center,
                ColorHistogram = hist,
                IsDetailed = detailed,
                Sharpness = sharpness,
                ThumbnailJpeg = thumb,
            };
        }
    }

    private void Write(string folderPath, List<PhotoInfo> items)
    {
        Directory.CreateDirectory(_dir);
        string file = FileFor(folderPath), temp = file + ".tmp";
        using (var w = new BinaryWriter(File.Create(temp), Encoding.UTF8))
        {
            w.Write(FormatVersion);
            w.Write(folderPath);
            w.Write(items.Count);
            foreach (var p in items)
            {
                w.Write(p.FileName);
                w.Write(p.FileSize);
                w.Write(p.ModifiedUtc.Ticks);
                w.Write(p.Width);
                w.Write(p.Height);
                w.Write(p.TakenAt.HasValue);
                if (p.TakenAt is { } taken) w.Write(taken.Ticks);
                w.Write(p.Hash);
                w.Write(p.CenterHash);
                w.Write(p.ColorHistogram.Length);
                foreach (float v in p.ColorHistogram) w.Write(v);
                w.Write(p.IsDetailed);
                w.Write(p.Sharpness);
                var thumb = p.ThumbnailJpeg;
                w.Write(thumb.Length);
                w.Write(thumb);
            }
        }
        File.Move(temp, file, overwrite: true);
    }
}
