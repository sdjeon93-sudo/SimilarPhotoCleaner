using System.Collections.ObjectModel;
using System.IO;
using SimilarPhotoCleaner.Core;

namespace SimilarPhotoCleaner.ViewModels;

/// <summary>주소 표시줄의 경로 조각 하나</summary>
public sealed record Crumb(string Name, string Path, bool IsFirst, bool IsLast);

/// <summary>왼쪽 폴더 목록(탐색기처럼)의 폴더 하나. 펼칠 때 하위 폴더를 읽고, 폴더 안 사진 수를 뒤에서 센다.</summary>
public sealed class FolderNode : ObservableObject
{
    public const string FolderGlyph = "";
    public const string PhoneGlyph = "";
    public const string PicturesGlyph = "";
    public const string DesktopGlyph = "";
    public const string DownloadGlyph = "";
    public const string DriveGlyph = "";
    public const string CloudGlyph = "";

    // 휴대폰 연결 폴더처럼 느린 곳에서 한꺼번에 너무 많이 세지 않도록 제한한다.
    private static readonly SemaphoreSlim CountGate = new(4);

    private bool _isExpanded, _isSelected;
    private int _imageCount = -1;
    private Task? _loadTask;

    public FolderNode(string name, string path, string icon, bool countImages = true)
    {
        Name = name;
        Path = path;
        Icon = icon;
        CountImagesOnLoad = countImages;
        Children.Add(new FolderNode()); // 펼침 화살표를 보이게 하는 자리표시
    }

    private FolderNode()
    {
        Name = "불러오는 중…";
        Path = "";
        Icon = "";
        IsPlaceholder = true;
    }

    public string Name { get; }
    public string Path { get; }
    public string Icon { get; }
    public bool IsPlaceholder { get; }
    public bool CountImagesOnLoad { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];

    /// <summary>폴더 바로 안에 있는 사진 수(하위 폴더 제외). 아직 모르면 빈칸.</summary>
    public string CountText => _imageCount > 0 ? $"{_imageCount:N0}" : "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value) _ = LoadChildrenAsync();
        }
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>하위 폴더를 한 번만 읽는다. 이미 읽는 중이면 그 작업을 기다린다. UI 스레드에서 부른다.</summary>
    public Task LoadChildrenAsync() => IsPlaceholder ? Task.CompletedTask : _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        List<DirectoryInfo> dirs;
        try { dirs = await Task.Run(() => ListSubfolders(Path)); }
        catch { dirs = []; }
        Children.Clear();
        foreach (var dir in dirs) Children.Add(new FolderNode(dir.Name, dir.FullName, FolderGlyph));
        foreach (var child in Children.ToList()) _ = child.CountImagesAsync();
    }

    public async Task CountImagesAsync()
    {
        if (IsPlaceholder || !CountImagesOnLoad || _imageCount >= 0) return;
        await CountGate.WaitAsync();
        try
        {
            int count = await Task.Run(() => CountImages(Path));
            _imageCount = count;
            Notify(nameof(CountText));
        }
        catch { }
        finally
        {
            CountGate.Release();
        }
    }

    public static bool IsSameOrUnder(string path, string root)
    {
        string p = path.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        string r = root.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static List<DirectoryInfo> ListSubfolders(string path)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        return new DirectoryInfo(path).EnumerateDirectories("*", options)
            // '.'으로 시작하는 숨김 폴더(안드로이드), '$'로 시작하는 Windows 시스템 폴더, 지운 사진 폴더는 뺀다.
            .Where(d => !d.Name.StartsWith('.') && !d.Name.StartsWith('$') &&
                        !d.Name.Equals(FileRemover.MovedFolderName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static int CountImages(string path)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        return Directory.EnumerateFiles(path, "*", options).Count(ImageLoader.IsSupported);
    }
}
