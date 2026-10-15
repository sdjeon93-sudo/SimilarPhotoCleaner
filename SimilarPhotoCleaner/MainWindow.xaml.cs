using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SimilarPhotoCleaner.Core;
using SimilarPhotoCleaner.ViewModels;

namespace SimilarPhotoCleaner;

public partial class MainWindow : Window
{
    private const string DefaultHint =
        "← → 사진 넘기기(보고 있는 사진을 남김) · 사진의 ✓ 단추나 Space: 여러 장 남기기 · Enter 선택 완료 · S 나중에 · ↑ ↓ 묶음 이동 · 휠 확대";

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AnalysisCache _cache = new();
    private readonly ObservableCollection<PhotoGroupView> _groups = [];
    private readonly ObservableCollection<FolderNode> _folderRoots = [];
    private readonly Dictionary<PhotoInfo, (PhotoGroupView Group, PhotoCard Card)> _cardOf = [];
    private readonly PreviewCache _previews = new(capacity: 8, minLongSide: 1800);
    private readonly Stack<UndoEntry> _undo = new();
    private readonly ScaleTransform _zoom = new();
    private readonly TranslateTransform _pan = new();
    private readonly bool _ready;

    private string? _root;
    private List<PhotoInfo> _photos = [];
    private PhotoGroupView? _current;
    private PhotoCard? _focused;
    private CancellationTokenSource? _scanCts;
    private int _previewVersion, _regroupVersion;
    private int _removedCount;
    private long _removedBytes;
    private string _scanNote = "";
    private Point? _panStart;
    private Point _panOrigin;
    private Action? _infoPrimary, _infoSecondary;
    private DispatcherTimer? _hintTimer;
    private bool _closeResolved;
    private bool _suppressTree;

    // 2차 분석(묶인 사진의 선명도·큰 썸네일): 보고 있는 묶음부터 뒤에서 처리한다.
    private readonly object _detailGate = new();
    private readonly SemaphoreSlim _detailSignal = new(0);
    private List<PhotoInfo> _detailQueue = [];
    private CancellationTokenSource _detailCts = new();
    private static readonly int DetailWorkers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    private sealed record UndoEntry(List<PhotoGroupView> Groups, List<(PhotoCard Card, string From, string To)> Moves);

    public MainWindow(string? startFolder)
    {
        InitializeComponent();
        // 노트북(1920×1080, 배율 125%)처럼 화면이 창보다 작으면 아래쪽 정리 버튼이 화면 밖으로 나가지 않게 줄인다.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        GroupList.ItemsSource = _groups;
        FolderTree.ItemsSource = _folderRoots;
        PreviewImage.RenderTransform = new TransformGroup { Children = { _zoom, _pan } };
        HintText.Text = DefaultHint;

        LevelSlider.Value = Math.Clamp(_settings.Level, Grouper.MinLevel, Grouper.MaxLevel);
        SubfoldersCheck.IsChecked = _settings.IncludeSubfolders;
        RemoveModeCombo.SelectedIndex = Math.Clamp(_settings.RemoveMode, 0, 1);
        ApplyFolderPaneVisibility();
        SetPreviewChrome(false);
        _ready = true;

        Task.Run(_cache.CleanUp);
        var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        saveTimer.Tick += (_, _) => Task.Run(_cache.Save);
        saveTimer.Start();

        VersionText.Text = $"버전 {Updater.CurrentVersion.ToString(3)}";
        Loaded += async (_, _) =>
        {
            Updater.CleanUpLeftovers();
            if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder)) StartScan(startFolder);
            _ = AutoCheckUpdateAsync();
            await LoadFolderRootsAsync();
        };
    }

    // ───────────────────────── 자동 업데이트 ─────────────────────────

    private Updater.Release? _newRelease;
    private bool _updating;

    /// <summary>시작하고 몇 초 뒤, 12시간에 한 번만 새 버전을 확인한다.</summary>
    private async Task AutoCheckUpdateAsync()
    {
        if (!Updater.IsEnabled) return;
        if (_settings.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(12)) return;
        await Task.Delay(TimeSpan.FromSeconds(3));
        await CheckUpdateAsync(manual: false);
    }

    private async Task CheckUpdateAsync(bool manual)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var (ok, release) = await Updater.CheckAsync();
            if (ok)
            {
                _settings.LastUpdateCheck = DateTime.Now;
                _settings.Save();
            }
            if (release != null)
            {
                _newRelease = release;
                UpdateText.Text = UpdateBannerText(release);
                UpdateBanner.Visibility = Visibility.Visible;
            }
            else if (manual)
            {
                FlashHint(ok ? $"지금 쓰는 버전({Updater.CurrentVersion.ToString(3)})이 최신이에요."
                             : "새 버전을 확인하지 못했어요. 인터넷 연결을 확인해 주세요.", error: !ok);
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    // 숫자 뒤 조사(이/가, 으로/로)가 버전마다 달라지지 않게 버전 번호를 괄호나 '버전' 앞에 둔다.
    private static string UpdateBannerText(Updater.Release release) =>
        $"새 버전이 나왔어요: {release.Version.ToString(3)} (지금 쓰는 버전 {Updater.CurrentVersion.ToString(3)})";

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!Updater.IsEnabled)
        {
            FlashHint("개발용 빌드에서는 업데이트하지 않아요.", error: false);
            return;
        }
        _ = CheckUpdateAsync(manual: true);
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_newRelease is not { } release) return;
        string notes = release.Notes.Trim();
        if (notes.Length > 900) notes = notes[..900] + "…";
        ShowInfo($"{release.Version.ToString(3)} 버전으로 업데이트할까요?",
            (notes.Length > 0 ? notes + "\n\n" : "") + "업데이트하면 프로그램이 잠깐 닫혔다가 새 버전으로 다시 열려요.",
            "지금 업데이트", () => _ = RunUpdateAsync(release), "나중에", null);
    }

    private async Task RunUpdateAsync(Updater.Release release)
    {
        if (_updating || !ResolvePendingSelections("업데이트하기")) return;
        _updating = true;
        _scanCts?.Cancel();
        UpdateNowButton.IsEnabled = false;
        UpdateLaterButton.IsEnabled = false;
        string version = release.Version.ToString(3);
        var progress = new Progress<double>(p => UpdateText.Text = $"새 버전({version}) 받는 중… {p:P0}");
        try
        {
            string exe = await Updater.DownloadAndSwapAsync(release, progress);
            UpdateText.Text = "업데이트를 마쳤어요. 새 버전으로 다시 엽니다…";
            _closeResolved = true;
            Updater.Restart(exe);
            Close();
        }
        catch (Exception ex)
        {
            _updating = false;
            UpdateNowButton.IsEnabled = true;
            UpdateLaterButton.IsEnabled = true;
            UpdateText.Text = UpdateBannerText(release);
            string reason = ex switch
            {
                UnauthorizedAccessException => "프로그램이 있는 폴더에 새 파일을 저장할 수 없어요(쓰기 권한 없음).",
                HttpRequestException => "인터넷에서 새 버전을 받지 못했어요. 연결을 확인해 주세요.",
                _ => ex.Message,
            };
            ShowInfo("업데이트하지 못했어요", reason + "\n\n릴리스 페이지에서 SimilarPhotoCleaner.exe를 직접 받아 바꿔 써도 돼요.",
                "릴리스 페이지 열기", () => OpenUrl(release.PageUrl), "닫기", null);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private bool MoveMode => RemoveModeCombo.SelectedIndex == 1;

    private string RemoveWhereText => MoveMode ? $"'{FileRemover.MovedFolderName}' 폴더로 옮겨요" : "휴지통으로 보내요";

    private static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── 폴더 목록 · 주소 표시줄 ─────────────────────────

    private async Task LoadFolderRootsAsync()
    {
        var roots = await Task.Run(BuildFolderRoots);
        foreach (var root in roots) _folderRoots.Add(root);
        foreach (var root in roots) _ = root.CountImagesAsync();
        UpdateCrumbs();
        if (_root != null) await RevealInTreeAsync(_root);
    }

    /// <summary>휴대폰(휴대폰 연결) → 사진·바탕 화면·다운로드·OneDrive → 드라이브 순서로 시작 폴더를 만든다.</summary>
    private static List<FolderNode> BuildFolderRoots()
    {
        var roots = new List<FolderNode>();
        try
        {
            if (Directory.Exists(FileRemover.PhoneLinkRoot))
                foreach (var device in new DirectoryInfo(FileRemover.PhoneLinkRoot).EnumerateDirectories())
                {
                    string storage = Path.Combine(device.FullName, "storage");
                    roots.Add(new FolderNode($"휴대폰 · {device.Name}", Directory.Exists(storage) ? storage : device.FullName,
                        FolderNode.PhoneGlyph, countImages: false));
                }
        }
        catch { /* 휴대폰 연결이 끊겨 있으면 건너뛴다 */ }

        void Known(string name, string? path, string glyph)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !roots.Any(r => SamePath(r.Path, path)))
                roots.Add(new FolderNode(name, path, glyph));
        }
        Known("사진", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), FolderNode.PicturesGlyph);
        Known("바탕 화면", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FolderNode.DesktopGlyph);
        Known("다운로드", DownloadsFolder(), FolderNode.DownloadGlyph);
        Known("OneDrive", Environment.GetEnvironmentVariable("OneDrive"), FolderNode.CloudGlyph);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network) || !drive.IsReady) continue;
                string label = !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.VolumeLabel
                    : drive.DriveType == DriveType.Removable ? "USB 드라이브" : "로컬 디스크";
                roots.Add(new FolderNode($"{label} ({drive.Name.TrimEnd(Path.DirectorySeparatorChar)})", drive.RootDirectory.FullName,
                    FolderNode.DriveGlyph, countImages: false));
            }
            catch { }
        }
        return roots;
    }

    private static string? DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        return SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0 ? path : null;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out string path);

    /// <summary>열린 폴더를 왼쪽 목록에서 펼쳐 보여 준다(그 사이 폴더들을 차례로 읽는다).</summary>
    private async Task RevealInTreeAsync(string path)
    {
        var node = _folderRoots.Where(r => FolderNode.IsSameOrUnder(path, r.Path)).MaxBy(r => r.Path.Length);
        while (node != null && !SamePath(node.Path, path))
        {
            node.IsExpanded = true;
            await node.LoadChildrenAsync();
            node = node.Children.FirstOrDefault(c => !c.IsPlaceholder && FolderNode.IsSameOrUnder(path, c.Path));
        }
        if (node == null || node.IsSelected || !SamePath(path, _root)) return;
        _suppressTree = true;
        try { node.IsSelected = true; }
        finally { _suppressTree = false; }
    }

    private void UpdateCrumbs()
    {
        if (_root == null)
        {
            Crumbs.ItemsSource = null;
            FolderText.Visibility = Visibility.Visible;
            UpButton.IsEnabled = false;
            return;
        }
        FolderText.Visibility = Visibility.Collapsed;

        // 휴대폰·사진 같은 시작 폴더가 있으면 그 이름부터 보여 준다(긴 C:\Users\... 대신).
        var baseNode = _folderRoots.Where(r => FolderNode.IsSameOrUnder(_root, r.Path)).MaxBy(r => r.Path.Length);
        string basePath = baseNode?.Path ?? Path.GetPathRoot(_root)!;
        var parts = new List<(string Name, string Path)>
        {
            (baseNode?.Name ?? basePath.TrimEnd(Path.DirectorySeparatorChar), basePath),
        };
        string relative = Path.GetRelativePath(basePath, _root);
        if (relative != ".")
        {
            string acc = basePath;
            foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                acc = Path.Combine(acc, part);
                parts.Add((part, acc));
            }
        }
        if (parts.Count > 5) parts = [parts[0], ("…", parts[^4].Path), .. parts.TakeLast(3)];
        Crumbs.ItemsSource = parts.Select((p, i) => new Crumb(p.Name, p.Path, i == 0, i == parts.Count - 1)).ToList();
        UpButton.IsEnabled = Path.GetDirectoryName(_root.TrimEnd(Path.DirectorySeparatorChar)) != null;
        Dispatcher.BeginInvoke(CrumbScroller.ScrollToRightEnd, DispatcherPriority.Loaded);
    }

    private void CrumbScroller_SizeChanged(object sender, SizeChangedEventArgs e) => CrumbScroller.ScrollToRightEnd();

    private void ApplyFolderPaneVisibility()
    {
        bool show = _settings.ShowFolderPane;
        FolderColumn.Width = show ? new GridLength(224) : new GridLength(0);
        FolderPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── 폴더 검사 ─────────────────────────

    private void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "정리할 사진 폴더 선택" };
        if (Directory.Exists(_settings.LastFolder)) dialog.InitialDirectory = _settings.LastFolder;
        if (dialog.ShowDialog(this) == true) StartScan(dialog.FolderName);
    }

    /// <summary>
    /// 폴더 검사를 시작한다. 정리하지 않은 '선택됨' 묶음이 있으면 먼저 묻고, 사용자가 취소하면 false.
    /// </summary>
    private bool StartScan(string folder, string action = "다른 폴더 열기")
    {
        if (!ResolvePendingSelections(action)) return false;
        _ = RunScanAsync(folder);
        return true;
    }

    private async Task RunScanAsync(string folder)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        string root = _root = Path.GetFullPath(folder);
        bool recursive = SubfoldersCheck.IsChecked == true;

        _settings.LastFolder = root;
        _settings.Save();
        UpdateCrumbs();
        _ = RevealInTreeAsync(root);
        Title = $"{Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))} - 비슷한 사진 정리기";
        RescanButton.IsEnabled = true;

        ResetSession();
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        InfoOverlay.Visibility = Visibility.Collapsed;
        ProgressOverlay.Visibility = Visibility.Visible;
        ProgressTitle.Text = "사진을 찾는 중...";
        ProgressDetail.Text = root;
        ProgressBar.IsIndeterminate = true;

        var failed = new List<string>();
        try
        {
            var files = await Task.Run(() => FindImages(root, recursive, cts.Token), cts.Token);
            if (cts != _scanCts) return;

            ProgressTitle.Text = "사진을 분석하는 중...";
            ProgressBar.IsIndeterminate = false;
            var results = new PhotoInfo?[files.Count];
            int done = 0, reused = 0;
            var clock = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                ProgressBar.Value = files.Count == 0 ? 1 : (double)done / files.Count;
                string text = $"{done:N0} / {files.Count:N0}장";
                if (reused > 0) text += $" · 이전 분석 재사용 {reused:N0}장";
                if (done >= 20 && clock.Elapsed.TotalSeconds > 2)
                    text += $" · {Remaining(clock.Elapsed.TotalSeconds / done * (files.Count - done))}";
                ProgressDetail.Text = text;
            };
            timer.Start();
            try
            {
                // 구글 드라이브처럼 읽기가 느린 곳에서는 여러 장을 동시에 읽어야 빠르다.
                var options = new ParallelOptions
                {
                    CancellationToken = cts.Token,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount * 2, 8, 32),
                };
                await Task.Run(() => Parallel.For(0, files.Count, options, i =>
                {
                    var file = files[i];
                    try
                    {
                        if (_cache.TryGet(file) is { } cached)
                        {
                            results[i] = cached;
                            Interlocked.Increment(ref reused);
                        }
                        else
                        {
                            var photo = PhotoAnalyzer.Analyze(file);
                            _cache.Put(photo);
                            results[i] = photo;
                        }
                    }
                    catch
                    {
                        lock (failed) failed.Add(file.FullName);
                    }
                    Interlocked.Increment(ref done);
                }), cts.Token);
            }
            finally
            {
                timer.Stop();
            }
            if (cts != _scanCts) return;
            _ = Task.Run(_cache.Save);

            _photos = results.OfType<PhotoInfo>().ToList();
            _scanNote = failed.Count == 0 ? "" : $"읽지 못한 파일 {failed.Count}개";
            if (failed.Any(f => f.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".heif", StringComparison.OrdinalIgnoreCase)))
                _scanNote += " (HEIC 사진은 Microsoft Store의 'HEIF 이미지 확장'이 있어야 읽을 수 있어요)";

            ProgressTitle.Text = "비슷한 사진을 묶는 중...";
            StartDetailWorkers();
            await RegroupAsync();
            if (cts != _scanCts) return;
            ProgressOverlay.Visibility = Visibility.Collapsed;

            if (_photos.Count == 0)
                ShowInfo("이 폴더에는 사진이 없어요",
                    recursive
                        ? $"읽을 수 있는 사진을 찾지 못했어요.\n{root}"
                        : $"이 폴더 바로 안에는 사진이 없어요. 왼쪽 폴더 목록에서 하위 폴더를 누르거나, 위쪽의 '하위 폴더 포함'을 켜고 다시 검사해 보세요.\n{root}",
                    "확인", null, "다른 폴더 선택", PickFolder);
            else if (_groups.Count == 0)
                ShowInfo("비슷한 사진을 찾지 못했어요",
                    $"사진 {_photos.Count:N0}장을 살펴봤지만 서로 비슷한 사진이 없었어요.\n위쪽의 '묶는 기준'을 느슨 쪽으로 옮기면 조금 덜 비슷한 사진도 묶어 볼 수 있어요.",
                    "확인", null, "다른 폴더 선택", PickFolder);
        }
        catch (OperationCanceledException)
        {
            if (cts != _scanCts) return;
            ProgressOverlay.Visibility = Visibility.Collapsed;
            ResetSession();
            WelcomeOverlay.Visibility = Visibility.Visible;
        }
    }

    private static string Remaining(double seconds) => seconds switch
    {
        < 10 => "곧 끝나요",
        < 60 => $"약 {Math.Ceiling(seconds / 10) * 10:0}초 남음",
        _ => $"약 {Math.Ceiling(seconds / 60):0}분 남음",
    };

    private List<FileInfo> FindImages(string root, bool recursive, CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var list = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            token.ThrowIfCancellationRequested();
            if (ImageLoader.IsSupported(file.Name) && !FileRemover.IsInsideMovedFolder(root, file.FullName))
                list.Add(file);
        }
        foreach (var folder in list.GroupBy(f => f.DirectoryName!, StringComparer.OrdinalIgnoreCase))
            _cache.Prune(folder.Key, folder.Select(f => f.Name));
        return list;
    }

    private void ResetSession()
    {
        StopDetailWorkers();
        _ = Task.Run(_cache.Save);
        _groups.Clear();
        _cardOf.Clear();
        _photos = [];
        _current = null;
        _focused = null;
        _previews.Clear();
        _undo.Clear();
        _removedCount = 0;
        _removedBytes = 0;
        _scanNote = "";
        FilmStrip.ItemsSource = null;
        PreviewImage.Source = null;
        ActionBar.IsEnabled = false;
        SelectionText.Text = "";
        GroupSummaryText.Text = "";
        SetPreviewChrome(false);
        ResetZoom();
        RefreshStatus();
    }

    private async Task RegroupAsync()
    {
        if (_root == null) return;
        int version = ++_regroupVersion;
        int level = (int)LevelSlider.Value;
        var photos = _photos;
        string root = _root;
        var groups = await Task.Run(() => Grouper.Group(photos, level));
        if (version != _regroupVersion || photos != _photos) return;

        int decidedBefore = photos.Count(p => !p.IsRemoved && p.KeepDecision != null);
        _current?.ReleaseThumbnails();
        _current = null;
        _focused = null;
        _groups.Clear();
        _cardOf.Clear();
        var ordered = groups
            .Select(g => g.Select(i => photos[i]).OrderBy(p => p.SortTime).ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).ToList())
            .OrderBy(g => g[0].SortTime);
        int number = 0;
        foreach (var members in ordered)
        {
            var group = new PhotoGroupView(++number, members, root);
            if (group.Cards.All(c => c.Info.IsReviewed)) group.State = GroupState.Cleaned;
            foreach (var card in group.Cards) _cardOf[card.Info] = (group, card);
            _groups.Add(group);
        }
        // 어느 묶음에도 들지 않게 된 사진의 선택 기록은 지운다.
        foreach (var p in photos.Where(p => !_cardOf.ContainsKey(p))) p.KeepDecision = null;
        int decidedAfter = photos.Count(p => !p.IsRemoved && p.KeepDecision != null);

        if (_groups.Count == 0)
        {
            FilmStrip.ItemsSource = null;
            PreviewImage.Source = null;
            ActionBar.IsEnabled = false;
            SelectionText.Text = "";
            SetPreviewChrome(false);
            RefreshStatus();
            return;
        }
        ActionBar.IsEnabled = true;
        ShowGroup(_groups.FirstOrDefault(g => g.State == GroupState.Undecided) ?? _groups[0]);
        if (decidedAfter < decidedBefore)
            FlashHint("묶음이 바뀌어서, 선택해 둔 묶음 중 일부가 '선택 안 됨'으로 돌아갔어요.", error: false);
    }

    // ───────────────────────── 2차 분석 ─────────────────────────

    private void StartDetailWorkers()
    {
        var token = _detailCts.Token;
        for (int i = 0; i < DetailWorkers; i++) Task.Run(() => DetailLoop(token));
    }

    private void StopDetailWorkers()
    {
        _detailCts.Cancel();
        _detailCts = new CancellationTokenSource();
        lock (_detailGate) _detailQueue = [];
    }

    /// <summary>보고 있는 묶음 → 그다음 묶음들 순서로 2차 분석할 사진 목록을 다시 만든다.</summary>
    private void RebuildDetailQueue()
    {
        var order = new List<PhotoInfo>();
        var seen = new HashSet<PhotoInfo>();
        void Add(PhotoGroupView g)
        {
            foreach (var c in g.Cards)
                if (!c.Info.IsDetailed && !c.IsRemoved && seen.Add(c.Info)) order.Add(c.Info);
        }
        if (_current != null) Add(_current);
        int start = _current == null ? 0 : _groups.IndexOf(_current) + 1;
        for (int k = 0; k < _groups.Count; k++) Add(_groups[(start + k) % _groups.Count]);

        lock (_detailGate) _detailQueue = order;
        if (order.Count > 0) _detailSignal.Release(DetailWorkers);
    }

    private void DetailLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            PhotoInfo? next = null;
            lock (_detailGate)
            {
                while (next == null && _detailQueue.Count > 0)
                {
                    var p = _detailQueue[0];
                    _detailQueue.RemoveAt(0);
                    if (!p.IsDetailed && !p.DetailInProgress && !p.IsRemoved)
                    {
                        p.DetailInProgress = true;
                        next = p;
                    }
                }
            }
            if (next == null)
            {
                try { _detailSignal.Wait(token); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            PhotoAnalyzer.Detail? result = null;
            try { result = PhotoAnalyzer.AnalyzeDetail(next.Path); }
            catch { /* 읽지 못하면 내장 썸네일 결과를 그대로 쓴다 */ }
            if (token.IsCancellationRequested) return;
            var photo = next;
            Dispatcher.InvokeAsync(() => ApplyDetail(photo, result, token));
        }
    }

    private void ApplyDetail(PhotoInfo photo, PhotoAnalyzer.Detail? result, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        if (result is not { } r)
        {
            photo.DetailFailed = true; // DetailInProgress로 남겨 다시 시도하지 않는다
            return;
        }
        photo.Sharpness = r.Sharpness;
        photo.Tilt = r.Tilt;
        photo.TiltConfidence = r.TiltConfidence;
        photo.ThumbnailJpeg = r.Thumbnail;
        photo.IsDetailed = true;
        photo.DetailInProgress = false;
        _cache.MarkChanged(photo);

        if (!_cardOf.TryGetValue(photo, out var entry)) return;
        entry.Card.RefreshThumbnail();
        entry.Group.RefreshDetails();
        if (entry.Group == _current)
        {
            UpdatePreviewState();
            UpdateActionBar();
        }
    }

    // ───────────────────────── 묶음 / 사진 이동 ─────────────────────────

    private void ShowGroup(PhotoGroupView group)
    {
        var previous = _current;
        _current = group;
        FilmStrip.ItemsSource = group.Cards;
        if (previous != null && previous != group) previous.ReleaseThumbnails();
        if (GroupList.SelectedItem != group) GroupList.SelectedItem = group;
        GroupList.ScrollIntoView(group);
        ResetZoom();
        SetPreviewChrome(true);
        RebuildDetailQueue();

        var first = group.Cards.FirstOrDefault(c => c.IsKept && !c.IsRemoved)
                    ?? group.Cards.FirstOrDefault(c => !c.IsRemoved)
                    ?? group.Cards[0];
        FocusCard(first, keepIt: false);
        RefreshStatus();
    }

    private void GoGroup(int delta)
    {
        if (_current == null) return;
        int i = _groups.IndexOf(_current) + delta;
        if (i >= 0 && i < _groups.Count) ShowGroup(_groups[i]);
    }

    /// <summary>다음 '선택 안 됨' 묶음으로 간다. 없으면 정리하기를 권한다.</summary>
    private void GoToNextUndecided()
    {
        if (_groups.Count == 0) return;
        int start = _current == null ? -1 : _groups.IndexOf(_current);
        for (int k = 1; k <= _groups.Count; k++)
        {
            var group = _groups[(start + k + _groups.Count) % _groups.Count];
            if (group.State == GroupState.Undecided && group != _current)
            {
                ShowGroup(group);
                return;
            }
        }
        if (_current?.State == GroupState.Undecided)
        {
            FlashHint("'선택 안 됨' 묶음은 지금 보고 있는 묶음 하나만 남았어요.", error: false);
            return;
        }
        OfferCleanUp();
    }

    private void OfferCleanUp()
    {
        var selected = _groups.Where(g => g.State == GroupState.Selected).ToList();
        var items = selected.SelectMany(g => g.CardsToRemove).ToList();
        if (selected.Count > 0)
        {
            ShowInfo("모든 묶음을 골랐어요",
                $"선택한 묶음 {selected.Count}개에서 고르지 않은 사진 {items.Count}장({Format.Size(items.Sum(c => c.Info.FileSize))})을 {RemoveWhereText}.\n" +
                "정리하기 전에는 왼쪽 목록에서 묶음을 눌러 고른 사진을 바꿀 수 있어요.",
                "지금 정리하기", () => ApplySelected(confirm: false), "나중에", null);
        }
        else
        {
            string where = MoveMode
                ? $"지운 사진은 '{Path.Combine(_root!, FileRemover.MovedFolderName)}' 폴더에 모아 두었어요."
                : "지운 사진은 휴지통에서 되살릴 수 있어요.";
            ShowInfo("모든 묶음을 정리했어요", $"정리한 사진 {_removedCount:N0}장 · {Format.Size(_removedBytes)}\n{where}",
                "확인", null, "다른 폴더 정리", PickFolder);
        }
    }

    private void FocusCard(PhotoCard card, bool keepIt)
    {
        if (_current == null) return;
        if (_focused != null && _focused != card) _focused.IsFocused = false;
        _focused = card;
        card.IsFocused = true;
        FilmStrip.ScrollIntoView(card);

        bool changesKeep = !card.IsKept || KeptCount() > 1;
        if (keepIt && !card.IsRemoved && changesKeep)
        {
            foreach (var c in _current.AliveCards) c.IsKept = c == card;
            OnKeepChanged();
        }
        UpdatePreview();
        UpdateActionBar();
    }

    private void MoveFocus(int delta)
    {
        if (_current == null || _focused == null) return;
        var cards = _current.Cards;
        for (int j = cards.IndexOf(_focused) + delta; j >= 0 && j < cards.Count; j += delta)
        {
            if (cards[j].IsRemoved) continue;
            // 한 장만 남기는 중이면 보고 있는 사진이 곧 남길 사진이 된다.
            FocusCard(cards[j], keepIt: KeptCount() <= 1);
            return;
        }
    }

    private int KeptCount() => _current?.AliveCards.Count(c => c.IsKept) ?? 0;

    private void ToggleKeep(PhotoCard card)
    {
        if (card.IsRemoved) return;
        if (card.IsKept && KeptCount() == 1)
        {
            FlashHint("최소 한 장은 남겨야 해요.");
            return;
        }
        card.IsKept = !card.IsKept;
        OnKeepChanged();
        UpdatePreviewState();
        UpdateActionBar();
    }

    /// <summary>사용자가 남길 사진을 바꿨을 때: 추천이 덮어쓰지 않게 하고, '선택됨' 묶음이면 기록도 고친다.</summary>
    private void OnKeepChanged()
    {
        if (_current == null) return;
        _current.UserTouched = true;
        if (_current.State == GroupState.Selected) _current.CommitDecisions();
        RefreshBatchPanel();
    }

    // ───────────────────────── 선택 / 정리 / 되돌리기 ─────────────────────────

    /// <summary>Enter: 지금 고른 대로 '선택됨'으로 표시하고 다음 '선택 안 됨' 묶음으로 간다.</summary>
    private void SelectCurrent()
    {
        var group = _current;
        if (group == null) return;
        if (KeptCount() == 0)
        {
            FlashHint("남길 사진을 한 장 이상 골라 주세요.");
            return;
        }
        if (group.State != GroupState.Cleaned || group.CardsToRemove.Any())
        {
            group.State = GroupState.Selected;
            group.UserTouched = true;
            group.CommitDecisions();
        }
        RefreshStatus();
        GoToNextUndecided();
    }

    /// <summary>S: 이 묶음은 '선택 안 됨'으로 두고(선택했던 것은 취소) 다음 묶음으로 간다.</summary>
    private void Defer()
    {
        var group = _current;
        if (group == null || group.State == GroupState.Cleaned) return;
        if (group.State == GroupState.Selected)
        {
            group.State = GroupState.Undecided;
            group.ClearDecisions();
        }
        RefreshStatus();
        GoToNextUndecided();
    }

    /// <summary>'선택됨' 묶음의 고르지 않은 사진을 한꺼번에 정리한다. 모두 정리했으면 true.</summary>
    private bool ApplySelected(bool confirm)
    {
        if (_root == null) return false;
        var groups = _groups.Where(g => g.State == GroupState.Selected).ToList();
        if (groups.Count == 0)
        {
            FlashHint("'선택됨' 묶음이 없어요. 묶음마다 남길 사진을 고르고 Enter를 눌러 주세요.");
            return false;
        }
        var items = groups.SelectMany(g => g.CardsToRemove).ToList();
        if (confirm && items.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"선택한 묶음 {groups.Count}개에서 고르지 않은 사진 {items.Count}장({Format.Size(items.Sum(c => c.Info.FileSize))})을 {RemoveWhereText}.\n\n계속할까요?",
                "선택한 묶음 정리", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return false;
        }

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<(string From, string To)>();
        var failures = new List<FileRemover.Failure>();
        if (items.Count > 0)
        {
            var paths = items.Select(c => c.Info.Path).ToList();
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                if (MoveMode)
                {
                    (moves, failures) = FileRemover.MoveToFolder(paths, _root);
                    removed.UnionWith(moves.Select(m => m.From));
                }
                else
                {
                    var (recycled, notRecycled) = FileRemover.SendToRecycleBin(paths, new WindowInteropHelper(this).Handle);
                    removed.UnionWith(recycled);
                    failures = notRecycled;
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        foreach (var card in items.Where(c => removed.Contains(c.Info.Path)))
        {
            card.IsRemoved = true;
            _removedCount++;
            _removedBytes += card.Info.FileSize;
            _previews.Remove(card.Info.Path);
        }
        foreach (var group in groups)
        {
            if (!group.CardsToRemove.Any())
            {
                group.State = GroupState.Cleaned;
                foreach (var c in group.AliveCards) c.Info.IsReviewed = true;
            }
            group.RefreshCounts();
        }
        if (moves.Count > 0)
        {
            var byPath = items.ToDictionary(c => c.Info.Path, StringComparer.OrdinalIgnoreCase);
            var undoMoves = moves.Select(m => (byPath[m.From], m.From, m.To)).ToList();
            var undoGroups = undoMoves.Select(m => _cardOf[m.Item1.Info].Group).Distinct().ToList();
            _undo.Push(new UndoEntry(undoGroups, undoMoves));
        }

        RefreshStatus();
        UpdatePreview();

        int failed = items.Count - removed.Count;
        if (failed > 0)
        {
            if (failures.FirstOrDefault(FileRemover.IsPhonePermissionProblem) is { } phoneFailure)
            {
                ShowPhoneHelp(failed, removed.Count, phoneFailure.ErrorCode);
            }
            else
            {
                MessageBox.Show(this,
                    $"사진 {failed}장은 정리하지 못했어요.\n다른 프로그램에서 열려 있거나, 권한이 없거나, 정리를 취소했을 수 있어요.\n그 묶음은 '선택됨'으로 남겨 두었어요.",
                    "사진 정리", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        int undecided = _groups.Count(g => g.State == GroupState.Undecided);
        string body = (MoveMode
                ? $"'{FileRemover.MovedFolderName}' 폴더로 옮겼어요. 왼쪽 아래 '되돌리기'로 제자리에 돌려놓을 수 있어요."
                : "휴지통에서 되살릴 수 있어요.")
            + (undecided > 0 ? $"\n아직 '선택 안 됨' 묶음이 {undecided}개 남았어요." : "");
        if (undecided > 0)
            ShowInfo($"사진 {removed.Count}장을 정리했어요", body, "남은 묶음 고르기", GoToNextUndecided, "닫기", null);
        else
            ShowInfo($"사진 {removed.Count}장을 정리했어요", body, "확인", null, "다른 폴더 정리", PickFolder);
        return true;
    }

    private CancellationTokenSource? _quickCts;

    /// <summary>
    /// 빠르게 정리하기: '선택 안 됨' 묶음마다 선명도·구도 점수가 높은 사진을 골라 남기고
    /// (2~3장 묶음은 1장, 4장 이상은 2~3장), '선택됨' 묶음과 함께 한꺼번에 정리한다.
    /// </summary>
    private async Task QuickCleanAsync()
    {
        if (_root == null || _quickCts != null) return;
        var targets = _groups.Where(g => g.State == GroupState.Undecided).ToList();
        if (targets.Count == 0 && !_groups.Any(g => g.State == GroupState.Selected))
        {
            FlashHint("정리할 묶음이 없어요.", error: false);
            return;
        }
        if (!await WaitForDetailsAsync(targets)) return;

        var picks = targets.ToDictionary(g => g, g => g.QuickPick());
        var selected = _groups.Where(g => g.State == GroupState.Selected).ToList();
        var items = targets.SelectMany(g => g.AliveCards.Where(c => !picks[g].Contains(c)))
            .Concat(selected.SelectMany(g => g.CardsToRemove)).ToList();

        var lines = new List<string>();
        if (targets.Count > 0)
            lines.Add($"'선택 안 됨' 묶음 {targets.Count}개에서 선명도와 구도가 좋은 사진을 골라 남겨요.\n" +
                      "    2~3장인 묶음은 1장, 4~6장은 2장, 7장 이상은 3장을 남겨요.");
        if (selected.Count > 0)
            lines.Add($"이미 고른 '선택됨' 묶음 {selected.Count}개는 고른 대로 정리해요.");
        lines.Add($"모두 사진 {items.Count}장({Format.Size(items.Sum(c => c.Info.FileSize))})을 {RemoveWhereText}.");
        var answer = MessageBox.Show(this,
            string.Join("\n", lines) + "\n\n예: 바로 정리하기\n아니요: 골라 두기만 하고 직접 확인하기\n취소: 돌아가기",
            "빠르게 정리하기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer is not (MessageBoxResult.Yes or MessageBoxResult.No)) return;

        foreach (var (group, keep) in picks) group.ApplyPick(keep);
        if (answer == MessageBoxResult.No)
        {
            ShowGroup(targets.FirstOrDefault() ?? _current ?? _groups[0]);
            FlashHint("자동으로 골라 '선택됨'으로 두었어요. 묶음을 눌러 확인한 뒤 '선택한 묶음 정리하기'를 누르세요.", error: false);
            return;
        }
        if (_current != null) ShowGroup(_current);
        ApplySelected(confirm: false);
    }

    /// <summary>묶음의 모든 사진에 선명도·기울기가 나올 때까지 진행 창을 띄우고 기다린다. 중지하면 false.</summary>
    private async Task<bool> WaitForDetailsAsync(List<PhotoGroupView> groups)
    {
        static bool Pending(PhotoCard c) => !c.Info.IsDetailed && !c.Info.DetailFailed;
        var cards = groups.SelectMany(g => g.AliveCards).ToList();
        if (!cards.Any(Pending)) return true;

        var cts = _quickCts = new CancellationTokenSource();
        ProgressTitle.Text = "선명도와 구도를 계산하는 중...";
        ProgressBar.IsIndeterminate = false;
        ProgressOverlay.Visibility = Visibility.Visible;
        RebuildDetailQueue();
        try
        {
            while (true)
            {
                int left = cards.Count(Pending);
                ProgressBar.Value = 1 - (double)left / cards.Count;
                ProgressDetail.Text = $"묶인 사진 {cards.Count - left:N0} / {cards.Count:N0}장";
                if (left == 0) return true;
                await Task.Delay(200, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            ProgressOverlay.Visibility = Visibility.Collapsed;
            _quickCts = null;
        }
    }

    private void Undo()
    {
        if (_undo.Count == 0 || _root == null) return;
        var entry = _undo.Pop();
        var restored = FileRemover.MoveBack(entry.Moves.Select(m => (m.From, m.To)), _root)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (card, from, _) in entry.Moves)
        {
            if (!restored.Contains(from)) continue;
            card.IsRemoved = false;
            _removedCount--;
            _removedBytes -= card.Info.FileSize;
        }
        foreach (var group in entry.Groups)
        {
            foreach (var c in group.Cards) c.Info.IsReviewed = false;
            group.State = GroupState.Selected;
            group.CommitDecisions();
            group.RefreshCounts();
        }
        InfoOverlay.Visibility = Visibility.Collapsed;

        if (restored.Count < entry.Moves.Count)
            MessageBox.Show(this, $"사진 {entry.Moves.Count - restored.Count}장은 되돌리지 못했어요. '{FileRemover.MovedFolderName}' 폴더를 확인해 주세요.",
                "되돌리기", MessageBoxButton.OK, MessageBoxImage.Warning);

        var first = entry.Groups.FirstOrDefault();
        if (first != null && _groups.Contains(first)) ShowGroup(first);
        else _ = RegroupAsync();
        RefreshStatus();
        FlashHint($"사진 {restored.Count}장을 제자리로 돌려놓았어요. 그 묶음들은 다시 '선택됨'이에요.", error: false);
    }

    /// <summary>
    /// 정리하지 않은 '선택됨' 묶음이 있으면, 다른 일을 하기 전에 정리할지 묻는다. 계속해도 되면 true.
    /// </summary>
    private bool ResolvePendingSelections(string action)
    {
        int pending = _groups.Count(g => g.State == GroupState.Selected && g.CardsToRemove.Any());
        if (pending == 0) return true;
        var answer = MessageBox.Show(this,
            $"'선택됨' 묶음 {pending}개를 아직 정리하지 않았어요.\n{action} 전에 정리할까요?\n\n" +
            $"예: 정리하고 {action}\n아니요: 정리하지 않고 {action} (고른 내용은 사라져요)\n취소: 돌아가기",
            "비슷한 사진 정리기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => ApplySelected(confirm: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ───────────────────────── 상태 표시 ─────────────────────────

    private void RefreshStatus()
    {
        UpdateSummary();
        UpdateActionBar();
    }

    private void UpdateSummary()
    {
        if (_groups.Count == 0 && _photos.Count == 0)
        {
            GroupSummaryText.Text = "";
            return;
        }
        int undecided = _groups.Count(g => g.State == GroupState.Undecided);
        int selected = _groups.Count(g => g.State == GroupState.Selected);
        int cleaned = _groups.Count(g => g.State == GroupState.Cleaned);
        int photos = _groups.Sum(g => g.AliveCards.Count());
        var lines = new List<string>
        {
            $"사진 {_photos.Count:N0}장 중 {photos:N0}장이 묶음 {_groups.Count:N0}개",
            $"선택 안 됨 {undecided} · 선택됨 {selected} · 정리됨 {cleaned}",
        };
        if (_removedCount > 0) lines.Add($"정리한 사진 {_removedCount:N0}장 ({Format.Size(_removedBytes)})");
        if (_scanNote.Length > 0) lines.Add(_scanNote);
        if (_root != null && FileRemover.IsPhoneLinkPath(_root))
            lines.Add("휴대폰 연결 폴더예요. 휴대폰 사진을 지우려면 'Windows와 연결' 앱에 '모든 파일 관리' 권한이 있어야 해요.");
        GroupSummaryText.Text = string.Join("\n", lines);
    }

    /// <summary>
    /// 휴대폰 연결(Phone Link) 폴더에서 지우기가 거부됐을 때. 휴대폰의 사진 폴더(DCIM)는 보호되어 있어서,
    /// 'Windows와 연결' 앱에 '모든 파일 관리' 권한이 없으면 카메라가 찍은 사진을 PC에서 지우거나 옮길 수 없다.
    /// </summary>
    private void ShowPhoneHelp(int failed, int done, int errorCode)
    {
        string doneText = done > 0 ? $" ({done}장은 정리했어요)" : "";
        ShowInfo("휴대폰이 사진 지우기를 막았어요",
            $"사진 {failed}장을 정리하지 못했어요{doneText}.\n" +
            "휴대폰의 사진 폴더는 보호되어 있어서, 휴대폰의 'Windows와 연결' 앱에 '모든 파일 관리' 권한이 있어야 PC에서 지울 수 있어요.\n\n" +
            "휴대폰에서 (메뉴 이름은 휴대폰마다 조금 다를 수 있어요)\n" +
            "1. 설정 → 애플리케이션 → 'Windows와 연결' → 권한\n" +
            "2. '파일 및 미디어'(또는 '저장공간')에서 '모든 파일 관리 허용'을 고르기\n" +
            "    안 보이면: 설정 → 애플리케이션 → 오른쪽 위 ⋮ → 특별한 접근 → 모든 파일 접근 → 'Windows와 연결' 켜기\n" +
            "3. 권한을 바꾼 뒤 아래 '다시 시도'를 누르세요.\n\n" +
            $"고른 묶음은 '선택됨'으로 남겨 두었어요. (오류 코드 0x{errorCode:X8})",
            "다시 시도", () => ApplySelected(confirm: false), "닫기", null);
    }

    private void UpdateActionBar()
    {
        RefreshBatchPanel();
        var group = _current;
        if (group == null)
        {
            SelectionText.Text = "";
            return;
        }
        int keep = group.AliveCards.Count(c => c.IsKept);
        var remove = group.CardsToRemove.ToList();
        string removeText = remove.Count == 0 ? "지울 사진 없음" : $"지울 사진 {remove.Count}장 ({Format.Size(remove.Sum(c => c.Info.FileSize))})";
        SelectionText.Text = $"{group.Title} [{group.StateLabel}] · 남길 사진 {keep}장 · {removeText}";

        DeferButton.IsEnabled = group.State != GroupState.Cleaned;
        DeferText.Text = group.State == GroupState.Selected ? "선택 취소하고 나중에 (S)" : "나중에 고르기 (S)";
        SelectText.Text = group.State switch
        {
            GroupState.Selected => "선택됨 · 다음 묶음 (Enter)",
            GroupState.Cleaned when remove.Count > 0 => "다시 선택 (Enter)",
            GroupState.Cleaned => "다음 묶음 (Enter)",
            _ => "선택 완료 (Enter)",
        };
    }

    private void RefreshBatchPanel()
    {
        var selected = _groups.Where(g => g.State == GroupState.Selected).ToList();
        var items = selected.SelectMany(g => g.CardsToRemove).ToList();
        BatchPanel.IsEnabled = _groups.Count > 0;
        QuickButton.IsEnabled = _groups.Any(g => g.State != GroupState.Cleaned);
        BatchButton.IsEnabled = selected.Count > 0;
        BatchButtonText.Text = selected.Count == 0 ? "선택한 묶음 정리하기" : $"선택한 묶음 {selected.Count}개 정리하기";
        BatchText.Text = selected.Count == 0
            ? "묶음마다 남길 사진을 고르고 Enter를 누르면 '선택됨'이 돼요. 다 고른 뒤 여기서 한꺼번에 정리해요."
            : $"고르지 않은 사진 {items.Count}장({Format.Size(items.Sum(c => c.Info.FileSize))})을 {RemoveWhereText}. (Ctrl+Enter)";
        UndoButton.Visibility = _undo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── 크게 보기 ─────────────────────────

    private async void UpdatePreview()
    {
        var card = _focused;
        var group = _current;
        if (card == null || group == null) return;
        int version = ++_previewVersion;

        int index = group.Cards.IndexOf(card);
        PreviewCounter.Text = $"{group.Title}  ·  {index + 1} / {group.Cards.Count}";
        PreviewInfo.Text = string.Join("   ·   ",
            new[] { card.FileName, card.TakenText, card.DetailText, card.FolderText }.Where(s => s.Length > 0));
        UpdatePreviewState();

        // 먼저 썸네일을 보여 주고, 큰 이미지가 준비되면 바꾼다.
        if (!ReferenceEquals(PreviewImage.Tag, card))
        {
            PreviewImage.Source = card.Thumbnail;
            PreviewImage.Tag = card;
        }
        if (card.IsRemoved) return;

        var full = await _previews.GetAsync(card.Info.Path);
        if (version != _previewVersion) return;
        if (full != null) PreviewImage.Source = full;

        foreach (int j in new[] { index + 1, index - 1, index + 2 })
            if (j >= 0 && j < group.Cards.Count && !group.Cards[j].IsRemoved)
                _ = _previews.GetAsync(group.Cards[j].Info.Path);
    }

    private void UpdatePreviewState()
    {
        var card = _focused;
        if (card == null) return;
        PreviewRecommended.Visibility = card.IsRecommended ? Visibility.Visible : Visibility.Collapsed;
        PreviewStateText.Text = card.IsRemoved ? "정리한 사진" : card.IsKept ? "✔ 남길 사진" : "✕ 지울 사진";
        PreviewState.Background = card.IsRemoved
            ? new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
            : (Brush)FindResource(card.IsKept ? "KeepBrush" : "DeleteBrush");
    }

    private void SetPreviewChrome(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewTopBar.Visibility = v;
        ((FrameworkElement)PreviewInfo.Parent).Visibility = v;
        if (!visible) PreviewImage.Tag = null;
    }

    private void ZoomAt(Point p, double factor)
    {
        double s = _zoom.ScaleX, ns = Math.Clamp(s * factor, 1, 8);
        if (Math.Abs(ns - s) < 0.001) return;
        _pan.X += p.X * (s - ns);
        _pan.Y += p.Y * (s - ns);
        _zoom.ScaleX = _zoom.ScaleY = ns;
        ClampPan();
        UpdateZoomBadge();
    }

    private void ResetZoom()
    {
        _zoom.ScaleX = _zoom.ScaleY = 1;
        _pan.X = _pan.Y = 0;
        UpdateZoomBadge();
    }

    private void ClampPan()
    {
        double w = PreviewImage.ActualWidth, h = PreviewImage.ActualHeight, s = _zoom.ScaleX;
        _pan.X = Math.Clamp(_pan.X, w - w * s, 0);
        _pan.Y = Math.Clamp(_pan.Y, h - h * s, 0);
    }

    private void UpdateZoomBadge()
    {
        bool zoomed = _zoom.ScaleX > 1.001;
        ZoomBadge.Visibility = zoomed ? Visibility.Visible : Visibility.Collapsed;
        ZoomText.Text = $"{_zoom.ScaleX:0.#}배 확대 · 더블클릭하면 원래대로";
        PreviewHost.Cursor = zoomed ? Cursors.SizeAll : null;
    }

    private Point ClampToImage(Point p) => new(
        Math.Clamp(p.X, 0, PreviewImage.ActualWidth),
        Math.Clamp(p.Y, 0, PreviewImage.ActualHeight));

    private static void OpenOriginal(PhotoCard? card)
    {
        if (card == null || card.IsRemoved || !File.Exists(card.Info.Path)) return;
        try { Process.Start(new ProcessStartInfo(card.Info.Path) { UseShellExecute = true }); }
        catch { }
    }

    // ───────────────────────── 안내 ─────────────────────────

    private void ShowInfo(string title, string body, string primaryText, Action? primary, string? secondaryText, Action? secondary)
    {
        InfoTitle.Text = title;
        InfoBody.Text = body;
        InfoPrimaryButton.Content = primaryText;
        _infoPrimary = primary;
        InfoSecondaryButton.Content = secondaryText;
        InfoSecondaryButton.Visibility = secondaryText == null ? Visibility.Collapsed : Visibility.Visible;
        _infoSecondary = secondary;
        InfoOverlay.Visibility = Visibility.Visible;
    }

    private void CloseInfo(Action? then)
    {
        InfoOverlay.Visibility = Visibility.Collapsed;
        then?.Invoke();
    }

    private void FlashHint(string message, bool error = true)
    {
        HintText.Text = message;
        HintText.Foreground = (Brush)FindResource(error ? "DeleteBrush" : "KeepBrush");
        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            HintText.Text = DefaultHint;
            HintText.Foreground = (Brush)FindResource("MutedBrush");
        };
        _hintTimer.Start();
    }

    // ───────────────────────── 이벤트 ─────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ProgressOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) CancelProgress();
            e.Handled = true;
            return;
        }
        if (InfoOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Enter) CloseInfo(_infoPrimary);
            else if (e.Key == Key.Escape) CloseInfo(null);
            e.Handled = e.Key is Key.Enter or Key.Escape or Key.Space;
            return;
        }
        if (ctrl && e.Key == Key.O)
        {
            PickFolder();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.System && e.SystemKey == Key.Up)
        {
            GoUp();
            e.Handled = true;
            return;
        }
        if (_current == null || RemoveModeCombo.IsDropDownOpen) return;

        switch (e.Key)
        {
            case Key.Left: MoveFocus(-1); break;
            case Key.Right: MoveFocus(1); break;
            case Key.Up or Key.PageUp: GoGroup(-1); break;
            case Key.Down or Key.PageDown: GoGroup(1); break;
            case Key.Space when _focused != null: ToggleKeep(_focused); break;
            case Key.Enter when ctrl: ApplySelected(confirm: true); break;
            case Key.Enter: SelectCurrent(); break;
            case Key.S when !ctrl: Defer(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Escape: ResetZoom(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e) => PickFolder();

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_root != null) StartScan(_root, "다시 검사하기");
    }

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void GoUp()
    {
        if (_root == null) return;
        string? parent = Path.GetDirectoryName(_root.TrimEnd(Path.DirectorySeparatorChar));
        if (parent != null) StartScan(parent);
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path && !SamePath(path, _root)) StartScan(path);
    }

    private void ToggleFolderPane_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowFolderPane = !_settings.ShowFolderPane;
        _settings.Save();
        ApplyFolderPaneVisibility();
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTree || e.NewValue is not FolderNode { IsPlaceholder: false } node) return;
        node.IsExpanded = true; // 탐색기처럼 누른 폴더의 하위 폴더를 바로 보여 준다
        if (SamePath(node.Path, _root)) return;

        // 선택 표시가 끝난 뒤에 검사를 시작한다(정리하지 않은 선택이 있으면 확인 창이 뜨기 때문).
        var previous = e.OldValue as FolderNode;
        Dispatcher.BeginInvoke(() =>
        {
            if (!node.IsSelected || StartScan(node.Path)) return;
            // 확인 창에서 취소했으면 목록의 선택도 원래 폴더로 되돌린다.
            _suppressTree = true;
            node.IsSelected = false;
            if (previous != null) previous.IsSelected = true;
            _suppressTree = false;
        });
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => CancelProgress();

    /// <summary>진행 창의 중지: 빠르게 정리하기를 기다리는 중이면 그것만, 아니면 폴더 검사를 멈춘다.</summary>
    private void CancelProgress()
    {
        if (_quickCts != null) _quickCts.Cancel();
        else _scanCts?.Cancel();
    }

    private async void QuickClean_Click(object sender, RoutedEventArgs e) => await QuickCleanAsync();

    private void Select_Click(object sender, RoutedEventArgs e) => SelectCurrent();
    private void Defer_Click(object sender, RoutedEventArgs e) => Defer();
    private void ApplySelected_Click(object sender, RoutedEventArgs e) => ApplySelected(confirm: true);
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void OpenOriginal_Click(object sender, RoutedEventArgs e) => OpenOriginal(_focused);
    private void InfoPrimary_Click(object sender, RoutedEventArgs e) => CloseInfo(_infoPrimary);
    private void InfoSecondary_Click(object sender, RoutedEventArgs e) => CloseInfo(_infoSecondary);

    private async void LevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Level = (int)LevelSlider.Value;
        _settings.Save();
        if (_photos.Count == 0) return;
        InfoOverlay.Visibility = Visibility.Collapsed;
        await RegroupAsync();
    }

    private void SubfoldersCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        _settings.Save();
        if (_root != null) FlashHint("'다시 검사'를 누르면 바뀐 설정으로 검사해요.", error: false);
    }

    private void RemoveModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.RemoveMode = RemoveModeCombo.SelectedIndex;
        _settings.Save();
        UpdateActionBar();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is PhotoGroupView group && group != _current) ShowGroup(group);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoCard card) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            OpenOriginal(card);
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            FocusCard(card, keepIt: false);
            ToggleKeep(card);
        }
        else
        {
            // 한 장만 남기는 중이면 누른 사진이 남길 사진이 된다. 여러 장을 골라 둔 상태면 크게 보기만 바꾼다.
            FocusCard(card, keepIt: KeptCount() <= 1);
        }
    }

    /// <summary>카드의 ✓ 단추: 다른 사진은 그대로 두고 이 사진만 남기기에 더하거나 뺀다.</summary>
    private void KeepToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoCard card) return;
        FocusCard(card, keepIt: false);
        ToggleKeep(card);
        if (KeptCount() == 2 && card.IsKept)
            FlashHint("여러 장을 남기는 중이에요. 사진을 눌러도 고른 것은 그대로이고, ✓ 단추로 빼거나 더할 수 있어요.", error: false);
    }

    private void PreviewState_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_focused != null) ToggleKeep(_focused);
    }

    private void FilmStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindChild<ScrollViewer>(FilmStrip) is not { } viewer) return;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewImage.Source == null) return;
        ZoomAt(ClampToImage(e.GetPosition(PreviewImage)), e.Delta > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source == null) return;
        if (e.ClickCount == 2)
        {
            if (_zoom.ScaleX > 1.001) ResetZoom();
            else ZoomAt(ClampToImage(e.GetPosition(PreviewImage)), 2.5);
            e.Handled = true;
            return;
        }
        if (_zoom.ScaleX > 1.001)
        {
            _panStart = e.GetPosition(PreviewHost);
            _panOrigin = new Point(_pan.X, _pan.Y);
            PreviewHost.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start) return;
        var pos = e.GetPosition(PreviewHost);
        _pan.X = _panOrigin.X + pos.X - start.X;
        _pan.Y = _panOrigin.Y + pos.Y - start.Y;
        ClampPan();
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart == null) return;
        _panStart = null;
        PreviewHost.ReleaseMouseCapture();
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Border.ClipToBounds alone is rectangular; also clip zoomed photos to the rounded panel.
        PreviewHost.Clip = new RectangleGeometry(new Rect(e.NewSize), 22, 22);
        ClampPan();
    }

    // Keep the comparison photo usable on short laptop displays. The full scores return
    // automatically when the window has enough height for the expanded photo cards.
    private void UpdateAdaptiveLayout(double height)
    {
        if (FilmStrip == null) return;
        bool compact = height < 740;
        FilmStrip.Height = compact ? 166 : 244;
        FilmStrip.Tag = compact ? "Compact" : null;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAdaptiveLayout(e.NewSize.Height);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFolder(e) != null ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (DroppedFolder(e) is { } folder) StartScan(folder);
    }

    private static string? DroppedFolder(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } items) return null;
        if (Directory.Exists(items[0])) return items[0];
        return File.Exists(items[0]) ? Path.GetDirectoryName(items[0]) : null;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closeResolved)
        {
            if (!ResolvePendingSelections("닫기"))
            {
                e.Cancel = true;
                return;
            }
            _closeResolved = true;
        }
        _scanCts?.Cancel();
        _detailCts.Cancel();
        _cache.Save();
        _settings.Save();
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
