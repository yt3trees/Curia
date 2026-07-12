# Curia AI エージェントチャット - 仕様案

## 実装状況 (2026-07-12)

Phase A / Phase B の基盤実装と、Phase C の主要機能は完了している。ビルドおよび single-file publish は成功済み。

- Markdig.Wpf を導入し、アシスタント回答を Markdown として描画する。
- 書き込みツールにはチャット内の Approve / Reject カードを表示する。同一ツールのセッション内自動承認も利用できる。
- セッションは `%CONFIG%/agent_chat_history/` へ保存する。User / Assistant / ToolCall / ToolResult を保存し、履歴を開いたときにもツール結果を確認できる。
- Agent Chat を開くと最新セッションを自動復元する。仕様上の「起動時は新規セッション」とは異なる現行動作であり、履歴一覧・任意セッション再開は Phase C で扱う。
- Command Palette の `?` 入力は Agent Chat へ遷移して自動送信する。旧 Ask Curia の表示経路は利用しないが、旧 UI 関連コードの物理削除と `CuriaQueryService` の UI 専用 API 整理は未完了。
- パスガードは管理ルート、`..`、ルート境界を検証する。junction / symbolic link の実体解決による逸脱検出は追加対応が必要。
- `capture_note` は現在グローバル `capture_log.md` への追記であり、プロジェクト固有ファイルへのルーティングは未実装。
- `get_schedule` は Curia の手動スケジュールブロックを返す。Outlook / ICS イベントの統合は未実装。
- `search_wiki` は現状 Wiki タイトル検索として実装している。ページ本文を含む検索・WikiQueryService 連携は追加対応が必要。
- Phase C: `update_current_focus` と `append_decision_log` は Agent ツールとして統合済み。通常の Approve / Reject に加え、既存の差分レビュー画面で最終 Apply / Skip を要求する。
- Phase C: 履歴一覧、任意セッション再開、履歴削除、および固定プリセット "Morning preparation" を実装済み。
- Phase C: `complete_task` は Asana の完了操作後に 15 分間有効な Undo token を返す。Undo token はアプリ内メモリのみで保持し、アプリ再起動後は利用できない。
- Phase C: `openai` / `azure_openai` は native function calling (`tools` / `tool_calls`) を利用する。CLI プロバイダは既存のプロンプトベース JSON プロトコルを維持する。Compatibility Check はプロバイダに応じて native echo probe または JSON probe を実行する。
- 2026-07-12 のコードレビューで、CLI transport の権限制御、Phase C ツールのパス検証、セッション競合などに未修正の問題が確認された。詳細は「コードレビュー結果と修正バックログ」を参照。

チャット UI から自然文で指示すると、AI エージェントが Curia の各サービスを「ツール」として呼び出し、データ取得・操作を代行する機能。

例:
- 「今日やるべきタスクを整理して」→ get_today_tasks + get_schedule を呼び、優先順位付きで回答
- 「Alpha プロジェクトの DB 方針の決定事項を教えて」→ search_decision_logs で検索して引用付き回答
- 「"見積レビュー" というタスクを Alpha に明日期限で追加して」→ create_task を提案 → ユーザー承認 → 実行
- 「今週のスタンドアップを作り直して」→ generate_standup 実行

## コンセプト

```
ユーザー (チャット入力)
     │
     ▼
AgentOrchestratorService  (エージェントループ)
  ├─ システムプロンプト + ツール定義一覧 + 会話履歴を LLM へ
  ├─ LLM 応答をパース
  │    ├─ tool_call → AgentToolRegistry から該当ツールを実行
  │    │     ├─ 読み取り系: 即実行、結果を履歴に追加して再度 LLM へ
  │    │     └─ 書き込み系: UI で承認カード表示 → 承認後に実行
  │    └─ final_answer → チャットに回答表示、ループ終了
  └─ 最大反復回数 / CancellationToken で暴走防止
     │
     ▼
各ツール = 既存サービスの薄いラッパー
(ProjectDiscoveryService, TodayQueueService, CuriaQueryService, CaptureService, ...)
```

- 既存の Ask Curia (CommandPalette の `?` モード) は本機能に一本化して削除する。CuriaQueryService 自体はエージェントのツール (ask_knowledge_base) のバックエンドとして存続 (D6 参照)
- AI 機能ガード (AiEnabled) に乗せる。既存パターン (IsAiEnabled + AiEnabledChangedMessage) を踏襲
- LLM 呼び出しは LlmClientService 経由 (直接 HTTP 禁止のリポジトリ規約を遵守)

## 用語

| 用語 | 意味 |
|---|---|
| ツール | エージェントが呼び出せる 1 機能。名前 + 説明 + パラメータスキーマ + 実行処理 |
| エージェントループ | LLM 応答→ツール実行→結果返却を final_answer まで繰り返す処理 |
| 承認カード | 書き込み系ツールの実行前にチャット内に表示する Approve / Reject UI |
| セッション | 1 つの会話履歴 (マルチターン)。JSON で永続化 |

---

## Phase 0: 設計上の論点と方針

### D1. ツール呼び出しプロトコル: プロバイダ別 transport を採用

CLI 系プロバイダ (claude_code / gemini_cli / codex_cli / github_copilot) は stdin/stdout のテキストしか扱えないため、プロンプトベース JSON プロトコルを利用する。`openai` / `azure_openai` は native function calling を利用し、`tools` / `tool_calls` を transport 層で `AgentToolCall` に正規化する。

- システムプロンプトにツール定義一覧 (名前 / 説明 / パラメータの JSON Schema 風記述) を埋め込む
- LLM には必ず次のいずれかの JSON のみを返させる:

```json
{"type": "tool_call", "tool": "get_today_tasks", "arguments": {"bucket": "overdue"}, "reason": "期限切れタスクの確認"}
{"type": "final_answer", "text": "回答本文 (Markdown)"}
```

ツール定義 (`ICuriaAgentTool`) は両経路で共通利用する。native 経路では `LlmClientService.ChatWithToolsAsync` が API ペイロードと応答を変換し、`AgentOrchestratorService` が承認・実行・tool result のループを共通処理する。

### D1-2. JSON 遵守率対策 (CLI プロバイダ、特に github_copilot)

github_copilot を含む CLI プロバイダはプレーンテキストしか返せないため、プロンプト遵守だけに頼らず「寛容な抽出 + 検証 + 互換性チェック」の三段構えで担保する。

1. プロンプト側の工夫 (遵守率を上げる)
   - システムプロンプトに tool_call と final_answer の few-shot 例を各 1 つ入れる
   - "Your entire reply must be a single JSON object starting with `{`. No prose, no code fences." を明示
   - 毎ターンのユーザーメッセージ末尾に "Reply with one JSON object only." のリマインダを付加 (長い会話での指示忘れ対策)
2. パーサ側の工夫 (多少の逸脱を吸収する)
   - コードフェンス (```json ... ```) は除去してからパース
   - 応答が「前置き + JSON」の混在でも、最初のバランスした `{...}` ブロックを走査で抽出して試行 (AgentProtocol.TryExtractJson)
   - 抽出した JSON に `type` フィールドがない / 未知の値 → リトライメッセージ ("Return exactly one JSON object of type tool_call or final_answer.") を 1 回だけ挟む
   - リトライでも失敗 → 応答全文を final_answer として扱う (会話は壊さない)
3. Agent Compatibility Check (プロバイダ実力の事前検証)
   - Settings > LLM API に "Test Agent Compatibility" ボタンを追加 (Test Connection と同列)
   - 内容: 最小ツール定義 (echo ツール 1 個) を渡し、(a) ツール呼び出しを要する質問 → tool_call JSON が返るか、(b) ツール結果を与えて → final_answer JSON が返るか、の 2 プローブを実行
   - 2 プローブとも成功したら `AgentCompatibilityOk = true` を settings に保存 (provider + model の組で記録し、変更されたら再チェック要求)
   - AgentChatPage は `AiEnabled && AgentCompatibilityOk` のときのみ表示。不合格プロバイダには "This provider/model did not pass the agent compatibility check." を表示
   - AiEnabled のガードパターン (Test Connection 成功後のみ ON) と同じ思想で、失敗するプロバイダでの中途半端な動作を未然に防ぐ

github_copilot が中継するモデル (GPT-4o / 4.1 クラス) は few-shot 付き JSON 指示への遵守率が実用水準にあるため、この構えで対応可能と判断する。gemini_cli / codex_cli も同じ仕組みでチェックに通れば自動的に使える (プロバイダ別の特殊対応コードは書かない)。

### D2. 書き込み系ツールは必ずユーザー承認を挟む

- ツールに ToolRiskLevel (ReadOnly / Write / Dangerous) を持たせる
- ReadOnly: 即実行
- Write: チャット内に承認カード (ツール名 + 引数のプレビュー + Approve / Reject) を表示し、承認された場合のみ実行。Reject 時は「ユーザーが拒否した」ことをツール結果として LLM に返し、ループ続行 (代替案を提案できる)
- Dangerous (ファイル削除、スクリプト実行など): 初期リリースではツール自体を登録しない
- 「このセッション中は同種ツールを自動承認」トグルを承認カードに付ける (Claude Code の許可プロンプトと同じ UX)

### D3. ファイルアクセスはマネージドルート内に限定

read_file / append_file / write_file 系ツールは、パスガードを共通実装する。

- 許可: Local Projects Root / Box Root / Obsidian Vault Root / Curia config dir 配下のみ
- 拒否: 上記以外の絶対パス、`..` を含む相対パス、シンボリックリンク経由の逸脱
- ガード違反はツール実行エラーとして LLM に返す (例外でループを落とさない)

### D4. UI 操作系ツールはコールバック委譲

「Editor でこのファイルを開いて」のような UI 操作は、既存の Cross-page navigation パターン (OnOpenInEditor / OnOpenInTimeline コールバック) を踏襲する。ツール → ViewModel コールバック → MainWindow ナビゲーション。ツールから INavigationService や Page を直接触らない。UI スレッドへのディスパッチはツール側で行う。

### D5. コンテキストサイズ管理

- ツール結果は 1 件あたり最大 30,000 文字に切り詰め (超過時は末尾に「...truncated」を付ける)
- 会話履歴が長くなったら古いツール結果から間引く (直近 N ターン + 各 final_answer は残す)
- エージェントループの最大反復は 10 回 (設定可能)。到達したら「ここまでの調査結果」で強制 final_answer を生成させる

### D5-2. システムプロンプトへのプロジェクト一覧の事前注入

プロジェクト名のグラウンディング (ユーザーが略称で言及した場合の解決) のため、コンパクトな一覧のみを毎ターン注入する。

- 注入内容: `name (tier/category)` の 1 行形式のみ。focus 経過日数や git 状態などの詳細は含めない (詳細は list_projects ツールで取得)
- 上限: 50 プロジェクトまで。超過分は最近更新順で切り、"...and N more (use list_projects)" を付記
- ProjectDiscoveryService の既存キャッシュ (5 分 TTL) をそのまま使い、注入のための追加スキャンはしない
- これによりトークン消費は 1 ターンあたり数百トークンに抑えつつ、「Alpha の件」のような曖昧な言及を最初のツール呼び出しなしで解決できる

### D6. 既存 Ask Curia (`?` モード) の削除と一本化

CommandPalette の `?` プレフィックス QA は本機能に一本化して削除する。

方針:

- CuriaQueryService と ICuriaSourceAdapter 群は削除しない。ask_knowledge_base ツールのバックエンドとしてそのまま使う (2 段階選定ロジックは横断検索ツールとして最適)
- 削除するのは UI 層のみ: CommandPaletteViewModel の AskMode 関連プロパティ / コマンド、CommandPaletteOverlay の回答パネル
- `?` プレフィックス自体は残し、入力テキストを引き継いで AgentChatPage へ遷移する導線に置き換える (`?昨日の決定事項` → Agent チャットに質問がプリセットされ自動送信)
- WikiPage の Wiki 専用チャット (WikiQueryService 統合分) は対象外。Wiki ページ内の QA はそのまま維持する

移行手順 (バグ防止):

- Step 1: AgentChatPage が Phase A で安定動作することを確認
- Step 2: `?` 入力時の遷移導線を実装 (この時点で旧回答パネルと併存)
- Step 3: 旧 AskMode UI を削除 (CommandPaletteViewModel / CommandPaletteOverlay)
- Step 4: CuriaQueryService の公開 API のうち UI 専用だったもの (セッション履歴等) を整理

---

## Phase 1: データモデルとツール基盤

### Models/AgentChatModels.cs (新規)

```csharp
namespace Curia.Models;

public enum ToolRiskLevel { ReadOnly, Write, Dangerous }

public class AgentToolDescriptor
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";       // LLM に見せる説明 (いつ使うかを含める)
    public string ParametersSchema { get; set; } = "";   // JSON Schema 風のテキスト
    public ToolRiskLevel RiskLevel { get; set; }
}

public class AgentToolCall
{
    public string Tool { get; set; } = "";
    public JsonObject Arguments { get; set; } = new();
    public string Reason { get; set; } = "";
}

public class AgentToolResult
{
    public bool Success { get; set; }
    public string Content { get; set; } = "";            // LLM に返すテキスト (JSON or Markdown)
    public string? DisplaySummary { get; set; }           // チャット UI に出す短い要約
}

public enum AgentMessageKind { User, Assistant, ToolCall, ToolResult, Approval, Error }

public class AgentChatMessage
{
    public AgentMessageKind Kind { get; set; }
    public string Text { get; set; } = "";
    public AgentToolCall? ToolCall { get; set; }
    public DateTime Timestamp { get; set; }
}
```

### Services/Agent/ICuriaAgentTool.cs (新規)

```csharp
public interface ICuriaAgentTool
{
    AgentToolDescriptor Descriptor { get; }
    Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct);
}
```

### Services/Agent/AgentToolRegistry.cs (新規)

- `IEnumerable<ICuriaAgentTool>` を DI で受け取り、名前引きの辞書を構築
- `BuildToolsPrompt()`: 全ツールの Descriptor をシステムプロンプト用テキストに整形
- `TryGet(name, out tool)`: オーケストレータからの解決
- App.xaml.cs で各ツールを singleton 登録 (ICuriaSourceAdapter と同じパターン)

---

## Phase 2: ツールカタログ (案)

### 読み取り系 (ReadOnly) - Phase A で実装

| ツール名 | 内容 | 流用サービス |
|---|---|---|
| list_projects | プロジェクト一覧 (名前 / tier / category / focus 経過日数 / 未コミット有無) | ProjectDiscoveryService |
| get_today_tasks | 今日のタスクキュー (overdue / today / soon / normal バケット指定可) | TodayQueueService |
| get_project_tasks | 指定プロジェクトの tasks.md をパースして返す (workstream / 完了フラグでフィルタ) | AsanaTaskParser |
| read_current_focus | 指定プロジェクトの current_focus.md 全文 | ContextCompressionLayerService |
| read_project_summary | 指定プロジェクトの project_summary.md 全文 | ContextCompressionLayerService |
| search_decision_logs | 決定ログをキーワード / プロジェクト / 期間で検索 | DecisionLogService |
| ask_knowledge_base | 横断 QA (Ask Curia をそのまま 1 ツール化)。曖昧な知識質問はこれに委譲 | CuriaQueryService |
| search_wiki | Wiki ページの検索・取得 | WikiService / WikiQueryService |
| get_schedule | 今日 / 今週の予定 (カレンダー連携済みイベント) | ScheduleService / OutlookCalendarService |
| get_team_tasks | チームメンバー別タスク一覧 | TeamTaskParser |
| get_standup | 最新の生成済みスタンドアップを読む | StandupGeneratorService |
| get_state_snapshot | curator_state.json 相当の全体状態 (プロジェクト + タスクの俯瞰) | StateSnapshotService |
| read_file | マネージドルート内の任意ファイル読み取り (D3 ガード付き) | FileEncodingService |
| get_open_issues | 指定プロジェクトの open_issues.md | (glob + FileEncodingService) |

### 書き込み系 (Write、承認カード必須) - Phase B で実装

| ツール名 | 内容 | 流用サービス |
|---|---|---|
| create_task | Asana タスク作成 (Capture の分類ロジックを利用、dedup ガードあり) | CaptureService |
| capture_note | フリーフォームメモを対象プロジェクトのファイルに追記 | CaptureService |
| update_current_focus | current_focus.md の更新提案を生成 (既存の ProposalReviewDialog で diff 承認) | FocusUpdateService |
| append_decision_log | 決定ログのドラフト生成と追記 | DecisionLogGeneratorService |
| append_to_file | マネージドルート内の Markdown への追記 (D3 ガード付き) | FileEncodingService |
| sync_asana | Asana 同期を今すぐ実行 | AsanaSyncService |
| generate_standup | スタンドアップを再生成 | StandupGeneratorService |

### UI 操作系 (ReadOnly 扱い、副作用は画面遷移のみ) - Phase B で実装

| ツール名 | 内容 | 実現方法 |
|---|---|---|
| open_in_editor | 指定ファイルを Editor ページで開く | OnOpenInEditor コールバック (D4) |
| open_in_timeline | 指定プロジェクトを Timeline で開く | OnOpenInTimeline コールバック |
| navigate_to_page | Dashboard / Wiki / Schedule 等へ遷移 | MainWindowViewModel 経由 |
| start_pomodoro | ポモドーロ開始 (タスク名指定可) | PomodoroService |

### 初期リリースで登録しないもの (Dangerous)

- ファイル削除 / 上書き (write_file)
- ScriptRunnerService 経由の任意スクリプト実行
- Asana タスクの完了化・削除 (誤爆時の影響が大きい。Phase C で承認カード + Undo 検討)

---

## Phase 3: AgentOrchestratorService

### Services/Agent/AgentOrchestratorService.cs (新規)

```csharp
public class AgentOrchestratorService
{
    public AgentOrchestratorService(
        LlmClientService llm,
        ConfigService configService,
        AgentToolRegistry registry);

    // approvalCallback: Write ツールの承認を UI に問い合わせる (true=承認)
    // progressCallback: ツール実行状況をチャット UI に逐次通知
    public async Task<AgentChatMessage> RunTurnAsync(
        List<AgentChatMessage> history,
        string userInput,
        Func<AgentToolCall, Task<bool>> approvalCallback,
        Action<AgentChatMessage> progressCallback,
        CancellationToken ct);
}
```

### システムプロンプト骨子

```
You are Curia Agent, an assistant embedded in a personal project management app.
You can call tools to read project data and perform actions on behalf of the user.

Rules:
- Respond with EXACTLY ONE JSON object per turn, no other text.
- To call a tool: {"type":"tool_call","tool":"<name>","arguments":{...},"reason":"<short>"}
- To answer the user: {"type":"final_answer","text":"<markdown>"}
- Call tools to gather facts BEFORE answering. Do not guess project data.
- Prefer ask_knowledge_base for open-ended "why/when/what was decided" questions.
- Write tools require user approval; if rejected, propose an alternative or ask.
- Respond to the user in {LlmLanguage}.

Today: {date}
Projects: {D5-2 のコンパクト一覧 (name + tier/category のみ、最大 50 件)}

Available tools:
{AgentToolRegistry.BuildToolsPrompt()}
```

### ループ処理

1. 履歴 + ユーザー入力から messages を構築し ChatWithHistoryAsync を呼ぶ
2. 応答 JSON をパース (フェンス除去 → JsonNode.Parse)
3. tool_call の場合:
   - registry で解決。未知ツール名ならエラー文字列をツール結果として返しループ続行
   - Write なら approvalCallback を await。拒否なら結果 = "User rejected this action."
   - ExecuteAsync を try/catch で包み、例外はエラー文字列に変換 (ループを落とさない)
   - 結果を D5 の上限で切り詰めて履歴に追加、progressCallback で UI 通知、手順 1 へ
4. final_answer の場合: AgentChatMessage を返して終了
5. 反復が MaxIterations (10) に達したら「これ以上ツールを呼ばずに現時点の情報で回答せよ」を注入して最終応答を得る
6. OperationCanceledException はそのまま伝播 (UI 側で「キャンセルしました」表示)

---

## Phase 4: UI (AgentChatPage または チャットパネル)

### 配置の選択肢

- 案 1: 新規ページ AgentChatPage (NavigationView に追加) … 推奨。履歴表示・承認カードに十分な面積が取れる
- 案 2: Dashboard 右側のドッキングパネル … 常時見えるが実装コスト高
- 案 3: CommandPalette の拡張 (`!` プレフィックス等) … 入口としては良いが承認 UI が窮屈

推奨: 案 1 をメインに、CommandPalette からの「Agent に聞く」導線 (入力を引き継いで AgentChatPage へ遷移) を後付けする。

### AgentChatViewModel (新規)

- `ObservableCollection<AgentChatMessage> Messages`
- `[ObservableProperty] string inputText`
- `[ObservableProperty] bool isRunning` (実行中は入力欄を無効化、Stop ボタン表示)
- `[ObservableProperty] bool isAiEnabled` (+ AiEnabledChangedMessage 購読、リポジトリ規約どおり)
- `[RelayCommand] SendAsync` / `[RelayCommand] Cancel` / `[RelayCommand] NewSession`
- 承認カード: Kind = Approval のメッセージに Approve / Reject コマンドをバインドし、TaskCompletionSource で approvalCallback に橋渡し

### 表示

- ユーザー / アシスタントは吹き出し表示。アシスタント側は Markdown レンダリングを最初から実装する
  - レンダラ: Markdig.Wpf を採用 (Markdig ベースで拡張性が高く、XAML スタイル辞書でテーマを差し替えられる)。代替候補は MdXaml
  - NuGet 追加が必要なため、実装着手時にユーザー承認を得ること (リポジトリ規約)
  - ダークモード対応: Markdig.Wpf の Styles を上書きし、AppSurface* / AppText ブラシに揃えたリソース辞書を用意する
  - コードブロックは等幅フォント + 背景色。リンククリックは Editor ジャンプ (マネージドルート内パス) または既定ブラウザ (http/https)
  - ユーザー側の吹き出しはプレーンテキストで良い (入力そのまま)
- ツール呼び出しは折りたたみ行で表示: `🔧 get_today_tasks {"bucket":"overdue"} → 3 tasks found` (クリックで結果全文展開)
- 実行中はツール名のインジケータ (「search_decision_logs を実行中...」)
- ダークモード対応は projectcurator-popup-window スキルの規約 (AppSurface* / AppText ブラシ) に従う。UI テキストは英語

### セッション永続化

- `%CONFIG%/agent_chat_history/{yyyy-MM-dd_HHmmss}.json` に保存
- 保存対象は User / Assistant / ToolCall (引数と要約のみ)。ツール結果全文は保存しない (サイズ抑制)
- 現行実装は起動時に最新セッションを自動復元する。履歴一覧からの任意セッション再開は Phase C

---

## Phase 5: ガードと設定

- AgentOrchestratorService.RunTurnAsync 冒頭で settings.AiEnabled を確認、false なら InvalidOperationException
- AgentChatPage のナビゲーション項目自体を IsAiEnabled で表示制御
- 設定追加 (Models/AppConfig.cs):
  - `AgentMaxIterations` (default 10)
  - `AgentToolResultMaxChars` (default 30000)
  - `AgentCompatibilityOk` (bool) + `AgentCompatibilityCheckedFor` (provider + model の文字列。現在の設定と不一致なら再チェック要求) … D1-2 参照
  - `AgentAutoApproveReadOnly` は不要 (ReadOnly は常時自動)
- CancellationToken を LlmClientService とツールの末端まで貫通させる (規約)

---

## Phase 6: エラーハンドリング

| ケース | 挙動 |
|---|---|
| AI 無効 | ページ非表示 + サービス冒頭ガード |
| LLM 応答が JSON でない | リトライ 1 回 → だめなら全文を final_answer 扱い |
| 未知ツール名 / 引数不正 | エラーメッセージをツール結果として返しループ続行 (LLM に自己修正させる) |
| ツール実行例外 | catch してエラー文字列化、ループ続行 |
| パスガード違反 | "Access denied: path is outside managed roots." を返す |
| 承認拒否 | "User rejected this action." を返す |
| MaxIterations 到達 | 強制 final_answer |
| キャンセル | ループ即中断、チャットに "Cancelled" 表示、途中までの履歴は保持 |

---

## 段階的リリース計画

### Phase A: MVP (読み取りエージェント)

- ツール基盤 (ICuriaAgentTool / AgentToolRegistry / AgentOrchestratorService)
- 読み取り系ツール 5 個に絞る: list_projects / get_today_tasks / get_project_tasks / search_decision_logs / ask_knowledge_base
- AgentChatPage (吹き出し + ツール折りたたみ行 + Stop)
- セッション永続化なし (メモリのみ)

この時点で「今日何やるべき?」「あの件どうなってた?」が対話で完結する。書き込みがないため承認 UI 不要でリスク最小。

### Phase B: 操作エージェント

- 承認カード実装
- 書き込み系: create_task / capture_note / append_to_file / generate_standup
- UI 操作系: open_in_editor / navigate_to_page
- 残りの読み取り系ツール追加 (schedule / team / wiki / snapshot / read_file)
- セッション永続化
- 旧 Ask Curia (`?` モード) の削除と AgentChatPage への導線置き換え (D6 の Step 2〜4)

### Phase C: 発展 (実装済み)

- [x] `update_current_focus` / `append_decision_log` を Agent ツールとして統合。Agent 承認後にも `ProposalReviewDialog` による最終レビューを要求する。
- [x] セッション履歴の一覧・再開・削除。
- [x] `openai` / `azure_openai` のネイティブ function calling。CLI プロバイダはプロンプトベース JSON を継続利用する。
- [x] 固定プリセット "Morning preparation"。schedule / today tasks / standup を収集する指示を Agent Chat に送信する。
- [x] Asana タスクの完了化。承認後に完了し、15 分間有効なアプリ内 Undo token で未完了に戻せる。

制約:

- Undo token は永続化しないため、アプリ再起動後は復元できない。
- `append_decision_log` の Agent 経路はドラフトの生成・レビュー・保存まで対応する。`open_issues.md` の resolved tension 削除と添付ファイル指定は Editor の専用フローを利用する。

---

## タスク分解 (Phase A)

| # | タスク | 対象 | 目安 |
|---|---|---|---|
| 1 | [x] AgentChatModels.cs 追加 | Models/ | 80 行 |
| 2 | [x] ICuriaAgentTool + AgentToolRegistry | Services/Agent/ | 120 行 |
| 3 | [x] プロトコルパーサ (JSON 抽出 / フェンス除去 / few-shot / フォールバック) | Services/Agent/AgentProtocol.cs | 150 行 |
| 4 | [x] AgentOrchestratorService (ループ / 反復上限 / 切り詰め / D5-2 注入) | Services/Agent/ | 280 行 |
| 5 | [x] 読み取りツール 5 種 | Services/Agent/Tools/ | 5 × 60 = 300 行 |
| 6 | [x] Agent Compatibility Check (プローブ実行 + settings 保存) | Services/Agent/ + SettingsViewModel | 120 行 |
| 7 | [x] Settings 画面に "Test Agent Compatibility" ボタン追加 | Views/Pages/SettingsPage | 40 行 |
| 8 | [x] Markdig.Wpf 導入 (NuGet 追加はユーザー承認後) + ダークテーマ対応 | Assets/ + csproj | 100 行 |
| 9 | [x] AgentChatViewModel | ViewModels/ | 200 行 |
| 10 | [x] AgentChatPage (XAML + code-behind、Markdown 吹き出し + ツール折りたたみ行) | Views/Pages/ | 300 行 |
| 11 | [x] App.xaml.cs DI 登録 + NavigationView 項目追加 | - | 30 行 |
| 12 | [x] AiEnabled + AgentCompatibilityOk ガード / メッセージ購読 | - | 30 行 |
| 13 | 手動テスト (下記チェックリスト) | - | - |

合計目安: 約 1,750 行。

### 手動テスト観点 (Phase A)

- [ ] AI 無効時にページが表示されないこと / 有効化で即出現すること (AiEnabledChangedMessage)
- [ ] Compatibility Check 未実施 / 不合格のプロバイダでページがブロックされること
- [ ] provider または model を変更すると再チェックが要求されること
- [ ] 「今日のタスクは?」で get_today_tasks が呼ばれ回答が返ること
- [ ] 「Alpha の決定事項でDBに関するもの」で search_decision_logs → 引用付き回答
- [ ] 回答の Markdown (見出し / リスト / コードブロック / テーブル) がダークモードで正しく描画されること
- [ ] 存在しないプロジェクト名を指定 → ツールエラー → LLM が聞き返すこと
- [ ] 実行中の Stop でループが止まり UI が固まらないこと
- [ ] github_copilot / claude_code / azure_openai の 3 プロバイダで JSON プロトコルが機能すること
- [ ] 「前置き + JSON」混在応答でもツール呼び出しが成立すること (TryExtractJson)
- [ ] MaxIterations 到達時に強制回答が返ること

---

## タスク分解 (Phase B)

| # | タスク | 対象 | 目安 |
|---|---|---|---|
| 1 | [x] 承認カード基盤: Kind=Approval メッセージ + Approve/Reject コマンド + TaskCompletionSource 橋渡し | ViewModels/ + Views/Pages/ | 150 行 |
| 2 | [x] 「このセッション中は同種ツールを自動承認」トグル (セッション内辞書で管理、永続化しない) | ViewModels/ | 40 行 |
| 3 | [~] パスガード共通実装 (マネージドルート判定 / `..` 拒否 / junction 解決) | Services/Agent/AgentPathGuard.cs | 100 行 |
| 4 | [x] 書き込みツール: create_task (CaptureService 委譲、dedup ガード継承) | Services/Agent/Tools/ | 80 行 |
| 5 | [~] 書き込みツール: capture_note (現状はグローバル capture_log.md へ追記) | Services/Agent/Tools/ | 60 行 |
| 6 | [x] 書き込みツール: append_to_file (パスガード + FileEncodingService でエンコーディング維持) | Services/Agent/Tools/ | 80 行 |
| 7 | [x] 書き込みツール: generate_standup / sync_asana | Services/Agent/Tools/ | 2 × 50 = 100 行 |
| 8 | [x] UI 操作ツール: open_in_editor / open_in_timeline / navigate_to_page (コールバック委譲 + UI スレッドディスパッチ) | Services/Agent/Tools/ | 120 行 |
| 9 | [x] UI 操作ツール: start_pomodoro | Services/Agent/Tools/ | 40 行 |
| 10 | [~] 残りの読み取りツール: get_schedule / get_team_tasks / search_wiki / get_state_snapshot / read_file / read_current_focus / read_project_summary / get_open_issues / get_standup | Services/Agent/Tools/ | 9 × 60 = 540 行 |
| 11 | [x] セッション永続化 (`%CONFIG%/agent_chat_history/`、User/Assistant/ToolCall 要約のみ保存) | Services/Agent/ | 120 行 |
| 12 | [x] `?` プレフィックスの導線置き換え: 入力を引き継いで AgentChatPage へ遷移 + 自動送信 (D6 Step 2) | CommandPaletteViewModel / Overlay | 80 行 |
| 13 | [~] 旧 AskMode UI の削除 (D6 Step 3: AskMode プロパティ / 回答パネル / 引用クリック処理) | CommandPaletteViewModel / Overlay | -200 行 |
| 14 | [ ] CuriaQueryService の UI 専用 API 整理 (D6 Step 4) | Services/ | 30 行 |
| 15 | 手動テスト (下記チェックリスト) | - | - |

合計目安: 約 1,540 行 (削除分を除く)。

実施順の推奨: 1〜3 (基盤) → 4〜7 (書き込みツール) → 8〜10 (UI 操作 + 読み取り拡充) → 11 (永続化) → 12〜14 (Ask Curia 移行は最後。エージェント側が安定してから)。

### 手動テスト観点 (Phase B)

- [ ] Write ツール実行前に承認カードが出ること / Reject で "User rejected" が LLM に渡り代替提案が返ること
- [ ] 自動承認トグル ON 後、同種ツールがカードなしで実行されること / 新セッションでリセットされること
- [ ] create_task で Asana にタスクが作成され、dedup ガードが効くこと
- [ ] append_to_file がマネージドルート外パスを拒否すること (`..`、絶対パス、別ドライブ)
- [ ] append_to_file が SJIS ファイルのエンコーディングを維持すること
- [ ] open_in_editor で Editor ページに遷移し対象ファイルが開くこと (UI スレッド例外が出ないこと)
- [ ] セッション JSON が保存され、ツール結果全文が含まれないこと
- [ ] `?` 入力 → AgentChatPage 遷移 + 自動送信が機能すること
- [ ] 旧 AskMode 削除後、CommandPalette の通常検索が退行していないこと
- [ ] WikiPage の Wiki チャットが影響を受けていないこと

---

## コードレビュー結果と修正バックログ (2026-07-12)

静的レビューに加えて、`dotnet build Curia.csproj` と分離ディレクトリへの `dotnet publish Curia.csproj -p:PublishProfile=SingleFile` を実行し、どちらも成功した。コンパイルエラーはないが、以下のセキュリティ問題、競合状態、仕様未達が残っている。リポジトリには自動テストがないため、修正後は本節と既存の手動テスト観点を使用して回帰確認する。

### P0: リリース前に修正

- [ ] `update_current_focus` / `append_decision_log` の `workstream` を検証する。
    - LLM 由来の値を `Path.Combine` に渡す前に、対象プロジェクトの既存 `Workstreams` の ID と完全一致することを確認する。
    - 最終パスを正規化し、対象プロジェクトの期待するルート配下であることを再検証する。`..`、ルート付きパス、区切り文字を含む不正 ID は拒否する。
- [ ] `AgentPathGuard` で junction / symbolic link による管理ルート逸脱を防ぐ。
    - 各既存パスセグメントの reparse point を解決して実体パスを検証するか、少なくとも reparse point を含むアクセスを拒否する。
    - `read_file` と `append_to_file` の両方へ適用し、TOCTOU を考慮してファイルオープン時にも境界を確認する。
- [ ] config ディレクトリ内の秘密情報を `read_file` から保護する。
    - `settings.json`、`asana_global.json` など、API key / token / credential を含むファイルを拒否する。
    - config ルート全体の許可ではなく、安全なファイルの allowlist を優先する。
- [ ] `open_in_editor` に管理パス検証を追加する。
    - `AgentPathGuard` を適用し、指定ファイルが選択されたプロジェクトの許可ディレクトリ配下であることも確認する。
- [ ] CLI 呼び出しのユーザーキャンセル時に子プロセスを終了する。
    - タイムアウトだけでなく、すべての `OperationCanceledException` で実行中のプロセスツリーを停止してからキャンセルを再送出する。
- [ ] `complete_task` の Undo token を失敗時に保持する。
    - Asana API 成功前に token を削除しない。同時利用防止が必要な場合は `InProgress` 状態を導入し、失敗・キャンセル時に再利用可能へ戻す。
    - `TodayQueueService.SetAsanaTaskCompletedAsync` は `OperationCanceledException` を一般エラーへ変換せず再送出する。

### P1: 主要機能の安定化

- [ ] Command Palette 初回遷移時の履歴復元と自動送信を直列化する。
    - `AgentChatViewModel.InitializeAsync()` の完了後に `SubmitAsync(question)` を await する。
    - 初期化、履歴ロード、送信、保存を同じ非同期ロックで保護し、fire-and-forget の例外を残さない。
- [ ] 履歴から別セッションをロードしたときに `_autoApprovedTools` をクリアする。
    - 自動承認状態は ViewModel 全体ではなくセッション ID に紐付けるか、`LoadSessionAsync` 成功時に必ずリセットする。
- [ ] native function calling のツールパラメータを正式な JSON Schema で定義する。
    - 現在の簡易変換は `minutes` 以外を文字列として公開するため、`limit` など実装側が整数を要求する引数と不整合になる。
    - 全 Descriptor のスキーマを起動時に検証し、不正スキーマを `additionalProperties=true` へ黙ってフォールバックしない。
- [ ] CLI JSON パーサの型不一致を安全に処理する。
    - `type`、`tool`、`text`、`reason` は `TryGetValue<string>()` 相当で検証し、型不一致を例外ではなくプロトコル不正として既存の 1 回リトライ経路へ流す。
- [ ] 履歴ファイル名の衝突を防止する。
    - 秒精度の `{yyyy-MM-dd_HHmmss}.json` に GUID またはミリ秒を追加し、作成時は衝突を検出して再生成する。
- [ ] 表示中セッションを削除した場合の動作を明確化する。
    - `Messages` もクリアして新規セッションへ移行するか、削除済み履歴が次回保存で復活しないよう表示中会話を履歴サービスから切り離す。
- [ ] 承認カードを解決後の状態へ更新する。
    - `AgentChatMessage` にプロパティ変更通知を実装し、Approve / Reject 後はチェックボックスとボタンを非表示または無効化する。
- [ ] Agent Chat 再表示時に AI / compatibility 状態を再評価する。
    - `_historyLoaded` による早期 return より前に `RefreshAvailability()` を実行し、compatibility 結果変更も Messenger で通知する。
- [ ] AI 無効化時に進行中ターンをキャンセルする。
    - `AiEnabledChangedMessage` で false を受信した場合は現在の CTS をキャンセルし、各反復と書き込みツール実行直前にも設定を再確認する。

### P2: 仕様完了と品質改善

- [ ] Agent Chat の NavigationView 項目を `AiEnabled && AgentCompatibilityOk` に応じて表示制御する。
- [ ] 旧 Ask Curia UI を物理削除する。
    - `CommandPaletteViewModel` の AskMode 状態、会話履歴、直接 `CuriaQueryService.AskAsync` を呼ぶ経路を削除する。
    - `CommandPaletteWindow` の旧回答・引用パネルと関連イベント処理を削除する。
    - `?` は Agent Chat への質問引き継ぎ専用とし、WikiPage の Wiki 専用チャットには影響を与えない。
- [ ] `CuriaQueryService` の UI 専用 API を整理し、`ask_knowledge_base` のバックエンドとして必要な API のみ残す。
- [ ] `append_to_file` の読み込み・全体書き戻し競合を防ぐ。
    - ファイル単位の排他、最終更新時刻の確認、またはエンコーディング維持可能な排他的追記を実装する。
- [ ] 同期的なファイル走査・Markdown 解析による UI フリーズを防ぐ。
    - Wiki 検索、チームタスク解析、プロジェクトタスク解析などを UI スレッド外で実行し、長いループで CancellationToken を確認する。

### 修正後の追加テスト観点

- [ ] `workstream` に `..`、絶対パス、区切り文字、未知 ID を渡してもファイル作成・更新されないこと。
- [ ] 管理ルート内から管理外を指す junction / symbolic link 経由の read / append が拒否されること。
- [ ] config 内の API key / Asana token を含むファイルが `read_file` で取得できないこと。
- [ ] CLI 実行中に Stop した後、CLI 子プロセスが残らないこと。
- [ ] Undo の通信失敗・キャンセル後に同じ token で再試行できること。
- [ ] アプリ起動後、Agent Chat を未表示のまま `?質問` を送っても履歴混入・二重送信・既存履歴上書きがないこと。
- [ ] セッション A で自動承認後にセッション B をロードすると、書き込みツールが再度承認を要求すること。
- [ ] OpenAI / Azure OpenAI で整数引数を持つ全ツールが native function calling から正常実行できること。
- [ ] 型が不正な CLI JSON 応答が 1 回リトライされ、チャット全体が例外終了しないこと。
- [ ] 同一秒に複数セッションを作成しても履歴が上書きされないこと。
- [ ] Approve / Reject 後に承認カードが解決済み表示になり、再操作できないこと。

---

## 設計決定事項 (2026-07-12 確定)

1. Markdown レンダラ: 最初から導入する。Markdig.Wpf + ダークテーマ用スタイル辞書 (Phase 4 参照)。NuGet 追加は実装時にユーザー承認を得る
2. プロジェクト一覧の事前注入: コンパクト形式 (name + tier/category、最大 50 件) を毎ターン注入。詳細は list_projects ツールに委譲 (D5-2 参照)
3. 既存 Ask Curia (`?` モード): 削除して本機能に一本化。CuriaQueryService はツールバックエンドとして存続、`?` は AgentChatPage への導線に置き換え (D6 参照)。移行は Phase B の最後に実施
4. CLI プロバイダの JSON 遵守: プロンプト few-shot + 寛容な JSON 抽出 + Agent Compatibility Check の三段構えで対応 (D1-2 参照)。github_copilot を含め、チェックに合格したプロバイダのみエージェント機能を有効化する
