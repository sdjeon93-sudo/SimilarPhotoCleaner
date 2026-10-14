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
    /// <summary>작은 JPEG 썸네일</summary>
    public byte[] ThumbnailJpeg { get; set; } = [];

    public DateTime SortTime => TakenAt ?? ModifiedAt;
    public double BytesPerPixel => FileSize / Math.Max(1.0, (double)Width * Height);

    // ── 이번 실행에서만 쓰는 상태 ──

    /// <summary>지웠거나 옮겨서 더 이상 폴더에 없음</summary>
    public bool IsRemoved { get; set; }
    /// <summary>정리를 마치고 남긴 사진</summary>
    public bool IsReviewed { get; set; }
    /// <summary>'선택됨' 묶음에서 고른 결과. true = 남김, false = 지움, null = 아직 고르지 않음</summary>
    public bool? KeepDecision { get; set; }
    /// <summary>2차 분석이 진행 중인지(중복 작업 방지)</summary>
    public bool DetailInProgress { get; set; }

    public void ResetSessionState()
    {
        IsRemoved = false;
        IsReviewed = false;
        KeepDecision = null;
        DetailInProgress = false;
    }
}
