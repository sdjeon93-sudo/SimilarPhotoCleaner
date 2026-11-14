using System.ComponentModel;
using System.IO;
using System.Windows;
using SimilarPhotoCleaner.Core;
using SimilarPhotoCleaner.ViewModels;

namespace SimilarPhotoCleaner;

public partial class BulkDeleteWindow : Window
{
    private readonly string _root;
    private readonly string _folderName;
    private List<BulkPhotoRemoval.Item> _items = [];
    private bool _busy;
    public BulkPhotoRemoval.Result? Result { get; private set; }

    public BulkDeleteWindow(string root)
    {
        _root = root;
        _folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        RootText.Text = root;
        NamePrompt.Text = $"계속하려면 폴더 이름 ‘{_folderName}’을 정확히 입력하세요.";
        Loaded += async (_, _) => await LoadTargets();
    }

    private async Task LoadTargets()
    {
        _busy = true;
        IncludeChildren.IsEnabled = false;
        _items = [];
        FileList.ItemsSource = null;
        FolderNameInput.Clear();
        Acknowledged.IsChecked = false;
        UpdateConfirmation();
        SummaryText.Text = "대상 사진을 확인하는 중…";
        try
        {
            bool recursive = IncludeChildren.IsChecked == true;
            _items = await Task.Run(() => BulkPhotoRemoval.Collect(_root, recursive));
            FileList.ItemsSource = _items;
            SummaryText.Text = $"{(recursive ? "하위 폴더 포함" : "현재 폴더만")} · 사진 {_items.Count:N0}장 · {Format.Size(_items.Sum(i => i.Length))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = "목록을 안전하게 확인하지 못해 실행할 수 없어요. " + ex.Message;
        }
        finally
        {
            _busy = false;
            IncludeChildren.IsEnabled = true;
            UpdateConfirmation();
        }
    }

    private void UpdateConfirmation()
    {
        if (ConfirmButton == null) return;
        ConfirmButton.IsEnabled = !_busy && _items.Count > 0 && Acknowledged.IsChecked == true &&
            string.Equals(FolderNameInput.Text, _folderName, StringComparison.Ordinal);
    }

    private async void Scope_Changed(object sender, RoutedEventArgs e) => await LoadTargets();
    private void Confirmation_Changed(object sender, RoutedEventArgs e) => UpdateConfirmation();

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled || _busy) return;
        _busy = true;
        UpdateConfirmation();
        IncludeChildren.IsEnabled = Acknowledged.IsEnabled = FolderNameInput.IsEnabled = CancelButton.IsEnabled = false;
        SummaryText.Text = "복구할 수 있도록 사진을 옮기는 중… 창을 닫지 마세요.";
        try
        {
            Result = await Task.Run(() => BulkPhotoRemoval.Move(_root, _items));
            _busy = false;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = "실행하지 못했어요. " + ex.Message;
            _items = [];
        }
        finally
        {
            _busy = false;
            IncludeChildren.IsEnabled = Acknowledged.IsEnabled = FolderNameInput.IsEnabled = CancelButton.IsEnabled = true;
            UpdateConfirmation();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) { if (_busy) e.Cancel = true; }
}
