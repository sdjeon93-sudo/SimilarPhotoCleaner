namespace SimilarPhotoCleaner.Core;

public sealed class PhotoInfo
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public long FileSize { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTime ModifiedAt => ModifiedUtc.ToLocalTime();
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTime? TakenAt { get; init; }

    /// <summary>구도 지문(64비트 지각 해시). 비트가 적게 다를수록 비슷한 구도다.</summary>
    public ulong Hash { get; init; }
    /// <summary>가운데 80% 영역의 구도 지문. 확대 정도가 조금 다른 사진끼리 비교할 때 쓴다.</summary>
    public ulong CenterHash { get; init; }
    /// <summary>색 분포(합계 1)</summary>
    public float[] ColorHistogram { get; init; } = [];

    /// <summary>원본을 읽어 선명도와 큰 썸네일까지 만들었는지. 아니면 내장 썸네일로만 분석한 상태다.</summary>
    public bool IsDetailed { get; set; }
    /// <summary>선명도(라플라시안 분산). 같은 묶음 안에서 비교하는 용도. IsDetailed일 때만 의미가 있다.</summary>
    public double Sharpness { get; set; }
    /// <summary>수평선·수직선이 돌아간 각도(도, 0~45). IsDetailed일 때만 의미가 있다.</summary>
    public float Tilt { get; set; }
    /// <summary>Tilt를 얼마나 믿을 수 있는지(0~1). 건물·수평선처럼 곧은 선이 뚜렷할수록 크고, 풀숲처럼 선이 없으면 0에 가깝다.</summary>
    public float TiltConfidence { get; set; }
    /// <summary>작은 JPEG 썸네일</summary>
    public byte[] ThumbnailJpeg { get; set; } = [];

    public DateTime SortTime => TakenAt ?? ModifiedAt;
    public double BytesPerPixel => FileSize / Math.Max(1.0, (double)Width * Height);

    /// <summary>수평이 맞는 정도(0~1). 곧은 선이 뚜렷한데 3° 이상 기울었으면 0, 선이 없으면 판단하지 않고 1에 가깝다.</summary>
    public double LevelScore => 1 - TiltConfidence * Math.Min(1, Tilt / 3.0);

    // ── 이번 실행에서만 쓰는 상태 ──

    /// <summary>지웠거나 옮겨서 더 이상 폴더에 없음</summary>
    public bool IsRemoved { get; set; }
    /// <summary>정리를 마치고 남긴 사진</summary>
    public bool IsReviewed { get; set; }
    /// <summary>'선택됨' 묶음에서 고른 결과. true = 남김, false = 지움, null = 아직 고르지 않음</summary>
    public bool? KeepDecision { get; set; }
    /// <summary>2차 분석이 진행 중인지(중복 작업 방지)</summary>
    public bool DetailInProgress { get; set; }
    /// <summary>2차 분석에서 원본을 읽지 못함(이번 실행에서는 다시 시도하지 않음)</summary>
    public bool DetailFailed { get; set; }

    public void ResetSessionState()
    {
        IsRemoved = false;
        IsReviewed = false;
        KeepDecision = null;
        DetailInProgress = false;
        DetailFailed = false;
    }
}
