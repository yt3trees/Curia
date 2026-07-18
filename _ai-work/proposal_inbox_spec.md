# Proposal Inbox (提案インボックス + トレイバッジ) - 実装計画

Focus Auto-Update (a0ae5fc) で、current_focus.mdの放置は解消するはずだった。Dashboard/Editorのボタンを押せばAIが更新案を作ってくれる。あとは押すだけである。

だが、このボタンが押される見込みは薄い。押すには「そろそろFocusを更新しよう」と思い立つ必要があり、それを思い立てるユーザーは、そもそも手で更新できている。放置の根本原因は生成の手間ではなく、更新という作業が能動的であること自体にある。ボタンを1つ増やしても、そこは変わらない。

そこで構造を逆転させる。生成はバックグラウンドに移し、ユーザーの仕事を「生成の実行」から「承認だけ」に変える:

- スケジューラが定期的に「提案する価値のあるプロジェクト」を選定し、裏でFocus更新提案を生成
- 生成された提案は永続的なインボックス (config dir配下) に溜まる
- トレイアイコンのバッジとDashboardのカードで件数を通知
- ユーザーは気が向いたときにdiffを見てAccept/Rejectするだけ

## コンセプト

```
ProposalSchedulerService (Timer, StandupGeneratorServiceと同パターン)
        │  対象選定: シグナルあり かつ focusが古い プロジェクト
        ▼
FocusUpdateService.GenerateProposalAsync(project, ws, signals)   ← 既存
        │
        ▼
ProposalInboxService  ── 永続化: [config_dir]\proposals\*.json
        │  WeakReferenceMessenger: ProposalInboxChangedMessage
        ├─▶ TrayService バッジ (Pending件数) + BalloonTip
        └─▶ Dashboard「Proposal Inbox」カード
                │ 行クリック
                ▼
        ProposalReviewDialog (既存のdiffレビュー) 
                ├─ Accept → ApplyProposalAsync (既存, focus_history保存込み)
                └─ Reject → アーカイブ
```

## ユースケース

### UC-1: 朝、溜まった提案をまとめて承認する

1. 前日の夕方〜夜、スケジューラが裏で動き、活動のあった2プロジェクトのFocus更新提案を生成済み
2. 朝PCを開くと、トレイのダイヤアイコンに「2」のバッジが付いている
3. ホットキーでCuriaを開くと、Dashboard上部にProposal Inboxカードが表示されている
4. 1件目をクリック → diffを確認 → 内容が妥当なのでAccept。current_focus.mdが更新され、focus_historyにスナップショットが残る
5. 2件目は的外れなのでReject
6. バッジが消え、レビュー完了。所要時間は1〜2分

ポイント: ユーザーは「Focusを更新しよう」と思い出す必要がない。承認だけが仕事になる。

### UC-2: pinned folderで作業していたら、勝手に提案が積まれる

1. 日中、pinned folderの資料を編集したり、development配下でコミットしたりして普通に作業する (Curiaは開かない)
2. 数時間後のスキャンで、FocusSignalCollectorが「MyProjectのシグナルがcurrent_focus.mdより新しい」ことを検出
3. 裏で提案が生成され、バルーン通知「New AI proposal - MyProject: ...」が一瞬表示される
4. ユーザーはすぐに反応しなくてよい。提案はインボックスに残り続け、バッジが件数を示す

ポイント: 通知は割り込みではなく「溜まっている」ことの表示。無視しても失われない。

### UC-3: 提案が古くなったら勝手に消える

1. インボックスにMyProjectのFocus更新提案がPendingで残っている
2. ユーザーがEditorで手動でcurrent_focus.mdを編集してしまう
3. 次にインボックスを表示したとき、その提案はExpiredになっており、Acceptできない (適用事故が起きない)
4. 手動更新後も活動が続けば、次のスキャンで新しい提案が生成し直される

ポイント: 手動運用とAI提案が競合しない。どちらを使ってもよい。

### UC-4: 数日放置しても提案が負債化しない

1. 忙しくて3日間レビューしなかった
2. Pending上限とMaxPerDayにより、インボックスには数件しか溜まっていない (古いものはExpiredに落ちている)
3. 週明けに最新の提案だけを見ればよく、「大量の未処理」に直面しない

ポイント: 溜め込み防止は設計側の責務。ユーザーの規律に依存しない。

### UC-5: AI機能を切っている間は完全に沈黙する

1. SettingsでAiEnabledをオフにする (またはProposal Inbox自体をオフ)
2. スケジューラは停止し、バッジ・カード・バルーンはすべて消える
3. LLM APIは一切呼ばれない

## 提案生成のインプット

AIが裏で勝手に動くとなると、まず気になるのは「何を読ませているのか」だろう。答えは、既存のUpdate Focusフローと完全に同じである。FocusUpdateService.GenerateProposalAsync がLLMプロンプトに詰める材料は以下の2系統で、本機能はこれに何も足さない。

### コンテキストファイル (中身を全文読む)

| インプット | 取得元 | 内容 |
|---|---|---|
| current_focus.md | プロジェクトの _ai-context | 更新対象そのもの。現在の全文 |
| tasks.md | obsidian_notes配下 (プロジェクト直下 + 各workstream) | Asana同期済みタスク。AsanaTaskParserで構造化 |
| project_summary.md | _ai-context/context | プロジェクト概要 (存在する場合のみ) |

### 行動シグナル (FocusSignalCollectorService, ローカルI/OのみでLLMは呼ばない)

収集期間は「最後のfocus_historyスナップショット以降」または「lookback日数 (設定FocusSignalLookbackDays, デフォルト14日)」の新しい方。

| シグナル | 取得元 | LLMに渡る情報 | 上限 |
|---|---|---|---|
| pinned folderのファイル活動 | pinned_folders.json のフォルダを再帰スキャン (深さ6, 500ファイル/フォルダ, node_modules等除外) | 相対パス + 更新日時 + フォルダラベル。ファイルの中身は読まない | 新しい順20件 |
| 日付付き作業フォルダ | shared/_work/{yyyy}/{yyyyMM}/{yyyyMMdd}_{feature} (workstream構造も対応) | 日付 + feature名 + workstreamラベル | 新しい順20件 |
| gitコミット | development/source 配下の全リポジトリで git log --since | 日付 + コミットメッセージ + リポジトリ名 | 新しい順20件 |
| 未コミットファイル | 同上で git status --porcelain | ファイル名のみ | 重複除去のみ |
| キャプチャログ | [config_dir]/capture_log.md のうちプロジェクトのエイリアスに合致する見出し | タイムスタンプ + 本文 (500文字まで) | 新しい順10件 |

- シグナル全体で約8000文字のサイズ上限があり、超過時は captures → pinned files → work folders → commits → uncommitted の順に末尾から削られる
- 要点: ファイル内容を読むのはコンテキストファイル3種のみ。行動シグナルは「何を・いつ触ったか」のメタデータであり、LLMはファイル名と日付から作業内容を推測する

## 設計方針

- 生成ロジックは一切新造しない。FocusUpdateService / FocusSignalCollectorServiceをそのまま呼ぶ。新規に作るのは「いつ呼ぶか (スケジューラ)」「どこに溜めるか (インボックス)」「どう知らせるか (バッジ)」の3つだけ
- LLMコスト制御を選定ロジックに組み込む: シグナルがfocusより新しいプロジェクトのみ対象、1日あたりの生成上限、同一プロジェクトのPending提案が残っている間は再生成しない
- 提案は陳腐化する。生成後にcurrent_focus.mdが手動更新されたらExpiredに落とす (適用事故防止)
- 初期スコープはFocus更新提案のみ。DecisionLog/MeetingFollowupの提案タイプはPhase 6で拡張 (ProposalTypeをenumにして拡張余地だけ確保)

## 用語

| 用語 | 意味 |
|---|---|
| Proposal | AIが生成した1件の変更提案。現状はFocus更新のみ |
| Pending | 未レビューの提案。バッジ件数の対象 |
| Accept | 提案を適用。ApplyProposalAsyncを通りfocus_historyにスナップショット保存 |
| Reject | 適用せずアーカイブ |
| Expired | 生成後にターゲットファイルが手動更新され無効化された提案 |

## Phase 0: データモデルと永続化

### 1. Models/ProposalModels.cs (新規)

```csharp
public enum ProposalType { FocusUpdate }          // 将来: DecisionLog, MeetingFollowup
public enum ProposalStatus { Pending, Accepted, Rejected, Expired }

public class ProposalItem
{
    public string Id { get; set; }                // GUID
    public ProposalType Type { get; set; }
    public ProposalStatus Status { get; set; }
    public string ProjectName { get; set; }
    public string? WorkstreamId { get; set; }
    public string Title { get; set; }             // 例: "MyProject: Focus update (general)"
    public string Summary { get; set; }           // FocusUpdateResult.Summary
    public DateTime CreatedAt { get; set; }
    public string TargetFocusPath { get; set; }
    public DateTime TargetFileLastWriteAt { get; set; }  // 陳腐化検出用
    public string CurrentContent { get; set; }
    public string ProposedContent { get; set; }
    public string? BackupPath { get; set; }
}
```

- FocusUpdateResult (FileUpdateProposal派生) から変換して保存する。逆にAccept時はProposalItemからFocusUpdateResult相当を復元する
- [x] ProposalModels.cs を作成
- [x] FocusUpdateResult → ProposalItem 変換 (ProposalItem.FromFocusResult 静的メソッド)

### 2. Services/ProposalInboxService.cs (新規)

- 保存先: `[config_dir]\proposals\{Id}.json` (ConfigServiceのディレクトリ解決を利用)
- アーカイブ: Accept/Reject/Expired時に `proposals\_archive\` へ移動。30日超のアーカイブは起動時に削除
- [x] LoadPendingAsync / LoadAllAsync (JSON読み込み、破損ファイルはスキップしてログ)
- [x] AddAsync (保存 + Message発行)
- [x] UpdateStatusAsync (状態遷移 + アーカイブ移動 + Message発行)
- [x] PendingCount プロパティ (キャッシュ)
- [x] ExpireStaleAsync: Pending提案のTargetFocusPathのLastWriteTimeがTargetFileLastWriteAtより新しければExpiredへ (起動時とインボックス表示時に実行)
- [x] Messages.cs (既存のMessage定義場所) に `ProposalInboxChangedMessage(int PendingCount)` を追加
- [x] App.xaml.cs にシングルトン登録

## Phase 1: バックグラウンド生成 (ProposalSchedulerService)

### 1. AppConfig 設定追加 (Models/AppConfig.cs)

- [x] `ProposalInboxEnabled` (bool, default false)
- [x] `ProposalScanIntervalHours` (int, default 4)
- [x] `ProposalMaxPerDay` (int, default 3)
- [x] `ProposalBalloonEnabled` (bool, default true)
- [x] `_config\settings.json.example` を更新

### 2. Services/ProposalSchedulerService.cs (新規)

StandupGeneratorServiceのTimerパターン (System.Threading.Timer + IDisposable) を踏襲。

対象プロジェクトの選定条件 (すべて満たすもの):

- ProjectDiscoveryServiceのキャッシュに存在し、current_focus.mdを持つ
- FocusSignalCollectorServiceのシグナル (lookback内のファイル更新/コミット/capture) が存在する
- 最新シグナルの日時 > current_focus.mdのLastWriteTime (focusが実活動より古い)
- 同一プロジェクト+workstreamのPending提案が存在しない
- 当日の生成数が ProposalMaxPerDay 未満

チェックリスト:

- [x] Timerで ProposalScanIntervalHours ごとに ScanAndGenerateAsync を実行
- [x] AiEnabled == false または ProposalInboxEnabled == false なら即return
- [x] SemaphoreSlim(1,1) で多重実行防止、生成は1プロジェクトずつ直列
- [x] 選定ロジック実装 (上記条件)
- [x] FocusSignalCollectorServiceでシグナル収集 → GenerateProposalAsync(project, null, ct, signals: signals) を呼び出し
- [x] 結果をProposalInboxService.AddAsyncへ。例外はcatchしてDebugログのみ (UIを妨げない)
- [x] 当日生成数のカウント (proposalsフォルダのCreatedAtから算出、別ファイル不要)
- [x] App.xaml.cs にDI登録、起動時に StartScheduler
- [x] Settings変更時 (ProposalInboxEnabled / interval) にスケジューラ再起動 (HotkeyServiceの再登録パターン参照)
- [x] AiEnabledChangedMessage 受信で停止/再開

## Phase 2: 通知UI (トレイバッジ + バルーン)

### 1. TrayService バッジ (Services/TrayService.cs)

- [x] `UpdateBadge(int count)` を追加: CreateDiamondIconを拡張し、count > 0 のとき右下にオレンジのドット (件数1桁なら数字入り) を描画したIconへ差し替え
- [x] Iconハンドルのリーク防止: 既存のDeleteObjectパターンに加え、差し替え前のIconをDispose
- [x] ContextMenuStripに「Proposals (N)」項目を追加。0件時はDisabled。クリックでウィンドウ表示 + Dashboardへ遷移 (OnActivatedと同経路)
- [x] ツールチップテキストに件数を反映

### 2. 通知の配線

- [x] MainWindowViewModel (または App.xaml.cs) で ProposalInboxChangedMessage を受信し TrayService.UpdateBadge を呼ぶ
- [x] 新規追加時のみ ShowBalloonTip("New AI proposal", "{ProjectName}: {Summary}") を表示 (ProposalBalloonEnabled時)
- [x] 起動時に PendingCount で初期バッジを反映

## Phase 3: レビューUI (Dashboard)

### 1. Proposal Inbox カード (Views/Pages/DashboardPage.xaml + DashboardViewModel)

- [x] DashboardViewModelに `ObservableCollection<ProposalItem> PendingProposals` と読み込み処理 (表示前にExpireStaleAsyncを実行)
- [x] Dashboard上部にカードを追加。Pending 0件時はカードごと非表示 (Visibility binding)
- [x] 行の表示: ProjectName / Title / CreatedAt (相対表示: "3h ago") / Summary先頭行
- [x] ProposalInboxChangedMessage 受信で一覧を更新

### 2. レビューフロー

- [x] 行クリック → ProposalReviewDialog.ShowAsync を再利用 (CurrentContent vs ProposedContentのdiff表示)
- [x] FocusUpdateService.ApplyProposalAsync は FocusUpdateResult を引数に取るため、(targetPath, content) ベースのオーバーロードを切り出すか、ProposalItemからFocusUpdateResultを復元するヘルパーを追加 (復元方式を推奨: 既存呼び出し側に影響なし)
- [x] Accept: 適用成功後 UpdateStatusAsync(Accepted)。適用直前にもTargetFileLastWriteAtを再チェックし、ズレていたら適用中断してExpiredへ
- [x] Reject: UpdateStatusAsync(Rejected)
- [ ] ダイアログにRefineが組み込まれている場合はFocusUpdateService.RefineAsyncへ接続 (任意、後回し可)
- [x] 適用後、EditorでそのファイルをすでにOpenしていた場合の再読み込み考慮 (既存のUpdate Focusフローと同じ挙動に合わせる)

## Phase 4: 設定UI (SettingsPage)

- [x] 「Proposal Inbox」セクション追加: 有効化トグル / スキャン間隔 (時間) / 1日の生成上限 / バルーン通知トグル
- [x] 有効化トグルは AiEnabled == true のときのみ操作可能 (AiToggleCanEnableパターン)
- [x] 保存時に即時反映 (スケジューラ再起動)

## Phase 5: 検証

- [x] `dotnet publish -p:PublishProfile=SingleFile` が通る
- [ ] AiEnabled = false で: スケジューラが動かない、バッジ非表示、Dashboardカード非表示
- [ ] 手動シナリオ: pinned folder内のファイルを更新 → スキャン間隔を短くして提案が生成される → バッジ点灯 → Dashboardカードから開いてAccept → current_focus.md更新 + focus_history保存 + バッジ消灯
- [ ] Rejectシナリオ: アーカイブへ移動し再生成されないこと (Pendingなし + シグナル/focus鮮度条件で再生成される場合はある。挙動として許容するか確認)
- [ ] 陳腐化シナリオ: 提案生成後にcurrent_focus.mdを手動編集 → インボックス表示時にExpiredになりAcceptできない
- [ ] 多重起動なし: スキャン中に次のTimer発火が来ても二重生成しない
- [ ] アプリ終了時にTimer/IconがDisposeされる

## Phase 6: 拡張 (初期スコープ外)

- [ ] DecisionLogGeneratorServiceの候補検出をスケジューラに追加 (ProposalType.DecisionLog)
- [ ] MeetingFollowupの提案化
- [ ] 提案タイプ別の有効/無効設定
- [ ] AgentChatから「この提案について相談」導線

## 設計上の論点 (実装前に判断)

- スキャンのトリガー: Timer定期実行のみか、Asana Sync完了・Capture追加をトリガーに即時スキャンも足すか。まずはTimerのみで開始し、体感が遅ければイベントトリガーを追加するのが安全
- workstream単位の提案を出すか: 初期はgeneralモード (プロジェクト全体) のみに絞り、ノイズを見てから判断
- 提案の同時保持数: MaxPerDayとは別に「Pending合計の上限 (例: 5件)」を設けるか。溜まりすぎると承認自体が負債化するため、上限超過時は古いPendingをExpiredに落とす案を推奨
