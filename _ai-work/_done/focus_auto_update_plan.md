# Focus Auto-Update - 行動シグナルからの current_focus 自動更新計画

ユーザーの実際の行動 (pinned folder のファイル活動、git 活動、capture ログ、Asana タスクの動き) からcurrent_focus.md のドラフトを自動生成し、ワンクリック承認で更新できるようにする機能。

## 背景 / 課題

- current_focus.md の手動更新は面倒で放置されがち。実運用でも更新が滞っている
- 一方で FocusUpdateService / DecisionLogGeneratorService / CuriaQueryService など多くの AI 機能が current_focus.md を文脈の起点にしており、focus が古いと出力品質が全体的に落ちる
- 既存の "Update Focus from Asana" (FocusUpdateService) は Asana タスクのみが入力で、かつ Editor ツールバーから能動的に起動する必要がある
- pinned folder と Dashboard はよく使われている。つまり「実際の関心がどこにあるか」の行動シグナルは既に溜まっており、これを入力に使えば「ゼロから書く」を「読んで承認するだけ」に変えられる

## 方針

- ユーザーに書かせない。行動シグナル + Asana タスクからドラフトを生成し、既存の ProposalReviewDialog (diff + Refine + 承認) で確認するだけにする
- 新規の並行実装は作らず、既存 FocusUpdateService の入力を拡張する (プロンプト・バックアップ・focus_history・Refine の仕組みをそのまま使う)
- トリガーは Dashboard カードの stale focus 表示に置いたワンクリックアクション。通知やポップアップは出さない (通知疲れ防止)
- AiEnabled ゲート、AiEnabledChangedMessage 購読パターンを遵守 (AGENTS.md の AI Features 規約)

## 全体フロー

```
[Dashboard カード: FocusFreshness が aging/stale + AI 有効]
  ↓ 「Auto-update focus」アクションをクリック
[FocusSignalCollectorService] 行動シグナル収集 (LLM 不使用・ローカルのみ)
  ↓
[FocusUpdateService.GenerateProposalAsync]  既存フロー + シグナルをプロンプトに追加
  ↓ (バックアップ → Asana 解析 → LLM 生成)
[ProposalReviewDialog]  diff 表示 / Refine / Accept / Reject
  ↓ Accept
[FocusUpdateService.ApplyProposalAsync]  書き込み + focus_history スナップショット
  ↓
[Dashboard カード更新 (FocusAge リセット)]
```

## シグナル設計 (FocusSignalCollectorService)

収集期間 (lookback) は「focus_history の最新スナップショット日付」を基点にし、無い場合や古すぎる場合は設定値 FocusSignalLookbackDays (既定 14 日) で打ち切る。

対象プロジェクトごとに以下を収集する。すべてローカル I/O のみで LLM は呼ばない。

1. Pinned folder のファイル活動
   - ConfigService.LoadPinnedFolders() から PinnedFolder.Project が対象プロジェクトに一致するものを抽出
   - 各フォルダ配下を EnumerateFiles で走査し、lookback 内に更新されたファイルを更新日時降順で上位 N 件 (既定 20 件)
   - 出力: 相対パス + 更新日時。ファイル内容は読まない (トークン節約と誤解釈防止)
   - 除外: node_modules、.git、bin、obj、~$ 一時ファイル等。走査は深さ・件数上限つき
2. Git 活動
   - プロジェクト配下のリポジトリ (ProjectDiscoveryService が uncommitted 検出に使っているのと同じ場所) に対して
     git log --since=<lookback> --pretty=format:"%ad %s" --date=short (上位 20 件) と git status --porcelain (ファイル名のみ) を実行
   - git.exe が無い・リポジトリでない場合はスキップ (既存の検出処理と同じ扱い)
3. capture_log.md のエントリ
   - ConfigService.ConfigDir/capture_log.md を "## yyyy-MM-dd HH:mm" 見出しでエントリ分割
   - lookback 内かつ本文にプロジェクト名または AnkenAliases (CaptureService.GetAliases 相当) を含むものを上位 10 件
4. 収集結果全体にサイズ上限 (合計文字数上限、既定 8,000 chars 程度) を設け、超過分は新しい順に優先して切り捨てる

## モデル

Models/FocusSignalModels.cs (新規):

```csharp
public class FocusActivitySignals
{
    public DateTime Since { get; set; }
    public List<FileActivityEntry> PinnedFolderFiles { get; set; } = [];
    public List<GitCommitEntry> RecentCommits { get; set; } = [];
    public List<string> UncommittedFiles { get; set; } = [];
    public List<CaptureLogEntry> Captures { get; set; } = [];
    public bool IsEmpty => ...; // 全リストが空
}
```

- FileActivityEntry: RelativePath / ModifiedAt / PinnedFolderLabel
- GitCommitEntry: Date / Message / RepoName
- CaptureLogEntry: Timestamp / Body (トリム済み)

## FocusUpdateService の変更

- GenerateProposalAsync に省略可能パラメータ FocusActivitySignals? signals = null を追加 (既存呼び出し元は無変更で動作)
- BuildUserPrompt に以下のセクションを追加 (signals が非 null かつ IsEmpty でない場合のみ):
  - "## Recent file activity (pinned folders — what the user actually touched)"
  - "## Recent git activity (commits since <date> / uncommitted files)"
  - "## Recent quick captures mentioning this project"
- BuildSystemPrompt にシグナル利用ルールを追記:
  - 活動シグナルは「実際に何に取り組んでいたか」の判断と "What I'm working on" の鮮度・優先順の根拠に使う
  - ファイル名やコミットメッセージから作業内容を断定しない。Asana タスクや既存記述と対応づくものを優先し、対応が取れない曖昧なシグナルは書かずにスキップする
  - シグナルにしか現れない活動を追記する場合は、ファイル名の直訳ではなく自然な短文にする (既存の "rephrase" ルールと同じ扱い)

## エントリポイント

1. Dashboard カード (メイン)
   - FocusFreshness が "aging" | "stale" かつ IsAiEnabled のとき、focus age 表示の隣にアクション (アイコンボタンまたはコンテキストメニュー項目 "Auto-update focus") を表示
   - クリックで収集 → 生成 → ProposalReviewDialog (owner: MainWindow、refineFunc は FocusUpdateService.RefineAsync を接続)
   - 実行中はカード上にスピナー表示。CancellationTokenSource でキャンセル可能にする
   - Accept 後: ProjectDiscoveryService のキャッシュを無効化してカードを再読み込みし、FocusAge の更新を反映
2. Editor ツールバーの既存 "Update Focus from Asana"
   - EditorViewModel でもシグナルを収集して渡すよう変更。既存フローが自動的に強化される
3. Agent ツール update_current_focus (PhaseCAgentTools)
   - 同様にシグナルを注入 (Phase 5)

## 設定 (AppConfig)

- FocusAutoUpdateBadgeEnabled (bool, 既定 true): Dashboard 上のアクション表示の有効/無効
- FocusSignalLookbackDays (int, 既定 14): シグナル収集期間の上限
- stale 判定しきい値は既存の GetFreshness ロジックをそのまま使い、新設しない
- SettingsPage の AI セクションに 2 項目を追加。settings.json.example も更新

## 実装タスク

### Phase 1: シグナル収集基盤

- [ ] 1-1. Models/FocusSignalModels.cs を新規作成
- [ ] 1-2. Services/FocusSignalCollectorService.cs を新規作成
  - CollectAsync(ProjectInfo project, CancellationToken ct) : Task<FocusActivitySignals>
  - 依存: ConfigService (pinned folders, capture_log, settings), FileEncodingService
  - I/O は Task.Run でバックグラウンド実行。個別シグナルの失敗は握りつぶして続行 (部分的な結果でよい)
- [ ] 1-3. App.xaml.cs にシングルトン登録

### Phase 2: FocusUpdateService 統合

- [ ] 2-1. GenerateProposalAsync に signals パラメータを追加
- [ ] 2-2. BuildUserPrompt にシグナルセクションを追加
- [ ] 2-3. BuildSystemPrompt に利用ルールを追記
- [ ] 2-4. dotnet build で既存呼び出し元 (EditorViewModel, PhaseCAgentTools) が壊れていないこと確認

### Phase 3: Dashboard エントリポイント

- [ ] 3-1. DashboardViewModel に IsAiEnabled ([ObservableProperty]) と AiEnabledChangedMessage 購読を追加 (未実装の場合)
- [ ] 3-2. AutoUpdateFocusCommand (AsyncRelayCommand<ProjectCardViewModel>) を追加
  - 収集 → 生成 → ProposalReviewDialog.ShowAsync → ApplyProposalAsync → キャッシュ無効化 + リフレッシュ
  - 例外時は Snackbar / メッセージ表示 (既存のエラーハンドリングパターンに合わせる)
- [ ] 3-3. DashboardPage.xaml のプロジェクトカードにアクション UI を追加
  - 表示条件: IsAiEnabled && FocusAutoUpdateBadgeEnabled && FocusFreshness in (aging, stale)
  - 実行中スピナーとキャンセル
- [ ] 3-4. workstream を持つプロジェクトの扱い: v1 は general モード (プロジェクト全体) 固定とし、既存の workstream 選択ダイアログは出さない

### Phase 4: 設定

- [ ] 4-1. Models/AppConfig.cs に FocusAutoUpdateBadgeEnabled / FocusSignalLookbackDays を追加
- [ ] 4-2. SettingsViewModel / SettingsPage.xaml に設定 UI を追加
- [ ] 4-3. _config/settings.json.example を更新

### Phase 5: 仕上げ

- [ ] 5-1. EditorViewModel の既存 Update Focus フローにシグナル注入
- [ ] 5-2. PhaseCAgentTools.UpdateCurrentFocusTool にシグナル注入
- [ ] 5-3. Command Palette に "Auto-update focus" コマンド追加 (プロジェクト選択つき)
- [ ] 5-4. dotnet publish -p:PublishProfile=SingleFile で最終確認

## 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| Models/FocusSignalModels.cs | 新規: シグナルモデル |
| Services/FocusSignalCollectorService.cs | 新規: 行動シグナル収集 |
| Services/FocusUpdateService.cs | signals パラメータ + プロンプト拡張 |
| App.xaml.cs | サービス登録 |
| ViewModels/DashboardViewModel.cs | AutoUpdateFocusCommand + AI ゲート |
| Views/Pages/DashboardPage.xaml(.cs) | カード上のアクション UI |
| ViewModels/EditorViewModel.cs | 既存フローへのシグナル注入 |
| Services/Agent/Tools/PhaseCAgentTools.cs | Agent ツールへのシグナル注入 |
| Models/AppConfig.cs | 設定 2 項目追加 |
| ViewModels/SettingsViewModel.cs, Views/Pages/SettingsPage.xaml | 設定 UI |
| _config/settings.json.example | 設定サンプル更新 |

## リスクと対策

- ファイル名からの幻覚 (やってもいない作業を書く): システムプロンプトで「シグナル単独からの断定禁止・曖昧ならスキップ」を明示。最終的に必ず人間が diff 承認するため実害は限定的
- 大きな pinned folder の走査コスト: EnumerateFiles + 件数/深さ上限 + 除外リスト。収集は Task.Run で UI をブロックしない
- トークン量の膨張: シグナル全体に文字数上限。ファイル内容は読まずパスとメタデータのみ
- capture_log にプロジェクト言及が無い / git 不在: 該当セクションを省略するだけで動作に影響なし
- Asana 未使用プロジェクト: tasks.md が無くても既存実装が空扱いで動く。シグナルのみでの生成も成立する

## 検証

- dotnet build Curia.csproj で型チェック
- dotnet publish -p:PublishProfile=SingleFile
- 手動シナリオ:
  1. focus が stale なプロジェクトの Dashboard カードにアクションが出ること (AI 無効時は出ないこと)
  2. 実行して diff に活動シグナル由来の内容が反映されること
  3. Refine → Accept → current_focus.md 更新 + focus_history/yyyy-MM-dd.md 生成
  4. カードの FocusAge が 0d に更新されること
  5. pinned folder が無い / git が無いプロジェクトでもエラーにならないこと
