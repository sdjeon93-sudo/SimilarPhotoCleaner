using System.Windows.Media.Imaging;
using SimilarPhotoCleaner.Core;

namespace SimilarPhotoCleaner.ViewModels;

public enum GroupState
{
    /// <summary>아직 남길 사진을 고르지 않음(미뤄 둔 묶음 포함)</summary>
    Undecided,
    /// <summary>남길 사진을 골랐고, 정리하기를 기다림</summary>
    Selected,
    /// <summary>고르지 않은 사진을 지움</summary>
    Cleaned,
}

/// <summary>비슷한 사진 묶음 하나</summary>
public sealed class PhotoGroupView : ObservableObject
{
    private GroupState _state;
    private BitmapSource? _cover;

    public PhotoGroupView(int number, IEnumerable<PhotoInfo> photos, string root)
    {
        Number = number;
        Cards = photos.Select(p => new PhotoCard(p, root)).ToList();

        // 묶음을 다시 만들 때, 모든 사진에 이미 고른 결과가 있으면 그대로 '선택됨'으로 되살린다.
        if (Cards.All(c => c.Info.KeepDecision != null) && Cards.Any(c => c.Info.KeepDecision == true))
        {
            foreach (var c in Cards) c.IsKept = c.Info.KeepDecision == true;
            UserTouched = true;
            _state = GroupState.Selected;
        }
        else
        {
            foreach (var c in Cards) c.Info.KeepDecision = null;
        }
        RefreshDetails();
    }

    public int Number { get; }
    public List<PhotoCard> Cards { get; }
    public DateTime FirstTime => Cards.Min(c => c.Info.SortTime);

    /// <summary>사용자가 남길 사진을 직접 바꿨는지. 바꿨으면 추천이 바뀌어도 고른 것을 건드리지 않는다.</summary>
    public bool UserTouched { get; set; }

    public IEnumerable<PhotoCard> AliveCards => Cards.Where(c => !c.IsRemoved);
    public IEnumerable<PhotoCard> CardsToRemove => AliveCards.Where(c => !c.IsKept);

    public string Title => $"묶음 {Number}";
    public string Subtitle => $"{AliveCards.Count()}장 · {FirstTime:yyyy.MM.dd HH:mm}";

    public BitmapSource? Cover
    {
        get
        {
            if (_cover == null)
            {
                var card = Cards.FirstOrDefault(c => c.IsRecommended) ?? Cards[0];
                try { _cover = ImageLoader.LoadFromBytes(card.Info.ThumbnailJpeg, 128); }
                catch { }
            }
            return _cover;
        }
    }

    public GroupState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Notify(nameof(StateLabel));
            Notify(nameof(Subtitle));
        }
    }

    public string StateLabel => State switch
    {
        GroupState.Selected => "선택됨",
        GroupState.Cleaned => "정리됨",
        _ => "선택 안 됨",
    };

    /// <summary>
    /// 선명도 막대와 추천 사진을 다시 계산한다. 원본 분석(2차)이 끝난 사진이 생길 때마다 부른다.
    /// 모든 사진의 선명도가 나오기 전에는 '바이트/픽셀'(선명할수록 JPEG가 커짐)로 임시 추천한다.
    /// </summary>
    public void RefreshDetails()
    {
        var alive = AliveCards.ToList();
        if (alive.Count == 0) return;

        double maxSharp = Cards.Where(c => c.Info.IsDetailed).Select(c => c.Info.Sharpness).DefaultIfEmpty(0).Max();
        foreach (var c in Cards)
            c.SharpnessRatio = c.Info.IsDetailed && maxSharp > 0 ? c.Info.Sharpness / maxSharp : -1;

        bool allDetailed = alive.All(c => c.Info.IsDetailed);
        double maxPixels = Math.Max(1, alive.Max(c => (double)c.Info.Width * c.Info.Height));
        double maxBpp = Math.Max(1e-9, alive.Max(c => c.Info.BytesPerPixel));
        double Score(PhotoCard c) =>
            0.7 * (allDetailed ? c.SharpnessRatio : c.Info.BytesPerPixel / maxBpp) +
            0.3 * (c.Info.Width * (double)c.Info.Height / maxPixels);
        var best = alive.OrderByDescending(Score).ThenByDescending(c => c.Info.FileSize).First();

        foreach (var c in Cards) c.IsRecommended = allDetailed && c == best;
        if (!UserTouched && State == GroupState.Undecided)
            foreach (var c in alive) c.IsKept = c == best;

        _cover = null;
        Notify(nameof(Cover));
    }

    /// <summary>지금 고른 결과를 사진마다 기록한다(묶는 기준을 바꿔도 되살릴 수 있게).</summary>
    public void CommitDecisions()
    {
        foreach (var c in AliveCards) c.Info.KeepDecision = c.IsKept;
    }

    public void ClearDecisions()
    {
        foreach (var c in Cards) c.Info.KeepDecision = null;
    }

    public void RefreshCounts() => Notify(nameof(Subtitle));

    public void ReleaseThumbnails()
    {
        foreach (var c in Cards) c.ReleaseThumbnail();
    }
}
