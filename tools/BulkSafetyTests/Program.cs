using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SimilarPhotoCleaner;
using SimilarPhotoCleaner.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
        _checks++;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // Only generated files are moved. Keep the unique directory for inspection.
        string root = Path.Combine(Path.GetTempPath(), "BulkSafety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Put(string path, string text = "fixture")
        {
            string full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Put("a.jpg"); Put("b.PNG"); Put("note.txt"); Put("clip.mp4");
        Put("child/c.heic"); Put("_지운 사진/old.jpg"); Put("child/_지운 사진/old.jpg");
        Put("hidden.jpg"); File.SetAttributes(Path.Combine(root, "hidden.jpg"), FileAttributes.Hidden);
        var direct = BulkPhotoRemoval.Collect(root, false);
        Check(direct.Count == 2, "default excludes children, recovery folders, documents and hidden files");
        Check(BulkPhotoRemoval.Collect(root, true).Count == 3, "recursive excludes all recovery folders");
        Check(direct.Any(i => i.RelativePath == "a.jpg"), "unreadable/unmatched photos included by extension");
        var app = new App(); app.InitializeComponent();
        var dialog = new BulkDeleteWindow(root);
        var confirm = (Button)dialog.FindName("ConfirmButton");
        var input = (TextBox)dialog.FindName("FolderNameInput");
        var ack = (CheckBox)dialog.FindName("Acknowledged");
        typeof(BulkDeleteWindow).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, direct);
        Check(!confirm.IsEnabled && !confirm.IsDefault, "no initial or Enter-default destructive action");
        input.Text = Path.GetFileName(root);
        Check(!confirm.IsEnabled, "name alone is insufficient");
        ack.IsChecked = true; ack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(confirm.IsEnabled, "exact name plus explicit acknowledgment enables action");
        input.Text += "wrong";
        Check(!confirm.IsEnabled, "wrong name blocks action");
        if (args.Contains("--preview")) { dialog.ShowDialog(); return; }
        dialog.Close();

        Put("a.jpg", "changed after review");
        Put("new.jpg");
        Put("_지운 사진/b.PNG", "existing recovery copy");
        var result = BulkPhotoRemoval.Move(root, direct);
        Check(result.Moved.Count == 1 && result.Failed.Count == 1, "changed files skipped and partial success recorded");
        Check(File.Exists(Path.Combine(root, "new.jpg")), "new files after confirmation are not added");
        Check(File.ReadAllText(Path.Combine(root, "_지운 사진/b.PNG")) == "existing recovery copy", "recovery name collisions never overwrite");
        Check(File.Exists(Path.Combine(root, "note.txt")) && File.Exists(Path.Combine(root, "clip.mp4")), "non-photo files remain untouched");
        var restored = FileRemover.MoveBack(result.Moved.Select(m => (m.Item.Path, m.Destination)), root);
        Check(restored.Count == 1 && File.ReadAllText(Path.Combine(root, "b.PNG")) == "fixture", "undo restores original contents");
        string outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid() + ".jpg");
        File.WriteAllText(outside, "outside");
        var external = new FileInfo(outside);
        var blocked = BulkPhotoRemoval.Move(root, [new(outside, "../outside.jpg", external.Length, external.LastWriteTimeUtc)]);
        Check(blocked.Moved.Count == 0 && File.Exists(outside), "out-of-scope path rejected");
        bool rejected = false;
        try { BulkPhotoRemoval.Collect(Path.GetPathRoot(root)!, false); } catch (IOException) { rejected = true; }
        Check(rejected, "drive-wide operation rejected");
        rejected = false;
        try { BulkPhotoRemoval.Collect(Path.Combine(root, "_지운 사진"), false); } catch (IOException) { rejected = true; }
        Check(rejected, "recovery folder cannot be purged by bulk action");
        // Restore conflicts leave the recovery copy intact and can be retried.
        result = BulkPhotoRemoval.Move(root, BulkPhotoRemoval.Collect(root, false));
        Put("b.PNG", "new original");
        restored = FileRemover.MoveBack(result.Moved.Select(m => (m.Item.Path, m.Destination)), root);
        Check(restored.Count == result.Moved.Count - 1 && File.ReadAllText(Path.Combine(root, "b.PNG")) == "new original" &&
            File.Exists(result.Moved.Single(m => m.Item.RelativePath == "b.PNG").Destination), "undo collision preserves both files");
        Console.WriteLine($"{_checks} checks passed. Fixtures: {root}");
    }

}
