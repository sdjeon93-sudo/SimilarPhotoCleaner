using System.IO;
using System.Runtime.InteropServices;

namespace SimilarPhotoCleaner.Core;

public static class FileRemover
{
    public const string MovedFolderName = "_지운 사진";

    /// <summary>정리하지 못한 파일과 Windows 오류 코드</summary>
    public sealed record Failure(string Path, int ErrorCode);

    /// <summary>휴대폰 연결(Phone Link)로 보이는 휴대폰 저장소의 위치</summary>
    public static readonly string PhoneLinkRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CrossDevice");

    public static bool IsPhoneLinkPath(string path) =>
        path.StartsWith(PhoneLinkRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 클라우드 파일 제공자(휴대폰 연결, OneDrive 등)가 작업을 거부했다는 오류인지.
    /// 0x18B = ERROR_CLOUD_FILE_ACCESS_DENIED, 0x185 = ERROR_CLOUD_FILE_UNSUCCESSFUL
    /// </summary>
    public static bool IsCloudDenied(int code) => (code & 0xFFFF) is 0x18B or 0x185;

    /// <summary>
    /// 휴대폰이 권한 때문에 막은 실패인지. 휴대폰 폴더에서 '다른 프로그램이 사용 중'(0x20, 0x21)이 아닌 실패는
    /// 대부분 'Windows와 연결' 앱의 권한 문제다.
    /// </summary>
    public static bool IsPhonePermissionProblem(Failure f) =>
        IsCloudDenied(f.ErrorCode) || (IsPhoneLinkPath(f.Path) && (f.ErrorCode & 0xFFFF) is not (0x20 or 0x21));

    /// <summary>
    /// 파일들을 휴지통으로 보낸다. 휴지통을 쓸 수 없는 드라이브(네트워크·USB 등)라서 영구 삭제될 상황이면
    /// Windows가 사용자에게 먼저 물어본다. Windows의 오류 창은 띄우지 않고, 실패한 파일은 돌려받아 프로그램이 안내한다.
    /// </summary>
    public static (List<string> Removed, List<Failure> Failed) SendToRecycleBin(IReadOnlyList<string> paths, IntPtr owner)
    {
        if (paths.Count == 0) return ([], []);
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int result = SHFileOperation(ref op);
        var removed = paths.Where(p => !File.Exists(p)).ToList();
        var failed = paths.Where(File.Exists).Select(p => new Failure(p, result)).ToList();
        return (removed, failed);
    }

    /// <summary>
    /// 파일들을 root 아래 '_지운 사진' 폴더로 옮긴다(하위 폴더 구조 유지). 옮긴 (원래 위치, 새 위치) 목록과 실패 목록을 돌려준다.
    /// </summary>
    public static (List<(string From, string To)> Moved, List<Failure> Failed) MoveToFolder(IReadOnlyList<string> paths, string root)
    {
        var moved = new List<(string, string)>();
        var failed = new List<Failure>();
        string movedRoot = Path.Combine(root, MovedFolderName);
        foreach (var path in paths)
        {
            try
            {
                string relative = Path.GetRelativePath(root, path);
                if (relative.StartsWith("..", StringComparison.Ordinal)) relative = Path.GetFileName(path);
                string target = UniquePath(Path.Combine(movedRoot, relative));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(path, target);
                moved.Add((path, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(new Failure(path, ex.HResult));
            }
        }
        // 하나도 못 옮겼으면 만들어 둔 빈 폴더를 치운다(지울 수 없는 곳이면 그대로 둔다).
        if (moved.Count == 0 && Directory.Exists(movedRoot))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(movedRoot, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                if (!Directory.EnumerateFileSystemEntries(movedRoot).Any()) Directory.Delete(movedRoot);
            }
            catch { }
        }
        return (moved, failed);
    }

    /// <summary>MoveToFolder로 옮긴 파일을 원래 자리로 되돌린다. 되돌린 원래 경로 목록을 돌려준다.</summary>
    public static List<string> MoveBack(IEnumerable<(string From, string To)> moves, string root)
    {
        var restored = new List<string>();
        foreach (var (from, to) in moves)
        {
            try
            {
                if (!File.Exists(to) || File.Exists(from)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                File.Move(to, from);
                restored.Add(from);
                DeleteEmptyFolders(Path.GetDirectoryName(to)!, Path.Combine(root, MovedFolderName));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return restored;
    }

    public static bool IsInsideMovedFolder(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar)
            .Any(part => part.Equals(MovedFolderName, StringComparison.OrdinalIgnoreCase));
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void DeleteEmptyFolders(string dir, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        while (current.StartsWith(stop, StringComparison.OrdinalIgnoreCase) && Directory.Exists(current)
               && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            if (current.Equals(stop, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
