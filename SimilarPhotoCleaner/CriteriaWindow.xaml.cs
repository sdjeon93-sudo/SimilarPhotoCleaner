using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimilarPhotoCleaner.Core;

namespace SimilarPhotoCleaner;

/// <summary>사진을 묶는 기준, 선명도·구도 판단, 추천 기준을 짧게 설명하는 창(설정 줄의 '기준 설명' 또는 F1).</summary>
public partial class CriteriaWindow : Window
{
    public CriteriaWindow(int currentLevel)
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);

        BonusText.Inlines.Add(new System.Windows.Documents.Run("연속 촬영 · ") { FontWeight = FontWeights.SemiBold });
        BonusText.Inlines.Add(
            $"촬영 시간이 {Grouper.CloseInTime.TotalMinutes:0}분 안인 사진끼리는 구도 차이를 {Grouper.CloseInTimeBonus}칸, " +
            $"색 비슷함을 {Grouper.CloseInTimeColorBonus:P0}만큼 더 너그럽게 봐요.");
        ExceptionText.Inlines.Add(new System.Windows.Documents.Run("따로 두는 사진 · ") { FontWeight = FontWeights.SemiBold });
        ExceptionText.Inlines.Add(
            $"가로세로 비율이 {Grouper.MaxAspectRatioDifference:0.#}배 넘게 다른 사진(가로 사진과 세로 사진 등)은 묶지 않아요. " +
            $"스크린샷은 일반 사진과 섞지 않고, 같은 폴더의 스크린샷끼리만 {Grouper.ScreenshotStricter}칸 더 엄격하게 묶어요.");
        QuickText.Inlines.Add(new System.Windows.Documents.Run("빠르게 정리하기 · ") { FontWeight = FontWeights.SemiBold });
        QuickText.Inlines.Add(
            "'선택 안 됨' 묶음마다 점수가 높은 사진을 남기고 나머지를 정리해요. 사진이 2~3장인 묶음은 1장, 4~6장은 2장, " +
            "7장 이상은 3장을 남겨요. 이미 '선택됨'으로 고른 묶음은 고른 대로 정리해요.");

        BuildLevelTable(currentLevel);
    }

    /// <summary>단계 이름(툴팁·표에 같이 쓴다)</summary>
    public static string LevelName(int level) => level switch
    {
        Grouper.MinLevel => $"{level}단계 · 엄격",
        3 => $"{level}단계 · 기본",
        Grouper.MaxLevel => $"{level}단계 · 느슨",
        _ => $"{level}단계",
    };

    /// <summary>그 단계에서 어떤 사진까지 한 묶음이 되는지 쉬운 말로</summary>
    public static string LevelSummary(int level) => level switch
    {
        1 => "거의 똑같은 사진만 묶어요(연속 촬영).",
        2 => "같은 장면을 조금 움직여 찍은 사진까지 묶어요.",
        3 => "같은 장소·같은 구도면 묶어요. 살짝 당기거나 밝기가 달라도 묶여요.",
        4 => "구도가 조금 달라도 묶어요. 다른 장면이 섞일 수 있어요.",
        _ => "분위기가 비슷하면 묶어요. 묶음이 크게 이어질 수 있어요.",
    };

    private void BuildLevelTable(int currentLevel)
    {
        string[] headers = ["단계", "구도 차이", "색 비슷함", "어떤 사진이 묶이나요"];
        LevelTable.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < headers.Length; c++)
            AddCell(0, c, headers[c], bold: true, muted: true, background: null);

        for (int level = Grouper.MinLevel; level <= Grouper.MaxLevel; level++)
        {
            var (distance, color) = Grouper.LimitsFor(level);
            bool current = level == currentLevel;
            var background = current ? (Brush)FindResource("PrimarySoftBrush") : null;
            int row = LevelTable.RowDefinitions.Count;
            LevelTable.RowDefinitions.Add(new RowDefinition());
            AddCell(row, 0, LevelName(level), current, false, background);
            AddCell(row, 1, $"{distance}칸 이하", current, false, background);
            AddCell(row, 2, $"{color:P0} 이상", current, false, background);
            AddCell(row, 3, LevelSummary(level), current, false, background);
        }
    }

    private void AddCell(int row, int column, string text, bool bold, bool muted, Brush? background)
    {
        var cell = new Border
        {
            Background = background ?? Brushes.Transparent,
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(0, row == 0 ? 0 : 1, 0, 0),
            Padding = new Thickness(column == 0 ? 12 : 8, 7, 12, 7),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource(muted ? "MutedBrush" : bold ? "PrimaryBrush" : "TextBrush"),
            },
        };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        LevelTable.Children.Add(cell);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
