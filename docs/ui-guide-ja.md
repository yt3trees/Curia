# 画面ガイド

[< READMEに戻る](../README-ja.md)

## 目次

- [Dashboard](#dashboard)
  - [DashboardのAI機能](#dashboardのai機能)
- [Editor](#editor)
  - [EditorのAI機能](#editorのai機能)
- [Timeline](#timeline)
- [Git Repos](#git-repos)
- [Asana Sync](#asana-sync)
- [Wiki](#wiki)
- [Agent Hub](#agent-hub)
- [Setup - New Project](#setup---new-project)
- [Setup - Workstreams](#setup---workstreams)

## Dashboard

全プロジェクトのヘルス状態、更新鮮度、Today Queueを一画面で確認できます。

![](../_assets/Dashboard.png)

![](../_assets/Dashboard-Card.png)

これはプロジェクトカードの標準表示です。ヘルスシグナル、リポジトリ状態、クイックアクションを確認できます。

- 上部バーでは、手動更新、自動更新(Off / 10 / 15 / 30 / 60 min)、非表示プロジェクトの表示切り替えができます。
- 各カードで、プロジェクトの状態を一目で確認できます。表示されるのは名前、Tier(FULL/MINI)、DOMAINタグ(該当時)、リンク状態ドット、decision log件数、未コミット件数です。
- 未コミット件数をクリックすると、リポジトリごとの変更内容をダイアログで確認できます。
- `Focus` / `Summary` は「最終更新から何日経ったか」を表示し、古くなるほど背景色が変わります。
- 30日分のアクティビティバーはクリックすると Timeline に移動します。
- カード下部のボタンから、フォルダを開く、ターミナル起動(Claude/Gemini/Codex起動含む)、Editorへ移動、作業フォルダのPinができます。
- Workstreamはカードごとに展開できます。各行で `current_focus.md` を開く、workstream `_work` を開く(右クリックで当日作業フォルダ作成)、最近フォルダのPinができます。Team Viewが設定済みの場合はWorkstream行(およびプロジェクトカード)にTeam Viewボタンが表示され、クリックするとTeam Viewダイアログが開きます。
- Team Viewダイアログでは、チームメンバーのAsanaタスクをプロジェクト別にグループ化して表示し、期日と期限超過(⚠)を確認できます。Syncボタンでアサナから最新タスクを取得し `team-tasks.md` を更新します。
- `Pinned Folders` は1件以上Pinすると表示されます。開く、解除、ドラッグ並び替え、Clear一括解除に対応しています。
- `Today Queue` は `tasks.md` の未完了タスクを読み込み、Overdue / Today / In Nd / No due で表示します。
- Today Queueの各行では、Asanaで開く、翌日までsnooze、Asanaで完了化ができます。
- Today Queueヘッダーでは、プロジェクト/Workstreamフィルタ、Show All(Top 10 と最大100件)、snooze一括解除、手動更新、高さ固定/可変の切り替えができます。

### DashboardのAI機能

<img src="../_assets/ai-feature/WhatsNext.png" width="60%" alt="What's Next ダイアログ" />

AI機能が有効な場合、上部バーの What's Next ボタンから全プロジェクト横断の優先アクションを3-5件表示できます。各項目には直接移動用の `Open` とテキスト出力用の `Copy` があります。

<img src="../_assets/ai-feature/ContextBriefing.png" width="60%" alt="Context Briefing ダイアログ" />

AI機能が有効な場合、各プロジェクトカードの Briefing ボタンでプロジェクト専用の再開要約 (Where you left off / Suggested next steps / Key context) を生成し、`Copy` / `Open in Editor` / `View Debug` が使えます。

<img src="../_assets/ai-feature/TodaysPlan.png" width="60%" alt="Today's Plan ダイアログ" />

Today's Plan ダイアログ(AI)では、1日の提案を時間帯別(例: Morning / Afternoon)に表示し、`Open` / `Copy` / `Save` / `View Debug` が利用できます。

## Editor

AI コンテキストファイル(`current_focus.md`、`decision_log` など)をツリーから選び、シンタックスハイライト付きで編集できます。

![](../_assets/Editor.png)

- 左上のプロジェクト選択ドロップダウンでプロジェクトを切り替え
- 左側のツリーに AI コンテキストファイルを表示: `current_focus.md`、`file_map.md`、`project_summary.md`、`open_issues.md`、`decision_log/`、`focus_history/`、`obsidian_notes/`、`workstreams/`、`CLAUDE.md`、`AGENTS.md`
- 右側にシンタックスハイライト付きの Markdown エディタ(セクション単位で色分け)
- ツールバーボタン: Refresh、Dec Log(decision log 簡易追加)、P(フォルダ Pin)、Save
- ヘッダーバーにファイルのフルパスを表示
- ステータスバーに現在のプロジェクト名とファイル名を表示

### EditorのAI機能

<img src="../_assets/ai-feature/UpdateFocusFromAsana.png" width="60%" alt="Update Focus ダイアログ" />

Update Focus (AI) は `tasks.md` と行動シグナル(pinned folder のファイル・日付付き work フォルダ・git 活動・quick capture)を読み込み、設定済み LLM に文脈を渡して差分提案ダイアログを表示します。Workstream 絞り込み、自然言語での再指示、`View Debug` に対応し、`focus_history/` へバックアップを保存します。

<img src="../_assets/ai-feature/AI-DecisionLog_1.png" width="60%" alt="AI Decision Log ダイアログ 1" />
<img src="../_assets/ai-feature/AI-DecisionLog_2.png" width="60%" alt="AI Decision Log ダイアログ 2" />

AI Decision Log (AIモードの Dec Log) は、直近の `focus_history` から暗黙の意思決定を検出し、決定メタデータ (Status/Trigger/添付) を受け取って構造化ドラフト (Options / Why / Risk / Revisit Trigger) を生成します。再指示とデバッグ表示に対応し、`decision_log/YYYY-MM-DD_{topic}.md` に保存されます。

<img src="../_assets/ai-feature/ImportMeetingNotes_1.png" width="60%" alt="Import Meeting Notes ダイアログ 1" />
<img src="../_assets/ai-feature/ImportMeetingNotes_2.png" width="60%" alt="Import Meeting Notes ダイアログ 2" />

Import Meeting Notes (AI) は会議メモを1回で分析し、Decisions / Focus / Tensions / Asana Tasks をプレビューします。適用対象を選択でき、`View Debug` で送受信内容を確認でき、`current_focus.md` 上書き前にバックアップされます。

## Timeline

プロジェクトや期間でフィルタして、変更履歴を時系列で確認できます。

![](../_assets/Timeline.png)

- Project ドロップダウンでプロジェクトを絞り込み(例: GenAi [Domain])
- Period ドロップダウンで表示期間を設定(例: 30 days)
- Graph scope で単一プロジェクトか全プロジェクトを選択
- Entries タブに日付(曜日付き)と [Focus]/[Decision]/[Work] ラベル付きのエントリ一覧を表示。[Work] エントリは `shared/_work/` 配下の日付フォルダ(例: `20260321_fix-login-bug`)が対象で、クリックすると Explorer でフォルダを開く
- Graph タブで選択期間のアクティビティ推移をグラフ表示。Work フォルダのイベントも Focus/Decision と合算してカウントされる

## Git Repos

ワークスペース内のリポジトリを一覧表示し、リモートURL・ブランチ・最終コミット日を確認できます。

![](../_assets/GitRepos.png)

- Project ドロップダウンでプロジェクト単位にリポジトリを絞り込み
- Scan ボタンでワークスペースルート配下を再帰的にスキャン
- Save to Cloud / Load from Cloud でクローン情報のバックアップ・復元
- Copy Clone Script で一覧のリポジトリを再クローンするシェルスクリプトを生成
- テーブル列: Project、Repository、Remote URL、Branch、Last Commit

## Asana Sync

プロジェクトごとにAsana同期のスケジュール、Workstreamマッピング、セクションフィルタを設定できます。

詳細な設定手順とページリファレンスは [Asana連携設定](asana-setup-ja.md) を参照してください。

## Wiki

プロジェクトごとのナレッジベースを LLM が段階的に構築・保守する機能です。議事録や設計書を取り込むと、LLM が要約・エンティティ・概念ページを自動生成して相互リンク付きで整理します。

### Wiki の作成方法

1. Wiki タブに移動し、右上のドロップダウンでプロジェクトを選択
2. 「Domain」(例: ERP / Web / Infra) を入力して Initialize Wiki をクリック
3. `_ai-context/context/wiki/` にディレクトリ一式が作成されます

Wiki が存在する場合は3つのサブビューが使えます。

### Pages

左ペインにカテゴリ別ページツリー、右ペインで選択ページの Markdown を表示します。検索バーでタイトル・パスを絞り込み、「Open in Editor」で直接編集できます。

ページは sources / entities / concepts / analysis の4カテゴリに分類されます。カテゴリ詳細は [Wiki機能ドキュメント](wiki-features-ja.md) を参照してください。

#### ソースの取り込み (Import)

下部の「+ Import Source」をクリックするか、Wiki タブにファイルをドラッグ＆ドロップします。対応形式: `.md` / `.txt` / `.pdf` / `.docx`。

AI 機能が有効なとき、LLM が新規・更新ページを生成し、保存前に差分レビューを表示します。Import フローとプロンプト詳細は [Wiki機能ドキュメント](wiki-features-ja.md) を参照してください。

### Query (AI機能が必要)

Wiki に対して自然言語で質問できます。

1. 質問を入力して Ask をクリック (Enter でも送信)
2. LLM が index.md を参照して関連ページを特定し、回答を生成
3. 回答と参照ページを確認し、「Save as Wiki Page」で `pages/analysis/` に保存可能
4. 過去の Q&A は History パネルに時系列で表示されます

### Lint

Wiki の整合性を静的 + LLM チェックで検証します。「Run Lint」をクリックすると、リンク切れ・孤立ページ・陳腐化コンテンツ・矛盾・未作成トピックを検出します。LLM チェック (Contradiction / Missing) は AI 機能が有効なときのみ実行されます。

チェック種別の一覧とプロンプト詳細は [Wiki機能ドキュメント](wiki-features-ja.md#lint) を参照してください。

## Agent Hub

Agent/Rule の定義を一元管理し、プロジェクト単位・CLI単位で配備できます。

![](../_assets/AgentHub.png)

ライブラリ、配備マトリクス、AI Builder の詳細は [AIエージェント協業](ai-agent-collaboration-ja.md#agent-hub-multi-cli-deployment) を参照してください。

## Setup - New Project

プロジェクトの新規作成、構成チェック、アーカイブ、Tier 変換をまとめて行えるページです。

![](../_assets/Setup-NewProject.png)

New Project タブ:

- Project Name: 既存プロジェクトを選ぶと Tier/Category を自動補完し、ExternalSharePath の追加や AI Context Setup の実行が可能
- Tier: `full (standard)` または `mini`
- Category: `project (time-bound)` または `domain`
- ExternalSharePath(任意): 共有データ用のカスタムパス
- Also run AI Context Setup: チェックすると `_ai-context/context/` と `_ai-context/obsidian_notes/` のジャンクションも自動作成
- Overwrite existing skills (-Force): `.claude/skills/`、`.codex/skills/`、`.gemini/skills/`、`.github/skills/` を既存でも再配置
- Setup Project ボタンでフォルダ構成、ジャンクション、スキルファイルを作成
- Output エリアに実行ログを表示

Check タブ:

- 既存プロジェクトのフォルダ構成、ジャンクション、スキルファイルを検証
- 不足や破損があれば報告

Archive タブ:

- プロジェクトをアーカイブ先に移動し、ジャンクションをクリーンアップ

Convert Tier タブ:

- `full` と `mini` の Tier 間でプロジェクトを変換し、フォルダ構成を調整

## Setup - Workstreams

プロジェクト内の Workstream を作成・ラベル編集・クローズ/再開できます。

![](../_assets/Setup-Workstreams.png)

- プロジェクト選択ドロップダウンと Reload ボタン
- Add Workstream: Workstream ID(kebab-case)、ラベル(任意)、表示ラベル(任意)を入力し、Create Workstream で作成
- Existing Workstreams に各 Workstream の ID、ラベル、ステータス(Active / Closed)を一覧表示
- Close ボタンで Closed に変更、Reopen で Active に復帰
- Save Labels でラベルの変更を保存
- Output エリアに実行ログを表示

