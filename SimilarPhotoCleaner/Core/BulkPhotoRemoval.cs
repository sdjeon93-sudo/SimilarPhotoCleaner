using System.IO;

namespace SimilarPhotoCleaner.Core;

/// <summary>A fixed, reviewable list. Bulk removal never uses recycle/USB permanent deletion.</summary>
public static class BulkPhotoRemoval
{
    public sealed record Item(string Path, string RelativePath, long Length, DateTime ModifiedUtc);
    public sealed record Result(List<(Item Item, string Destination)> Moved, List<string> Failed);

    private static string ValidateRoot(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetPathRoot(root) == root || !Directory.Exists(root) ||
            root.Split(Path.DirectorySeparatorChar).Any(p => p.Equals(FileRemover.MovedFolderName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("드라이브 전체 또는 '_지운 사진' 폴더에서는 일괄 지우기를 사용할 수 없어요.");
        CheckDirectoryChain(root);
        return root;
    }

    private static void CheckDirectoryChain(string directory)
    {
        for (var d = new DirectoryInfo(directory); d != null; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결 폴더에서는 안전을 위해 일괄 지우기를 사용할 수 없어요.");
    }

    public static List<Item> Collect(string root, bool recursive)
    {
        root = ValidateRoot(root);
        var items = new List<Item>();
        var folders = new Stack<string>();
        folders.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        while (folders.TryPop(out var folder))
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                if (ImageLoader.IsSupported(file.Name))
                    items.Add(new(file.FullName, Path.GetRelativePath(root, file.FullName), file.Length, file.LastWriteTimeUtc));
            if (recursive)
                foreach (var child in Directory.EnumerateDirectories(folder, "*", options))
                    if (!Path.GetFileName(child).Equals(FileRemover.MovedFolderName, StringComparison.OrdinalIgnoreCase)) folders.Push(child);
        }
        return items.OrderBy(i => i.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static Result Move(string root, IReadOnlyList<Item> items)
    {
        root = ValidateRoot(root);
        var moved = new List<(Item, string)>();
        var failed = new List<string>();
        foreach (var item in items)
        {
            try
            {
                string relative = Path.GetRelativePath(root, item.Path);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ||
                    FileRemover.IsInsideMovedFolder(root, item.Path) || !ImageLoader.IsSupported(item.Path))
                    throw new IOException("범위를 벗어난 파일");
                CheckDirectoryChain(Path.GetDirectoryName(item.Path)!);
                var current = new FileInfo(item.Path);
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    current.Length != item.Length || current.LastWriteTimeUtc != item.ModifiedUtc)
                    throw new IOException("확인 이후 변경된 파일");
                // Never follow a linked recovery folder, including an existing nested destination.
                string destination = Path.Combine(root, FileRemover.MovedFolderName, relative);
                for (var d = new DirectoryInfo(Path.GetDirectoryName(destination)!); d != null; d = d.Parent)
                    if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("연결된 복구 폴더");
                var result = FileRemover.MoveToFolder([item.Path], root);
                if (result.Moved.Count == 1) moved.Add((item, result.Moved[0].To));
                else failed.Add(item.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(item.RelativePath);
            }
        }
        return new(moved, failed);
    }
}
