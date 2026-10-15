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
        if (Cards.All(c => c.Info.KeepDecision != null))
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
    /// 선명도·구도 막대와 추천 사진을 다시 계산한다. 원본 분석(2차)이 끝난 사진이 생길 때마다 부른다.
    /// 모든 사진의 선명도가 나오기 전에는 '바이트/픽셀'(선명할수록 JPEG가 커짐)로 임시 추천한다.
    /// </summary>
    public void RefreshDetails()
    {
        var alive = AliveCards.ToList();
        if (alive.Count == 0) return;

        double maxSharp = Cards.Where(c => c.Info.IsDetailed).Select(c => c.Info.Sharpness).DefaultIfEmpty(0).Max();
        foreach (var c in Cards)
            c.SharpnessRatio = !c.Info.IsDetailed ? -1 : maxSharp > 0 ? c.Info.Sharpness / maxSharp : 1;

        var typical = TypicalScores(alive);
        foreach (var c in Cards)
            c.Composition = c.Info.IsDetailed && typical.TryGetValue(c, out double t) ? 0.5 * c.Info.LevelScore + 0.5 * t : -1;

        bool allDetailed = alive.All(c => c.Info.IsDetailed);
        double maxPixels = Math.Max(1, alive.Max(c => (double)c.Info.Width * c.Info.Height));
        double maxBpp = Math.Max(1e-9, alive.Max(c => c.Info.BytesPerPixel));
        foreach (var c in alive)
        {
            double resolution = c.Info.Width * (double)c.Info.Height / maxPixels;
            // 읽지 못한 사진이 섞여 있으면 선명도 대신 바이트/픽셀, 기울기 대신 구도 비슷함만 쓴다.
            c.Quality = allDetailed
                ? 0.4 * c.SharpnessRatio + 0.4 * c.Composition + 0.2 * resolution
                : 0.6 * (c.Info.BytesPerPixel / maxBpp) + 0.2 * typical[c] + 0.2 * resolution;
        }
        var best = Ranked().First();

        foreach (var c in Cards) c.IsRecommended = allDetailed && c == best;
        if (!UserTouched && State == GroupState.Undecided)
            foreach (var c in alive) c.IsKept = c == best;

        _cover = null;
        Notify(nameof(Cover));
    }

    /// <summary>
    /// 묶음의 다른 사진들과 구도가 얼마나 비슷한지(0~1). 구도 지문 차이의 평균이 가장 작은 사진이 1이고,
    /// 그보다 평균 8비트 이상 더 다르면 0이다. 사람이 끼어들었거나 혼자 많이 비뚤어진 사진이 낮게 나온다.
    /// </summary>
    private static Dictionary<PhotoCard, double> TypicalScores(List<PhotoCard> alive)
    {
        var mean = alive.ToDictionary(c => c, c => alive.Count < 2 ? 0 :
            alive.Where(o => o != c).Average(o => (double)System.Numerics.BitOperations.PopCount(c.Info.Hash ^ o.Info.Hash)));
        double min = mean.Values.Min();
        return mean.ToDictionary(p => p.Key, p => 1 - Math.Clamp((p.Value - min) / 8, 0, 1));
    }

    /// <summary>남길 사진 후보 순서(점수가 높은 순)</summary>
    private IEnumerable<PhotoCard> Ranked() =>
        AliveCards.OrderByDescending(c => c.Quality).ThenByDescending(c => c.Info.FileSize);

    /// <summary>빠르게 정리하기에서 남길 장수: 2~3장이면 1장, 4~6장이면 2장, 7장 이상이면 3장.</summary>
    public static int QuickKeepCount(int photos) => photos switch
    {
        <= 3 => 1,
        <= 6 => 2,
        _ => 3,
    };

    /// <summary>빠르게 정리하기: 점수가 높은 사진을 남길 장수만큼 고른다(바꾸지는 않음).</summary>
    public HashSet<PhotoCard> QuickPick()
    {
        RefreshDetails();
        return Ranked().Take(QuickKeepCount(AliveCards.Count())).ToHashSet();
    }

    /// <summary>QuickPick으로 고른 사진을 남기고 '선택됨'으로 확정한다.</summary>
    public void ApplyPick(HashSet<PhotoCard> keep)
    {
        foreach (var c in AliveCards) c.IsKept = keep.Contains(c);
        UserTouched = true;
        State = GroupState.Selected;
        CommitDecisions();
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
