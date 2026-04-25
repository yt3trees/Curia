# ポモドーロ機能 詳細仕様

バージョン: 1.0 / 作成日: 2026-04-25

---

## 概要

タスクに紐付いた 25 分集中タイマー (ポモドーロ) を実装し、セッション終了時に「何をしたか」を一言記録する。記録は既存の `focus_history/` 構造に統合し、Timeline・Standup・FocusUpdate・Skill の各機能がそのまま活用できるようにする。

### 設計方針

- ポモドーロログは `focus_history/pomodoro/` 配下に Markdown で格納し、AIコンテキストとしてそのまま参照可能にする
- ファイル肥大化は 30 日経過後の月次アーカイブ自動マージで防ぐ
- 既存機能 (Timeline / Standup / FocusUpdate / StateSnapshot / Skill) への統合は最小差分で行う
- UIは Dashboard の常設インジケーター + セッション終了時の軽量ポップアップの 2 画面のみ

---

## ゴール

1. Dashboard にポモドーロインジケーターを追加し、ワンクリックで開始できる
2. セッション終了時に「何をしたか」を一言入力し、`pomodoro/YYYY-MM-DD.md` に自動追記する
3. Timeline にポモドーロ実績を新 type として表示する
4. Standup 生成に昨日のポモドーロ集計を自動挿入する
5. FocusUpdate 実行時に当日のポモドーロメモをコンテキストとして利用する
6. `agent_hub/skills/pomodoro-review/` スキルで週次振り返りを AI 生成する

---

## 画面仕様

### 1. Dashboard インジケーター (常設)

Dashboard ヘッダー右端の既存ボタン列に追加する。

```
┌─────────────────────────────────────────────────────────────────┐
│ Curia                                           [≡] [⚙] [×]   │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Today's Queue          ┌──────────────────────────────────┐   │
│  ┌──────────────────┐   │ ▶ 25:00  ProjectA / タスク名     │   │
│  │ ○ タスクA        │   │ [ProjectA ▼] [開始]              │   │
│  │ ○ タスクB        │   └──────────────────────────────────┘   │
│  │ ○ タスクC        │                                           │
│  └──────────────────┘   今日: 0 sessions / 0 min               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

タイマー実行中:

```
┌─────────────────────────────────────────────────────────────────┐
│ Curia                                           [≡] [⚙] [×]   │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Today's Queue          ┌──────────────────────────────────┐   │
│  ┌──────────────────┐   │ ⏸ 18:42  ProjectA / タスクA      │   │
│  │ ○ タスクA   ←実行│   │ [一時停止] [中断]                │   │
│  │ ○ タスクB        │   └──────────────────────────────────┘   │
│  │ ○ タスクC        │                                           │
│  └──────────────────┘   今日: 2 sessions / 50 min              │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### インジケーター要素

| 要素 | 内容 |
|------|------|
| 時刻表示 | MM:SS 形式。残り時間をカウントダウン |
| アイコン | 待機中 ▶、実行中 ⏸ (一時停止アイコン)、休憩中 ☕ |
| プロジェクト選択 | TodayQueue のプロジェクト一覧を ComboBox で表示 |
| タスク選択 | 選択プロジェクトの TodayQueue タスクをプリフィル (任意選択) |
| 今日の集計 | セッション数 / 合計集中時間 (分) をインジケーター下部に表示 |
| 実行中タスクのハイライト | TodayQueue リストで実行中タスクに ← マーカーを表示 |

---

### 2. セッション開始ダイアログ

Dashboard インジケーターの [開始] クリック時に表示する軽量ポップアップ。

```
┌───────────────────────────────────────┐
│  Start Pomodoro Session               │
├───────────────────────────────────────┤
│                                       │
│  Project   [ProjectA            ▼]    │
│                                       │
│  Task      [タスクA (Asana)     ▼]    │
│            (任意 - 選択しなくてもOK)   │
│                                       │
│  Duration  ( ) 25 min  (●) 50 min    │
│            ( ) Custom: [  25  ] min   │
│                                       │
│              [Cancel]  [Start ▶]      │
└───────────────────────────────────────┘
```

- Project は現在選択中のプロジェクトをデフォルト選択
- Task は TodayQueue の overdue / today バケットのタスクを上位表示
- Duration デフォルトは 25 分。50 分・カスタムも選択可能
- [Start ▶] クリックでタイマー開始、ダイアログを閉じる

---

### 3. セッション終了ポップアップ

タイマーが 0 になると自動表示。CaptureWindow に近い軽量フローティングウィンドウ。

```
┌───────────────────────────────────────┐
│  Session Complete  ✓  25 min          │
├───────────────────────────────────────┤
│  ProjectA / タスクA                   │
│                                       │
│  What did you do?                     │
│  ┌─────────────────────────────────┐  │
│  │ API設計のドラフト完了、認証フロー│  │
│  │ は要検討                        │  │
│  └─────────────────────────────────┘  │
│                                       │
│  [Skip]  [☕ Take a Break]  [Save ✓]  │
└───────────────────────────────────────┘
```

- 画面右下に表示 (CaptureWindow と同じ位置)
- メモ入力は任意。[Skip] でメモなしで記録
- [☕ Take a Break] で 5 分休憩タイマーを自動開始してから保存
- [Save ✓] (または Enter キー) で `pomodoro/YYYY-MM-DD.md` に追記して閉じる
- Windows Toast 通知でも完了を通知 (アプリが背面にある場合に備え)

---

### 4. Timeline への統合

既存の Timeline フィルターに "Pomodoro" を追加する。

```
┌─────────────────────────────────────────────────────────────────┐
│ Timeline                                                        │
├───────────────┬─────────────────────────────────────────────────┤
│ Filters       │                                                 │
│ [✓] Focus     │  2026-04-25  ProjectA                          │
│ [✓] Decision  │  ├── 🍅 Pomodoro  4 sessions / 100 min         │
│ [✓] Work      │  │    09:00 API設計ドラフト完了                │
│ [✓] Pomodoro  │  │    10:05 認証フロー調査                     │
│               │  ├── 📋 Focus   ## 今やってること...           │
│               │  └── 📝 Decision  認証方式選定                  │
│               │                                                 │
│               │  2026-04-24  ProjectB                          │
│               │  ├── 🍅 Pomodoro  2 sessions / 50 min          │
│               │  └── 📋 Focus   ## バグ修正対応...             │
└───────────────┴─────────────────────────────────────────────────┘
```

- ヒートマップにポモドーロセッション数を濃淡で追加表示
- Pomodoro エントリをクリックで日次ログを Preview ペインに表示

---

## ファイル構造

### 日次ログ: `focus_history/pomodoro/YYYY-MM-DD.md`

```markdown
# Pomodoro Log 2026-04-25

## Sessions
- 09:00 [ProjectA] 25min completed — API設計のドラフト完了、認証フロー要検討
- 09:30 [ProjectA]  5min break
- 10:05 [ProjectA] 25min completed — 認証フロー調査、JWT vs Session 比較
- 10:35 [ProjectA]  5min break
- 11:00 [ProjectB] 25min interrupted — バグ再現確認 (電話中断)
- 14:00 [ProjectB] 50min completed — バグ原因特定、キャッシュ不整合

## Summary
- Total sessions: 4 completed, 1 interrupted
- Focus time: 125 min
- Break time: 10 min
- Completion rate: 80.0%
- Projects: ProjectA (75 min), ProjectB (75 min)
```

### 月次アーカイブ: `focus_history/pomodoro/YYYY-MM.md`

30 日経過した日次ファイルを月次ファイルに自動マージして削除する。

```markdown
# Pomodoro Archive 2026-04

## Daily Summary
| Date | Sessions | Focus Min | Completion |
|------|----------|-----------|------------|
| 04-01 | 6 | 150 | 100% |
| 04-02 | 4 | 100 | 75% |
| ...  | ...| ... | ... |
| 04-30 | 8 | 200 | 87.5% |

## Monthly Total
- Total sessions: 180
- Focus time: 4,500 min (75h)
- Best day: 04-15 (10 sessions / 250 min)
- Most worked: ProjectA (2,000 min)
```

---

## データモデル

### PomodoroSession (in-memory)

```csharp
public class PomodoroSession
{
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }       // 25 / 50 / カスタム
    public string ProjectKey { get; set; } = "";
    public string? TaskTitle { get; set; }         // 任意
    public string? Note { get; set; }              // セッション終了時メモ
    public PomodoroState State { get; set; }       // Running / Completed / Interrupted / Break
}

public enum PomodoroState
{
    Running,
    Paused,
    Completed,
    Interrupted,
    Break
}
```

### PomodoroDailySummary (log parsing 用)

```csharp
public record PomodoroDailySummary(
    DateTime Date,
    int CompletedSessions,
    int InterruptedSessions,
    int TotalFocusMinutes,
    double CompletionRate,
    Dictionary<string, int> MinutesByProject
);
```

---

## 新規ファイル・変更ファイル一覧

### 新規作成

| ファイル | 役割 |
|----------|------|
| `Services/PomodoroService.cs` | タイマー管理・ログ書き込み・月次アーカイブ |
| `ViewModels/PomodoroViewModel.cs` | ダッシュボードインジケーター用 VM |
| `Views/PomodoroCompleteWindow.cs` | セッション終了ポップアップ |
| `Views/PomodoroStartDialog.cs` | セッション開始ダイアログ |
| `.codex/skills/pomodoro-review/SKILL.md` | 週次振り返りスキル定義 |
| `.codex/skills/pomodoro-review/prompt.md` | スキル用プロンプトテンプレート |

### 変更ファイル

| ファイル | 変更内容 |
|----------|----------|
| `App.xaml.cs` | PomodoroService / PomodoroViewModel をシングルトン登録 |
| `Views/Pages/DashboardPage.xaml` | ポモドーロインジケーター UI 追加 |
| `Views/Pages/DashboardPage.xaml.cs` | PomodoroViewModel バインド |
| `Services/StandupGeneratorService.cs` | 昨日のポモドーロ集計を Yesterday セクションに挿入 |
| `Services/ContextCompressionLayerService.cs` | `focus_history/pomodoro/` ディレクトリを初期化対象に追加 |
| `Services/StateSnapshotService.cs` | 当日ポモドーロ集計を `curator_state.json` に追加 |
| `ViewModels/TimelineViewModel.cs` | Pomodoro type のエントリ読み込み・フィルター追加 |
| `Services/FocusUpdateService.cs` | 当日ポモドーロメモを captured context に追加 (オプション) |
| `Services/AgentHubService.cs` | pomodoro-review スキルを組み込みスキルとして展開 |

---

## PomodoroService 設計

```csharp
public class PomodoroService
{
    // ---- タイマー状態 ----
    public PomodoroSession? CurrentSession { get; private set; }
    public TimeSpan Remaining { get; private set; }
    public bool IsRunning { get; private set; }

    // ---- イベント ----
    public event Action<TimeSpan>? Tick;           // 1秒ごと
    public event Action<PomodoroSession>? Completed; // タイマー完了

    // ---- 操作 ----
    public void Start(PomodoroSession session) { ... }
    public void Pause() { ... }
    public void Resume() { ... }
    public void Interrupt() { ... }  // 中断 (メモなしで記録)

    // ---- ログ ----
    public Task SaveSessionAsync(PomodoroSession session, CancellationToken ct = default);
    public Task<PomodoroDailySummary?> GetTodaySummaryAsync(string projectKey);
    public Task<PomodoroDailySummary?> GetYesterdaySummaryAsync(string projectKey);

    // ---- アーカイブ ----
    // 起動時に 30 日経過した日次ファイルを月次ファイルへ自動マージ
    public Task ArchiveOldLogsAsync();
}
```

タイマーは `System.Threading.PeriodicTimer` (1 秒間隔) で実装し、UI スレッドへのディスパッチは `App.Current.Dispatcher.InvokeAsync` で行う。

---

## 既存機能との統合詳細

### Standup (StandupGeneratorService)

`BuildYesterdayLines()` の末尾に以下を追加:

```csharp
// 昨日のポモドーロ集計を挿入
var pomodoro = await _pomodoroService.GetYesterdaySummaryAsync(proj.HiddenKey);
if (pomodoro is { CompletedSessions: > 0 })
{
    lines.Add($"- [{proj.Name}] Pomodoro: {pomodoro.CompletedSessions} sessions, " +
              $"{pomodoro.TotalFocusMinutes} min focus " +
              $"({pomodoro.CompletionRate:P0})");
}
```

### Timeline (TimelineViewModel)

`BuildRawEntries()` でプロジェクトごとに以下を追加:

```csharp
var pomodoroDir = Path.Combine(proj.AiContextContentPath, "focus_history", "pomodoro");
if (Directory.Exists(pomodoroDir))
{
    foreach (var file in Directory.EnumerateFiles(pomodoroDir, "????-??-??.md"))
    {
        if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file),
                "yyyy-MM-dd", null, DateTimeStyles.None, out var date)) continue;
        rawEntries.Add(new TimelineRawEntry
        {
            Date        = date,
            Path        = file,
            Type        = "Pomodoro",
            Topic       = "",
            ProjectName = proj.Name,
        });
    }
}
```

フィルター `ShowPomodoro` を既存の ShowFocus / ShowDecision / ShowWork と同列に追加。

### FocusUpdate (FocusUpdateService)

`BuildUserPrompt()` 内で当日のポモドーロメモを context に含める (AiEnabled かつ当日ログが存在する場合のみ):

```
## Today's Pomodoro Sessions
- 09:00 [ProjectA] 25min — API設計ドラフト完了、認証フロー要検討
- 10:05 [ProjectA] 25min — 認証フロー調査、JWT vs Session 比較
```

### StateSnapshot (StateSnapshotService)

`ProjectEntry` に `PomodoroToday` プロパティを追加:

```csharp
public PomodoroSnapshotEntry? PomodoroToday { get; set; }

public record PomodoroSnapshotEntry(
    int CompletedSessions,
    int TotalFocusMinutes,
    double CompletionRate
);
```

---

## Skill: pomodoro-review

`agent_hub/skills/pomodoro-review/SKILL.md` として定義し、AgentHubService が初回起動時に展開する。

### 用途

Claude Code から `@pomodoro-review` を呼び出すことで、週次・月次の振り返りレポートを生成する。

### 入力ソース

- `focus_history/pomodoro/YYYY-MM-DD.md` (直近 7 日分)
- `curator_state.json` の PomodoroToday (当日リアルタイム集計)
- `project_summary.md` (プロジェクト概要)

### 出力

```markdown
# 週次ポモドーロ振り返り 2026-04-19 〜 2026-04-25

## 実績サマリー
- 合計セッション: 32 completed (4 interrupted)
- 合計集中時間: 800 min (13.3h)
- 完了率: 88.9%
- 最多集中日: 04-23 (10 sessions)

## プロジェクト別配分
- ProjectA: 450 min (56%)
- ProjectB: 250 min (31%)
- ProjectC: 100 min (13%)

## 気づきと提案
- ProjectA への集中が高く、目標の API 設計フェーズと一致
- 午前中 (9〜12 時) の完了率が 95% と高い → 重要タスクは午前に配置を継続推奨
- 中断セッションは全て午後 → 会議ブロックを Schedule に事前登録すると改善可能
```

---

## 実装フェーズ

### Phase 1: コア (優先実装)

1. `PomodoroService` - タイマー管理・ログ書き込み・アーカイブ
2. `PomodoroViewModel` - インジケーター用 ObservableProperty
3. Dashboard インジケーター UI (xaml + xaml.cs)
4. `PomodoroCompleteWindow` - セッション終了ポップアップ
5. `ContextCompressionLayerService` - `focus_history/pomodoro/` 初期化追加
6. `App.xaml.cs` - DI 登録

### Phase 2: 既存機能統合

7. `StandupGeneratorService` - 昨日のポモドーロ集計挿入
8. `TimelineViewModel` - Pomodoro type 追加・フィルター追加
9. `StateSnapshotService` - PomodoroToday 追加

### Phase 3: AI 連携

10. `FocusUpdateService` - 当日ポモドーロメモをコンテキストに追加
11. `PomodoroStartDialog` - タスク選択 UI
12. pomodoro-review スキル定義・AgentHubService 展開

---

## 未決事項・検討ポイント

| 項目 | 現状案 | 代替案 |
|------|--------|--------|
| タイマー音 | なし (Toast通知のみ) | 設定でビープ音を有効化可能に |
| 休憩タイマー | 5 分固定 | 設定で変更可能に (5/10/15 分) |
| 中断時のメモ | 任意入力ポップアップ | 中断は即記録 (メモなし) |
| 月次アーカイブ閾値 | 30 日 | 設定で変更可能に |
| ポモドーロ数の設定 | settings.json に `PomodoroDurationMinutes` 追加 | デフォルト 25 固定 |
| 複数プロジェクト同時実行 | 不可 (1 セッション 1 プロジェクト) | 現行維持 |
