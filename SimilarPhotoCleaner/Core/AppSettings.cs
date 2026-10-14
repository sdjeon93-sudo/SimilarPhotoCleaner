using System.IO;
using System.Text.Json;

namespace SimilarPhotoCleaner.Core;

public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public bool IncludeSubfolders { get; set; } = true;
    public int Level { get; set; } = 3;
    /// <summary>0 = 휴지통, 1 = '_지운 사진' 폴더로 옮기기</summary>
    public int RemoveMode { get; set; }
    /// <summary>왼쪽 폴더 목록을 보일지</summary>
    public bool ShowFolderPane { get; set; } = true;
    /// <summary>마지막으로 새 버전을 확인한 때(자동 확인은 12시간에 한 번)</summary>
    public DateTime? LastUpdateCheck { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimilarPhotoCleaner", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
