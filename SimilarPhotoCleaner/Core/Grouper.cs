using System.Numerics;

namespace SimilarPhotoCleaner.Core;

/// <summary>
/// 구도 지문과 색 분포가 비슷한 사진끼리 묶는다.
/// 촬영 시간이 가까운 사진(연속 촬영)은 조금 더 너그럽게 묶는다.
/// </summary>
public static class Grouper
{
    public const int MinLevel = 1;
    public const int MaxLevel = 5;

    // 단계별 (구도 지문 허용 차이 비트 수, 최소 색 유사도). 1 = 엄격, 5 = 느슨
    private static readonly (int Distance, float Color)[] Limits =
    [
        (0, 1f),
        (6, 0.75f),
        (10, 0.68f),
        (15, 0.60f),
        (17, 0.55f),
        (20, 0.50f),
    ];

    private const int CloseInTimeBonus = 5;
    private const float CloseInTimeColorBonus = 0.05f;
    private static readonly TimeSpan CloseInTime = TimeSpan.FromMinutes(3);

    /// <summary>지우지 않은 사진 중 2장 이상인 묶음들(사진 인덱스 목록)을 돌려준다.</summary>
    public static List<List<int>> Group(IReadOnlyList<PhotoInfo> photos, int level)
    {
        var (maxDistance, minColor) = Limits[Math.Clamp(level, MinLevel, MaxLevel)];
        int n = photos.Count;
        var pairs = new List<(int, int)>();
        var gate = new object();
        Parallel.For(0, n, () => new List<(int, int)>(), (i, _, local) =>
        {
            var a = photos[i];
            if (a.IsRemoved) return local;
            for (int j = i + 1; j < n; j++)
            {
                var b = photos[j];
                int distance = Distance(a, b);
                if (distance > maxDistance + CloseInTimeBonus || b.IsRemoved) continue;

                bool close = a.TakenAt is { } ta && b.TakenAt is { } tb && (ta - tb).Duration() <= CloseInTime;
                if (distance > maxDistance + (close ? CloseInTimeBonus : 0)) continue;
                float color = PhotoAnalyzer.ColorSimilarity(a.ColorHistogram, b.ColorHistogram);
                if (color < minColor - (close ? CloseInTimeColorBonus : 0)) continue;
                local.Add((i, j));
            }
            return local;
        }, local =>
        {
            lock (gate) pairs.AddRange(local);
        });

        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        foreach (var (a, b) in pairs)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (photos[i].IsRemoved) continue;
            int root = Find(i);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(i);
        }
        return groups.Values.Where(g => g.Count >= 2).ToList();
    }

    /// <summary>
    /// 구도 차이(0~64). 전체끼리 비교한 값과, 한쪽 가운데 영역과 다른 쪽 전체를 비교한 값 중 작은 것.
    /// 가운데 영역 비교는 우연히 맞을 가능성이 조금 더 있어서 벌점을 더한다.
    /// </summary>
    public static int Distance(PhotoInfo a, PhotoInfo b)
    {
        int full = BitOperations.PopCount(a.Hash ^ b.Hash);
        int zoomed = Math.Min(BitOperations.PopCount(a.Hash ^ b.CenterHash),
                              BitOperations.PopCount(a.CenterHash ^ b.Hash)) + CenterPenalty;
        return Math.Min(full, zoomed);
    }

    private const int CenterPenalty = 2;
}
