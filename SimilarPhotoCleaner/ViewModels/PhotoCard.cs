using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using SimilarPhotoCleaner.Core;

namespace SimilarPhotoCleaner.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
}

/// <summary>묶음 안의 사진 한 장(화면 표시용)</summary>
public sealed class PhotoCard(PhotoInfo info, string root) : ObservableObject
{
    public const double SharpnessBarMax = 104;

    private BitmapSource? _thumbnail;
    private bool _isKept, _isFocused, _isRecommended;

    public PhotoInfo Info { get; } = info;
    public string FileName => Info.FileName;

    public string FolderText
    {
        get
        {
            string? dir = Path.GetDirectoryName(Path.GetRelativePath(root, Info.Path));
            return string.IsNullOrEmpty(dir) ? "" : dir;
        }
    }

    public string DetailText => $"{Info.Width}×{Info.Height} · {Format.Size(Info.FileSize)}";
    public string TakenText => Info.TakenAt?.ToString("yyyy.MM.dd HH:mm:ss") ?? $"{Info.ModifiedAt:yyyy.MM.dd HH:mm} (수정한 날짜)";

    private double _sharpnessRatio = -1;

    /// <summary>묶음에서 가장 선명한 사진 대비 비율(0~1). 아직 원본을 분석하지 않았으면 -1</summary>
    public double SharpnessRatio
    {
        get => _sharpnessRatio;
        set
        {
            if (!Set(ref _sharpnessRatio, value)) return;
            Notify(nameof(SharpnessBarWidth));
            Notify(nameof(HasSharpness));
        }
    }

    public bool HasSharpness => SharpnessRatio >= 0;
    public double SharpnessBarWidth => Math.Max(4, SharpnessRatio * SharpnessBarMax);

    private double _composition = -1;

    /// <summary>구도 점수(0~1): 수평이 맞는지 + 묶음의 다른 사진들과 구도가 얼마나 비슷한지. 아직 원본을 분석하지 않았으면 -1</summary>
    public double Composition
    {
        get => _composition;
        set
        {
            if (!Set(ref _composition, value)) return;
            Notify(nameof(CompositionBarWidth));
            Notify(nameof(CompositionTip));
        }
    }

    public double CompositionBarWidth => Math.Max(4, Composition * SharpnessBarMax);

    public string CompositionTip => Info.TiltConfidence >= 0.3f
        ? $"구도 점수: 수평이 맞는지와 묶음의 다른 사진과 비슷한 구도인지로 매겨요. 이 사진은 약 {Info.Tilt:0.0}° 기울었어요."
        : "구도 점수: 수평이 맞는지와 묶음의 다른 사진과 비슷한 구도인지로 매겨요. 이 사진은 곧은 선이 뚜렷하지 않아 기울기는 따지지 않았어요.";

    /// <summary>빠르게 정리하기에서 남길 사진을 고르는 점수(선명도·구도·해상도). 묶음 안에서만 비교한다.</summary>
    public double Quality { get; set; }

    /// <summary>원본 분석으로 더 선명한 썸네일이 생겼을 때 다시 그리게 한다.</summary>
    public void RefreshThumbnail()
    {
        _thumbnail = null;
        Notify(nameof(Thumbnail));
    }

    public BitmapSource? Thumbnail
    {
        get
        {
            if (_thumbnail == null && Info.ThumbnailJpeg.Length > 0)
            {
                try { _thumbnail = ImageLoader.LoadFromBytes(Info.ThumbnailJpeg, 400); }
                catch { }
            }
            return _thumbnail;
        }
    }

    /// <summary>화면에서 내려간 묶음의 썸네일 메모리를 놓아준다. 다시 보이면 그때 새로 만든다.</summary>
    public void ReleaseThumbnail() => _thumbnail = null;

    public bool IsKept
    {
        get => _isKept;
        set { if (Set(ref _isKept, value)) Notify(nameof(StateText)); }
    }

    public bool IsFocused { get => _isFocused; set => Set(ref _isFocused, value); }
    public bool IsRecommended { get => _isRecommended; set => Set(ref _isRecommended, value); }

    public bool IsRemoved
    {
        get => Info.IsRemoved;
        set
        {
            if (Info.IsRemoved == value) return;
            Info.IsRemoved = value;
            Notify();
            Notify(nameof(StateText));
        }
    }

    public string StateText => IsRemoved ? "지움" : IsKept ? "남김" : "지울 사진";
}

public static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };
}
