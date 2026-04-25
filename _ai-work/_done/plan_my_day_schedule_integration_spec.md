# Plan My Day × Schedule 統合仕様

WeeklySchedule に登録済みの今日のブロックを、Plan My Day のスケジュールヒントに自動 pre-fill する。

---

## ゴール

`ShowScheduleHintDialogAsync` を開いたとき、今日の `ScheduleBlock` (Timed / AllDay 両方) をテキストに変換してヒント欄に初期値として表示する。ユーザーは編集・追記するだけでよく、手入力の手間が省ける。

---

## 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `Views/Pages/DashboardPage.xaml.cs` | ScheduleService 注入 + ヒント pre-fill ロジック追加 |

---

## 実装詳細

### 1. ScheduleService の注入

`DashboardPage.xaml.cs` のコンストラクタ引数に `ScheduleService` を追加し、フィールドに保持する。

```csharp
// フィールド追加
private readonly ScheduleService _scheduleService;

// コンストラクタ引数に追加
public DashboardPage(..., ScheduleService scheduleService, ...)
{
    ...
    _scheduleService = scheduleService;
}
```

`App.xaml.cs` の DI 登録は既に済み (ScheduleService はシングルトン登録済み)。

### 2. 今日のブロックをヒントテキストに変換するヘルパー

```csharp
private string BuildTodayScheduleHint()
{
    var weekStart = WeeklyScheduleViewModel.GetMondayOf(DateTime.Today);
    // WeeklyScheduleViewModel.GetMondayOf は private のため、同等処理をインライン実装
    // DateTime today = DateTime.Today;
    // var weekStart = today.AddDays(-(int)today.DayOfWeek == 0 ? 6 : (int)today.DayOfWeek - 1);
    var blocks = _scheduleService.GetBlocksForWeek(weekStart);
    var today = DateTime.Today;

    var lines = new List<string>();

    // Timed ブロック (今日 + 開始時刻でソート)
    var timedToday = blocks
        .Where(b => b.Kind == ScheduleBlockKind.Timed
                 && b.StartAt.HasValue
                 && b.StartAt.Value.Date == today)
        .OrderBy(b => b.StartAt!.Value);

    foreach (var b in timedToday)
    {
        var start = b.StartAt!.Value;
        var end   = start.AddMinutes(b.DurationSlots * 30);
        var label = string.IsNullOrWhiteSpace(b.TitleSnapshot)
            ? b.ProjectShortName
            : $"{b.ProjectShortName} {b.TitleSnapshot}";
        lines.Add($"{start:HH:mm}-{end:HH:mm} {label}");
    }

    // AllDay ブロック (今日を含む)
    var allDayToday = blocks
        .Where(b => b.Kind == ScheduleBlockKind.AllDay
                 && b.StartDate.HasValue
                 && b.StartDate.Value.Date <= today
                 && b.EndDate.HasValue
                 && b.EndDate.Value.Date >= today);

    foreach (var b in allDayToday)
    {
        var label = string.IsNullOrWhiteSpace(b.TitleSnapshot)
            ? b.ProjectShortName
            : $"{b.ProjectShortName} {b.TitleSnapshot}";
        lines.Add($"(all day) {label}");
    }

    return string.Join("\n", lines);
}
```

注: `WeeklyScheduleViewModel.GetMondayOf` は private static のため、同等の月曜算出を DashboardPage 内でインライン実装する。

### 3. ShowScheduleHintDialogAsync への適用

既存の TextBox 生成箇所で `Text` に pre-fill する。

```csharp
// 既存コード (抜粋)
var hintBox = new System.Windows.Controls.TextBox
{
    AcceptsReturn = true,
    Height = 80,
    ...
};

// 変更: 今日のブロックを初期値として設定
var prefill = BuildTodayScheduleHint();
if (!string.IsNullOrEmpty(prefill))
{
    hintBox.Text = prefill;
    hintBox.CaretIndex = hintBox.Text.Length; // カーソルを末尾に
}
```

---

## 出力例

今日 (2026-04-25) に WeeklySchedule で以下を登録済みの場合:
- 10:00-11:00 ProjectAlpha Sprint planning
- 14:00-15:00 ProjectBeta Demo
- (all day) ProjectGamma Release freeze

ヒントダイアログの初期テキスト:
```
10:00-11:00 ProjectAlpha Sprint planning
14:00-15:00 ProjectBeta Demo
(all day) ProjectGamma Release freeze
```

ユーザーは追記・削除だけすれば Plan My Day を実行できる。

---

## ブロックがない場合の挙動

今日のブロックが 0 件の場合は pre-fill なし (空欄のまま)。既存の挙動と変わらない。

---

## 考慮事項

- WeeklySchedulePage 側で今日のブロックを未ロードの場合も `GetBlocksForWeek` が月ファイルから読み込むため問題なし。
- `TitleSnapshot` が空のブロック (タスク名なし、プロジェクトのみ) は ProjectShortName だけを表示する。
- ヒント欄はあくまで LLM へのテキスト入力。ScheduleBlock の ID や構造は渡さない。
