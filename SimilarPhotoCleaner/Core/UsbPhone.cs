using System.IO;
using MediaDevices;

namespace SimilarPhotoCleaner.Core;

/// <summary>
/// USB 케이블로 연결한 휴대폰(MTP, '파일 전송' 모드). Windows의 휴대용 장치(WPD) API를 MediaDevices 2 라이브러리로 쓴다.
/// 휴대폰 연결(Phone Link)과 달리 카메라로 찍은 사진도 지울 수 있다(휴대폰 휴지통을 거치지 않는 영구 삭제).
/// MediaDevices 2는 스레드에 안전하므로 백그라운드 작업에서 바로 부른다.
/// </summary>
public sealed class UsbPhone : IDisposable
{
    public sealed record Info(string Id, string Name);
    public sealed record RemoteFile(string Path, long Size, DateTime? Modified);

    /// <summary>휴대폰 사진을 받아 두는 곳: %LocalAppData%\SimilarPhotoCleaner\usb\(기기 이름)</summary>
    public static string MirrorBase => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimilarPhotoCleaner", "usb");

    private readonly MediaDevice _device;

    public string Id { get; }
    public string Name { get; }
    public string MirrorRoot => Path.Combine(MirrorBase, SafeName(Name));

    private UsbPhone(MediaDevice device, string id, string name)
    {
        _device = device;
        Id = id;
        Name = name;
    }

    /// <summary>연결된 휴대폰(저장 공간을 열어 준 휴대용 장치) 목록. '충전 전용'이면 저장 공간이 없어 빠진다.</summary>
    public static Task<List<Info>> ListAsync() => Task.Run(() =>
    {
        var list = new List<Info>();
        foreach (var device in MediaDeviceManager.Instance.GetDevices() ?? [])
        {
            try
            {
                device.Connect();
                try
                {
                    if (device.GetDirectories(@"\").Length > 0) list.Add(new Info(device.DeviceId, NameOf(device)));
                }
                finally
                {
                    device.Disconnect();
                }
            }
            catch { /* 잠긴 휴대폰, 다른 프로그램이 쓰는 중 등 */ }
        }
        return list;
    });

    public static Task<UsbPhone?> OpenAsync(string id) => Task.Run(() =>
    {
        try
        {
            var device = (MediaDeviceManager.Instance.GetDevices() ?? []).FirstOrDefault(d => d.DeviceId == id);
            if (device == null) return null;
            // 파일 목록 캐시를 켜면 큰 폴더(사진 수천 장)에서 경로로 파일을 찾는 시간이 줄어든다.
            device.Connect(MediaDeviceAccess.Default, MediaDeviceShare.Default, true);
            return new UsbPhone(device, id, NameOf(device));
        }
        catch
        {
            return null;
        }
    });

    public bool IsConnected
    {
        get
        {
            try { return _device.IsConnected; }
            catch { return false; }
        }
    }

    /// <summary>하위 폴더의 전체 경로들(예: \내장 저장공간\DCIM)</summary>
    public Task<List<string>> GetFoldersAsync(string path) => Task.Run(() =>
        _device.GetDirectories(path)
            .Where(p => !Path.GetFileName(p).StartsWith('.') &&
                        !Path.GetFileName(p).Equals(FileRemover.MovedFolderName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

    /// <summary>폴더 안의 사진 파일(하위 폴더 포함 여부 선택)</summary>
    public Task<List<RemoteFile>> GetImagesAsync(string path, bool recursive, CancellationToken token) => Task.Run(() =>
    {
        var result = new List<RemoteFile>();
        var folders = new Stack<string>();
        folders.Push(path);
        while (folders.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var info = _device.GetDirectoryInfo(folders.Pop());
            foreach (var file in info.EnumerateFiles())
            {
                if (!ImageLoader.IsSupported(file.Name)) continue;
                result.Add(new RemoteFile(file.FullName, Convert.ToInt64(file.Length), file.LastWriteTime));
            }
            if (!recursive) continue;
            foreach (var dir in info.EnumerateDirectories())
                if (!dir.Name.StartsWith('.') && !dir.Name.Equals(FileRemover.MovedFolderName, StringComparison.OrdinalIgnoreCase))
                    folders.Push(dir.FullName);
        }
        return result;
    }, token);

    /// <summary>휴대폰 파일을 PC로 받는다(다 받은 뒤에 이름을 바꿔 반쯤 받은 파일이 남지 않게).</summary>
    public async Task DownloadAsync(string devicePath, string localPath, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        string part = localPath + ".part";
        await using (var output = File.Create(part))
            await _device.DownloadFileAsync(devicePath, output, token);
        File.Move(part, localPath, overwrite: true);
    }

    /// <summary>
    /// 파일 앞부분만 읽는다(내장 썸네일로 빠르게 분석하기 위해). 파일 전체를 받지 않고 필요한 만큼 읽은 뒤 멈춘다.
    /// </summary>
    public Task<byte[]> ReadHeadAsync(string devicePath, int size) => Task.Run(() =>
    {
        var info = _device.GetFileInfo(devicePath);
        using var stream = _device.OpenReadFromPersistentUniqueId(info.PersistentUniqueId)
                           ?? throw new IOException("휴대폰 파일을 열 수 없어요.");
        var buffer = new byte[size];
        int read = 0;
        while (read < size)
        {
            int n = stream.Read(buffer, read, size - read);
            if (n == 0) break;
            read += n;
        }
        return read == size ? buffer : buffer[..read];
    });

    /// <summary>파일 크기. 없으면 null.</summary>
    public Task<long?> GetSizeAsync(string devicePath) => Task.Run(() =>
    {
        try { return _device.FileExists(devicePath) ? (long?)Convert.ToInt64(_device.GetFileInfo(devicePath).Length) : null; }
        catch { return null; }
    });

    /// <summary>휴대폰에서 파일을 지운다(영구 삭제).</summary>
    public Task DeleteAsync(string devicePath) => Task.Run(() => _device.DeleteFile(devicePath));

    /// <summary>PC 파일을 휴대폰으로 보낸다(시험용).</summary>
    public Task UploadAsync(string localPath, string devicePath) => Task.Run(() =>
    {
        string? folder = Path.GetDirectoryName(devicePath);
        if (folder != null && !_device.DirectoryExists(folder)) _device.CreateDirectory(folder);
        _device.UploadFile(localPath, devicePath);
    });

    /// <summary>휴대폰의 빈 폴더(또는 폴더 전체)를 지운다(시험 뒷정리용).</summary>
    public Task DeleteFolderAsync(string devicePath, bool recursive) => Task.Run(() =>
    {
        if (_device.DirectoryExists(devicePath)) _device.DeleteDirectory(devicePath, recursive);
    });

    // ── 휴대폰 연결(Phone Link) 경로 ↔ USB 경로 ──

    /// <summary>
    /// 휴대폰 연결 경로(…\CrossDevice\기기 이름\storage\DCIM\Camera\a.jpg)에서 기기 이름과 저장 공간 안의 경로(DCIM\Camera\a.jpg)를 얻는다.
    /// </summary>
    public static (string Device, string Relative)? ParsePhoneLinkPath(string path)
    {
        if (!FileRemover.IsPhoneLinkPath(path)) return null;
        var parts = path[(FileRemover.PhoneLinkRoot.Length + 1)..].Split(Path.DirectorySeparatorChar, 3);
        if (parts.Length < 3 || !parts[1].Equals("storage", StringComparison.OrdinalIgnoreCase)) return null;
        return (parts[0], parts[2]);
    }

    public sealed record PhoneLinkItem(string LinkPath, string Relative, long Size);

    /// <summary>
    /// 휴대폰 연결로 본 파일들을 USB로 지운다. 폴더마다 목록을 한 번만 읽어 이름과 크기가 같은 파일을 찾고,
    /// 찾은 파일들을 객체 ID로 한꺼번에 지운다. 지운 파일의 휴대폰 연결 경로 목록을 돌려준다.
    /// </summary>
    public Task<HashSet<string>> DeletePhoneLinkFilesAsync(IReadOnlyList<PhoneLinkItem> items, IProgress<string>? progress) => Task.Run(() =>
    {
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storages = _device.GetDirectories(@"\");
        var targets = new Dictionary<string, string>(); // 객체 ID → 휴대폰 연결 경로
        foreach (var folder in items.GroupBy(i => Path.GetDirectoryName(i.Relative) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            progress?.Report($"휴대폰의 '{folder.Key}' 폴더에서 사진을 찾는 중… (사진이 많으면 1분쯤 걸려요)");
            string? devicePath = storages.Select(s => s + Path.DirectorySeparatorChar + folder.Key).FirstOrDefault(_device.DirectoryExists);
            if (devicePath == null) continue;
            var files = new Dictionary<string, (string Id, long Size)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in _device.GetDirectoryInfo(devicePath).EnumerateFiles())
                files[file.Name] = (file.Id, Convert.ToInt64(file.Length));
            foreach (var item in folder)
            {
                // 같은 이름에 크기까지 같아야 같은 사진으로 본다.
                if (files.TryGetValue(Path.GetFileName(item.Relative), out var f) && f.Size == item.Size)
                    targets[f.Id] = item.LinkPath;
            }
        }

        progress?.Report($"휴대폰에서 사진 {targets.Count}장을 지우는 중…");
        foreach (var (id, ok) in WpdDelete.Delete(_device.DeviceId, targets.Keys.ToList()))
            if (ok) deleted.Add(targets[id]);
        return deleted;
    });

    /// <summary>
    /// 휴대폰 연결로 본 파일이 이 USB 휴대폰의 어느 파일인지 찾는다. 저장 공간마다 같은 경로를 보고, 크기까지 같아야 같은 파일로 본다.
    /// </summary>
    public async Task<string?> FindPhoneLinkFileAsync(string relative, long size)
    {
        foreach (var storage in await GetStoragesAsync())
        {
            string candidate = storage + Path.DirectorySeparatorChar + relative;
            if (await GetSizeAsync(candidate) == size) return candidate;
        }
        return null;
    }

    /// <summary>저장 공간(내장 저장공간, SD 카드 등)의 경로들</summary>
    public Task<List<string>> GetStoragesAsync() => GetFoldersAsync(@"\");

    // ── 받아 둔 사진(미러) 경로 ↔ 휴대폰 경로 ──

    public string ToMirror(string devicePath) => MirrorRoot + devicePath;

    public static bool IsMirrorPath(string path) =>
        path.StartsWith(MirrorBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>받아 둔 사진 경로에서 (기기 폴더 이름, 휴대폰 경로)를 얻는다.</summary>
    public static (string Device, string DevicePath)? FromMirror(string path)
    {
        if (!IsMirrorPath(path)) return null;
        string rest = path[(MirrorBase.Length + 1)..];
        int slash = rest.IndexOf(Path.DirectorySeparatorChar);
        return slash < 0 ? (rest, @"\") : (rest[..slash], rest[slash..]);
    }

    public static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private static string NameOf(MediaDevice device)
    {
        string name = (!string.IsNullOrWhiteSpace(device.FriendlyName) ? device.FriendlyName : device.Description) ?? "";
        return string.IsNullOrWhiteSpace(name) ? "휴대폰" : name.Trim();
    }

    public void Dispose()
    {
        try { _device.Disconnect(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
