# Agent Chat ツール棚卸し

作成日: 2026-07-12

## 実装更新 (2026-07-12)

- 完了: provider共通のJSON Schema正規化・実行前引数検証、共通result envelope、匿名化した利用ログ、capability filtering、junction/symlinkの実体root照合。
- 完了: `get_tasks`、`search_knowledge`、`read_managed_file` を追加し、旧タスク取得、旧ナレッジ検索、旧`read_file`は非表示aliasまたはdeprecated toolへ移行した。
- 完了: `navigate_to_page` は `page="timeline"` と任意の`project`を受け、旧`open_in_timeline`を置き換えた。
- ユーザー判断で削除: `start_pomodoro`、`open_in_timeline`、`get_standup`、`generate_standup`。ポモドーロ、タイムライン、スタンドアップ本体のアプリ機能は維持する。
- 利用ログと履歴には、ツール引数、ファイル本文、ツール結果本文を保存しない。

## 目的

Agent Chat に公開されているツールを対象に、不要なもの、責務の重複、統合候補、安全性と保守性の問題を静的に確認した。

今回の対象は `ICuriaAgentTool` として登録される Agent Chat のツールのみ。Agent Hub の外部 CLI 向け `tools:`、`ScriptRunnerService`、Compatibility Check 専用の `echo` は対象外。

## 要約

- 公開ツールは26個。
- 宣言上の内訳は `ReadOnly` 18個、`Write` 8個、`Dangerous` 0個。
- 26個すべてが DI 登録されており、静的に完全な未使用・到達不能と判断できるツールはない。
- 実利用ログがないため、「利用されていない」という理由だけで即削除できるツールは判定できない。
- 優先して非表示・廃止を検討できるのは `get_state_snapshot` と `append_to_file`。
- 安全に統合しやすいのは、プロジェクト文書取得3ツールとタスク取得2ツール。
- `ask_knowledge_base`、`search_decision_logs`、`search_wiki` は責務が重複するが、検索品質への影響が大きいため段階移行が必要。
- `start_pomodoro` は状態変更を行うのに `ReadOnly` であり、統合とは別に早期修正が必要。
- `complete_task` は完了とUndoの2責務を持ち、引数schemaとも矛盾するため分割が望ましい。
- 最終的には26個から18〜19個程度へ整理可能。

## 現在の公開経路

- ツール登録: [App.xaml.cs](../App.xaml.cs#L139-L164)
- 共通インターフェース: [ICuriaAgentTool.cs](../Services/Agent/ICuriaAgentTool.cs)
- 名前解決と一覧生成: [AgentToolRegistry.cs](../Services/Agent/AgentToolRegistry.cs#L6-L38)
- 承認と実行ループ: [AgentOrchestratorService.cs](../Services/Agent/AgentOrchestratorService.cs)
- OpenAI/Azure用 native function変換: [LlmClientService.cs](../Services/LlmClientService.cs#L401-L545)
- ツール一覧UI: [AgentChatPage.xaml](../Views/Pages/AgentChatPage.xaml#L102-L126)

現在は、登録された全ツールが設定状況やページ状態にかかわらず常にモデルへ提示される。Asana未設定、UIコールバック未設定、対象データなしの場合も、実行時エラーになるまで候補から外れない。

## 全26ツールの判定

| 現在のツール | 宣言リスク | 判定 | 推奨 |
|---|---|---|---|
| `list_projects` | ReadOnly | 維持 | プロジェクト一覧の基本入口として維持 |
| `get_today_tasks` | ReadOnly | 統合 | `get_project_tasks` と `get_tasks` に統合 |
| `get_project_tasks` | ReadOnly | 統合 | `get_today_tasks` と `get_tasks` に統合 |
| `search_decision_logs` | ReadOnly | 段階統合 | `search_knowledge` の `decision` sourceへ移行 |
| `ask_knowledge_base` | ReadOnly | 廃止候補 | 二重LLM呼び出しをやめ、一次資料検索を外側Agentへ返す |
| `read_file` | ReadOnly | 縮小 | `read_managed_file` として対象、サイズ、ページングを制限 |
| `get_schedule` | ReadOnly | 維持・改善 | manual、Outlook、ICSの範囲を明示または統合 |
| `get_team_tasks` | ReadOnly | 維持 | チーム視点に独自価値あり |
| `search_wiki` | ReadOnly | 段階統合 | `search_knowledge` の `wiki` sourceへ移行 |
| `get_state_snapshot` | ReadOnly | 非表示・廃止候補 | Agent Chatには過大で、他ツールと大幅重複 |
| `read_current_focus` | ReadOnly | 統合 | `get_project_context` に統合 |
| `read_project_summary` | ReadOnly | 統合 | `get_project_context` に統合 |
| `get_open_issues` | ReadOnly | 統合 | `get_project_context` に統合 |
| `get_standup` | ReadOnly | 維持 | Morning preparationで有用 |
| `create_task` | Write | 維持・改善 | Asana専用からlocal/Asana共通のタスク参照へ拡張 |
| `capture_note` | Write | 改名 | 実態に合わせ `capture_inbox_note` へ変更 |
| `append_to_file` | Write | 非表示・廃止候補 | 汎用書き込みよりdomain-specific toolを優先 |
| `sync_asana` | Write | 維持 | Asana設定時だけ提示 |
| `generate_standup` | Write | 維持 | `get_standup` とのread/write分離は妥当 |
| `update_current_focus` | Write | 維持 | 差分レビュー付きで安全性が高い |
| `append_decision_log` | Write | 改名 | 実態は新規生成なので `create_decision_log` が適切 |
| `complete_task` | Write | 分割 | 完了専用と `undo_task_completion` に分ける |
| `open_in_editor` | ReadOnly | 維持 | ファイル指定が必要で独自性あり |
| `open_in_timeline` | ReadOnly | 統合 | `navigate_to_page` にproject引数を追加して統合 |
| `navigate_to_page` | ReadOnly | 維持・拡張 | 一般ナビゲーションの入口にする |
| `start_pomodoro` | ReadOnly | 維持・リスク修正 | `Write` または `Action` 扱いへ変更 |

## 重複と統合候補

### 1. プロジェクト文書取得

現在:

- `read_current_focus`
- `read_project_summary`
- `get_open_issues`
- 汎用の `read_file`

最初の3つは同じ `ProjectFileToolBase` を使い、対象ファイルだけが異なる。[AdditionalReadOnlyAgentTools.cs](../Services/Agent/Tools/AdditionalReadOnlyAgentTools.cs#L59-L92)

推奨:

- 新規 canonical tool: `get_project_context`
- 引数: `project`, `sections`
- `sections`: `focus`, `summary`, `open_issues`
- 複数sectionを1回で取得可能にする。
- 旧3ツールは非表示aliasとして一定期間残す。
- 未知ファイル取得だけを制限版 `read_managed_file` に任せる。

効果:

- 3ツールを1ツールに削減。
- 名前の `read_*` と `get_*` の揺れを解消。
- 既知のdomain fileを安全に扱える。

### 2. タスク取得

現在:

- `get_today_tasks`: 全体の優先キュー、bucket中心
- `get_project_tasks`: 特定プロジェクト、workstream、status中心

推奨:

- 新規 canonical tool: `get_tasks`
- 引数: `scope`, `project`, `workstream`, `status`, `due_bucket`, `limit`
- `scope`: `today_queue` または `project`
- 出力を `task_id`, `source`, `project`, `workstream`, `status`, `due`, `can_complete` に標準化。

注意:

- 2ツールのバックエンドと出力モデルが異なるため、先に共通 `TaskReference` を定義する。
- local task modeとAsana taskのID semanticsを揃えてから旧名を非表示化する。

### 3. ナレッジ検索

現在:

- `search_decision_logs`: 決定ログの構造検索
- `search_wiki`: Wikiの単純部分一致
- `ask_knowledge_base`: 複数sourceから検索した後、内部で別のLLM回答を生成

問題:

- `ask_knowledge_base` はAgent ChatからさらにLLMを呼ぶため、コスト、遅延、解釈の二重化が発生する。
- モデルから見ると3ツールの使い分けが曖昧。
- 一方で、決定ログの構造化情報とknowledge baseのcitation生成には独自価値がある。

推奨:

- 新規 canonical tool: `search_knowledge`
- 引数: `query`, `source_types`, `project`, `limit`, `include_content`
- `source_types`: `decision`, `wiki`, `task`, `focus`, `meeting` など。
- ツールは一次資料、excerpt、path、line hintを返すだけにする。
- 最終回答生成はAgent ChatのLLMだけで行う。

移行順:

- まず `search_knowledge` を追加。
- `ask_knowledge_base` と検索精度、citation、応答時間を比較。
- 同等品質を確認後に `ask_knowledge_base` を非表示化。
- 最後に `search_decision_logs` と `search_wiki` を非表示alias化。

### 4. UIナビゲーション

現在:

- `open_in_editor`
- `open_in_timeline`
- `navigate_to_page`

推奨:

- `open_in_editor` はファイル選択とパス検証があるため独立維持。
- `open_in_timeline` は `navigate_to_page(page="timeline", project="...")` に統合。
- `navigate_to_page` のproject引数はtimelineなど必要なページだけで使用する。

全3ツールを単一の `open_resource` にまとめる案もあるが、page、project、pathの条件付きschemaが複雑になり、現在の簡易schema変換ではかえって誤呼び出しが増える。現時点では2ツール構成が安全。

### 5. タスク完了とUndo

現在の `complete_task` は次を同時に行う。

- `task_gid` で完了
- `undo_token` で未完了へ復元

schemaでは `task_gid` がrequiredだが、Undo実行時は不要。[PhaseCAgentTools.cs](../Services/Agent/Tools/PhaseCAgentTools.cs#L134-L205)

推奨:

- `complete_task(task_id)`
- `undo_task_completion(undo_token)`

分割によりschema、承認、自動承認の単位が明確になる。

## 優先廃止候補

### `get_state_snapshot`

理由:

- 全プロジェクト、全タスク、各種パスを一括で返し、Agent Chatには大きすぎる。
- `list_projects`、`get_today_tasks`、`get_project_tasks`、システムプロンプトのプロジェクト一覧と重複する。
- 大量の絶対パスや不要情報をモデルへ送る。
- 本来は外部連携用snapshotとしての価値が高く、サービス自体を削除する必要はない。

対応:

- まずモデル提示とToolsパネルから非表示化。
- 明示名によるalias解決だけ一定期間残す。
- `list_projects` と `get_tasks` でportfolio質問に回答できることを確認後、Agent Chat登録を外す。

### `append_to_file`

理由:

- 汎用書き込みで影響範囲が広い。
- domain-specificな `update_current_focus`、`create_decision_log`、`capture_inbox_note` より安全性が低い。
- セッション内自動承認後は別ファイルや別内容でも再承認されない。
- Curia標準のjunctionレイアウトと現在のpath guard方針が衝突する。

対応:

- 利用ログ導入後、必要な保存先を確認。
- 頻出用途を専用ツール化。
- 残す場合は毎回承認、差分レビュー、対象ファイルallowlistを必須にする。

## 統合前に直すべき共通問題

### 1. schemaが正式なJSON Schemaではない

現在の `ParametersSchema` は説明文字列で、native function schemaへの変換時に文字列から型とrequiredを推測している。[AgentChatModels.cs](../Models/AgentChatModels.cs#L9-L15) [LlmClientService.cs](../Services/LlmClientService.cs#L490-L526)

影響:

- enum制約がproviderへ渡らない。
- 日付形式やID形式を表現できない。
- conditional argumentsを正しく表せない。
- CLI経路とnative経路で検証が一致しない。

対応:

- descriptorに正式なJSON Schemaを保持。
- registry構築時に全schemaを検証。
- CLI/native共通の実行前argument validatorを導入。

### 2. `start_pomodoro` のリスク分類

`PomodoroService.Start()` で状態を変更するが `ReadOnly` になっている。[AgentUiTools.cs](../Services/Agent/Tools/AgentUiTools.cs#L56-L75)

対応:

- 直ちに `Write` へ変更するか、`Action` risk levelを新設。
- ページ遷移は承認不要、タイマー開始、外部同期、ファイル/API更新は承認対象とする。

### 3. tool resultの切り詰め

オーケストレータはシリアライズ後の文字列を単純に文字数で切り詰めるため、大きなJSONが不正JSONになる可能性がある。

影響が大きいもの:

- `search_wiki`
- `get_state_snapshot`
- `read_file`
- `list_projects`

対応:

- 各ツールにlimit、ページング、本文長制限を持たせる。
- `truncated` と `next_cursor` を構造的に返す。
- JSON文字列を途中で切らない。

### 4. nativeとCLIの失敗結果が不一致

CLI経路はsuccess/failureを明示するが、native経路は主にcontent文字列だけをtool messageへ返す。

対応:

- 共通result envelopeを採用。
- 例: `success`, `code`, `summary`, `data`, `truncated`, `next_cursor`。

### 5. junctionとpath guard

`read_file`、`append_to_file`、`open_in_editor` はpath guardを使うが、標準管理レイアウトのjunctionも拒否し得る。一方、専用project readerはdiscovery済みpathを直接読むため方針が不統一。

対応:

- junction自体を拒否するのではなく、最終実体pathを解決して許可済み実体rootと照合する。
- 安全な実体path検証ができるまでは汎用ファイルツールを非表示にする。

### 6. capability filteringがない

対応:

- `IsAvailable(context)` または `CapabilityRequirements` をdescriptorに追加。
- Asana設定、local task mode、UI callback、対象root、providerに応じてモデル提示対象を絞る。
- 「登録されている」と「現在モデルへ提示する」を分離する。

## 推奨ターゲット構成

### 読み取り 8個

- `list_projects`
- `get_tasks`
- `get_team_tasks`
- `get_schedule`
- `get_project_context`
- `search_knowledge`
- `get_standup`
- `read_managed_file`

### 変更 8個

- `create_task`
- `complete_task`
- `undo_task_completion`
- `capture_inbox_note`
- `sync_asana`
- `generate_standup`
- `update_current_focus`
- `create_decision_log`

### UI・アクション 3個

- `open_in_editor`
- `navigate_to_page`
- `start_pomodoro`

合計19個。

`open_in_editor` と `navigate_to_page` を正式JSON Schema導入後に `open_resource` へ統合するなら18個まで削減可能。ただし統合数を減らすこと自体より、モデルが誤解しにくい単純な契約を優先する。

## 推奨移行順

### Step 1: 安全性と契約

- `start_pomodoro` のrisk levelを修正。
- 正式JSON Schemaと共通argument validationを導入。
- `complete_task` とUndoを分割。
- tool result envelopeと構造的なサイズ制限を導入。
- junctionの実体path検証方針を決める。

### Step 2: 利用ログとdeprecation基盤

最低限記録するもの:

- tool name
- success/failure code
- duration
- result size
- approval/rejection
- provider
- schema validation failure
- timestamp

記録しないもの:

- 引数本文
- ファイル本文
- ユーザープロンプト

registry側には次を追加する。

- `CanonicalName`
- `Aliases`
- `IsAdvertised`
- `DeprecatedSince`
- `CapabilityRequirements`

旧名は呼び出せるが、プロンプトとToolsパネルには表示しない状態を作る。

### Step 3: 低リスク統合

- `get_project_context` を追加し、旧3 readerを非表示alias化。
- `navigate_to_page` にproject引数を追加し、`open_in_timeline` を非表示alias化。
- `append_decision_log` を `create_decision_log` へ改名。
- `capture_note` を `capture_inbox_note` へ改名。

### Step 4: 過大・汎用ツールを非表示化

- `get_state_snapshot` をモデル提示から外す。
- `append_to_file` をモデル提示から外す。
- `read_file` を制限版 `read_managed_file` へ置換。

### Step 5: タスク統合

- 共通 `TaskReference` を定義。
- `get_tasks` を追加。
- local/Asana共通の作成、完了、Undoを整備。
- 旧タスク取得2ツールを非表示alias化。

### Step 6: ナレッジ検索統合

- `search_knowledge` を追加。
- 既存3経路と品質、citation、速度を比較。
- `ask_knowledge_base` の内側LLM依存を解消。
- 品質確認後に旧3ツールを非表示alias化。

### Step 7: 旧alias削除

- 利用ログで旧名の呼び出しがないことを確認。
- provider別の手動・自動テストを実施。
- 公開ドキュメントを更新してから登録を削除。

## テスト観点

このリポジトリには自動テストがないため、統合時には少なくとも次を確認する。

- 全descriptorのschema validation
- required、optional、enum、型不一致
- CLI/nativeのresult parity
- risk levelと承認表示
- セッション内自動承認の対象範囲
- junction、symbolic link、root境界
- result paginationとJSON妥当性
- Asana/local task mode
- complete/Undoの競合と期限切れ
- capability filtering
- deprecated alias解決
- Stop時のCancellationToken伝播

## 最終判断

- 即時に実装クラス自体を削除するより、まず「モデルへ提示しない」状態を作るのが安全。
- 第一候補は `get_state_snapshot` の非表示化。
- 第二候補は `append_to_file` の非表示化とdomain toolへの置換。
- 最初の統合対象はプロジェクト文書取得3ツール。
- 次にタスク取得2ツールを共通モデル上で統合。
- ナレッジ検索3ツールは価値が重なるが、品質退行リスクが最も高いため最後に統合。
- ツール数削減より先に、正式schema、capability filtering、alias/deprecation、利用ログを整備するのが妥当。
