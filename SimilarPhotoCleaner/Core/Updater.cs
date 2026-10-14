using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SimilarPhotoCleaner.Core;

/// <summary>
/// GitHub 릴리스로 자동 업데이트한다.
/// 릴리스 태그는 'v1.4.0'처럼 버전을, 첨부 파일은 'SimilarPhotoCleaner.exe' 하나를 올린다.
/// 새 exe를 실행 파일과 같은 폴더에 받아 두고, 실행 중인 exe는 이름만 '.old.exe'로 바꿔 비켜 둔 뒤 새 파일로 바꿔 다시 시작한다.
/// </summary>
public static class Updater
{
    public const string Owner = "sdjeon93-sudo";
    public const string Repo = "SimilarPhotoCleaner";
    public const string AssetName = "SimilarPhotoCleaner.exe";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";

    public sealed record Release(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256, string PageUrl);

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AssetName);
    private static string OldExePath => Path.ChangeExtension(ExePath, ".old.exe");
    private static string NewExePath => Path.ChangeExtension(ExePath, ".new.exe");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SimilarPhotoCleaner/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>개발 중 빌드 폴더에서 실행할 때는 업데이트하지 않는다.</summary>
    public static bool IsEnabled => !ExePath.Contains("SimilarPhotoCleaner-build", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 최신 릴리스를 확인한다. Ok = 확인에 성공했는지(인터넷 없음 등이면 false), Newer = 지금보다 새 버전(없으면 null).
    /// </summary>
    public static async Task<(bool Ok, Release? Newer)> CheckAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (true, null); // 아직 릴리스가 없음
            if (!response.IsSuccessStatusCode) return (false, null);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return (true, ParseRelease(doc.RootElement));
        }
        catch
        {
            return (false, null);
        }
    }

    private static Release? ParseRelease(JsonElement root)
    {
        {
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var version) || version <= CurrentVersion) return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d &&
                              d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
                return new Release(version, tag,
                    root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.GetProperty("size").GetInt64(),
                    sha,
                    root.GetProperty("html_url").GetString() ?? ReleasesPage);
            }
            return null; // 아직 exe를 첨부하지 않은 릴리스
        }
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>
    /// 새 exe를 받아 실행 중인 exe와 바꾼다. 성공하면 새 exe의 경로를 돌려준다(호출한 쪽이 실행하고 종료한다).
    /// 실패하면 예외를 던지고, 원래 exe는 그대로 둔다.
    /// </summary>
    public static async Task<string> DownloadAndSwapAsync(Release release, IProgress<double>? progress, CancellationToken token = default)
    {
        string exe = ExePath, newExe = NewExePath, oldExe = OldExePath;

        // 1) 같은 폴더에 새 파일로 받는다(같은 드라이브여야 이름 바꾸기로 교체할 수 있다).
        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), token);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        // 2) 받은 파일이 맞는지 확인한다.
        var info = new FileInfo(newExe);
        if (release.Size > 0 && info.Length != release.Size)
        {
            File.Delete(newExe);
            throw new IOException("받은 파일의 크기가 맞지 않아요. 인터넷 연결을 확인하고 다시 시도해 주세요.");
        }
        if (release.Sha256 != null)
        {
            await using var stream = File.OpenRead(newExe);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                stream.Close();
                File.Delete(newExe);
                throw new IOException("받은 파일이 손상됐어요. 다시 시도해 주세요.");
            }
        }

        // 3) 실행 중인 exe는 이름만 바꿔 비켜 두고(실행 중에도 이름은 바꿀 수 있다), 새 파일을 그 자리에 둔다.
        if (File.Exists(oldExe)) File.Delete(oldExe);
        File.Move(exe, oldExe);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(oldExe, exe); // 되돌리기
            throw;
        }
        return exe;
    }

    /// <summary>새 exe를 실행한다. 호출한 쪽은 곧바로 종료해야 한다.</summary>
    public static void Restart(string exe) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });

    /// <summary>지난 업데이트에서 비켜 둔 예전 exe와 받다 만 파일을 지운다. 시작할 때 부른다.</summary>
    public static void CleanUpLeftovers()
    {
        foreach (var path in new[] { OldExePath, NewExePath })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* 아직 실행 중이면 다음에 지운다 */ }
        }
    }
}
