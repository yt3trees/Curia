# 全タブ起動時バックグラウンドプリロード - 実装仕様

アプリケーション起動直後は Dashboard の初回表示を優先し、その後、未表示の各タブが必要とするデータをバックグラウンドで順次ロードする。

現状は各 Page の `Loaded` イベントで初めて ViewModel の初期ロードが実行されるため、初回タブ切り替え時に待ち時間が発生する。本対応では、ユーザーがタブを開く前に Singleton ViewModel の初期化を済ませ、初回表示を高速化する。

## 目的

- アプリ起動後、各タブの初期データをユーザー操作なしで事前ロードする
- 最初に表示する Dashboard の描画速度を悪化させない
- 重い処理を同時実行せず、原則として1タブずつ順次ロードする
- プリロード中にユーザーが対象タブを開いても、同じロード処理を重複実行しない
- 1タブのロード失敗で、ほかのタブのプリロードを止めない
- 既存の Refresh、再同期、週移動などの明示的な再ロード操作は維持する

## 対象タブ

| 順序 | タブ | ViewModel | 現在の初期ロード | 負荷・注意点 |
|---:|---|---|---|---|
| 1 | Dashboard | `DashboardViewModel` | `RefreshAsync()` | 初期表示対象。プロジェクト探索、Today Queue、State Snapshot 出力 |
| 2 | Settings | `SettingsViewModel` | `Load()` | 軽量な同期ロード。Page 固有の PasswordBox 反映は表示時に行う |
| 3 | Editor | `EditorViewModel` | `LoadProjectsAsync()` | 通常はプロジェクト一覧のみ。選択状態によってツリー・ファイル読込あり |
| 4 | Wiki | `WikiViewModel` | `InitAsync()` | 初期段階はプロジェクト一覧。ドメイン本体は選択後にロード |
| 5 | Git Repos | `GitReposViewModel` | `InitAsync()` | 初期段階はプロジェクト一覧。Git Scan は自動実行しない |
| 6 | Asana Sync | `AsanaSyncViewModel` | `InitAsync()` | Scheduler は既に起動時開始済み。初期画面データのみロード |
| 7 | Setup | `SetupViewModel` | `LoadProjectNamesAsync()` | プロジェクト、先頭プロジェクトの Workstream をロード |
| 8 | Agent Chat | `AgentChatViewModel` | `InitializeAsync()` | セッション履歴をディスクから復元 |
| 9 | Agent Hub | `AgentHubViewModel` | `InitializeAsync()` | ライブラリ列挙、組み込み Skill の初回展開が発生し得る |
| 10 | Timeline | `TimelineViewModel` | `InitAsync()` | 全プロジェクト横断のファイル走査と Heatmap 生成で重い |
| 11 | Schedule | `WeeklyScheduleViewModel` | `LoadWeekAsync()` | タスク解析、ICS 通信、Outlook COM 取得があり最も外部依存が強い |

Dashboard 内の `PomodoroViewModel` は Dashboard の既存初期化経路で扱う。独立したプリロード項目にはしない。

## 基本方針

### 1. Page ではなく Singleton ViewModel を初期化する

プリロードのために各 Page へ順番にナビゲートしたり、全 Page の Visual Tree を生成したりしない。

理由:

- ナビゲーション選択が画面上で切り替わる
- WPF Control は UI スレッド専用であり、Page 自体をワーカースレッドで生成できない
- Page コンストラクターや `Loaded` にはイベント接続、テーマ反映、コールバック設定などの UI 固有処理が含まれる
- Page の生成まで行うとメモリ消費と起動時 UI 負荷が増える

本仕様の「プリロード」はデータと ViewModel 状態の事前ロードを意味する。Page の XAML 生成は従来どおり初回ナビゲーション時に行う。

### 2. UI 初回描画後に低優先度で開始する

`MainWindow` が Dashboard にナビゲートし、画面を表示可能な位置へ移動した後、Dispatcher の `ContextIdle` 相当の優先度でプリロードを開始する。

起動順:

```text
App.OnStartup
  -> MainWindow.Show
  -> MainWindow.OnLoaded
  -> Dashboard へ Navigate
  -> Dashboard の初回ロード開始
  -> MainWindow の初回描画を優先
  -> StartupPreloadService を低優先度で開始
  -> Dashboard の初期化完了を共有 Task で待機
  -> 残りの ViewModel を順次プリロード
```

`App.OnStartup()` で `MainWindow.Show()` より前にプリロードを開始しない。

### 3. 原則として順次実行する

複数 ViewModel を `Task.WhenAll()` で一斉ロードしない。

理由:

- `ProjectDiscoveryService` の初回キャッシュ生成が競合する可能性がある
- ディスク走査、ネットワーク、Outlook COM が重なると起動直後の負荷が高くなる
- 複数 ViewModel が `ObservableCollection` を更新し、UI Dispatcher が混雑する

軽量ページも含め、初期実装は必ず1件ずつ await する。計測後に安全性が確認できた処理だけ並列化を検討する。

## 初期化の共通契約

各 ViewModel に初回ロード専用の `EnsureInitializedAsync()` を追加する。Page の `Loaded` と起動時プリローダーの双方が、この同じメソッドを呼ぶ。

### 必須動作

- 未開始なら初期化を開始する
- 実行中なら同一の初期化 Task を返して待機する
- 成功済みなら即時完了する
- 失敗時は状態を未初期化へ戻し、ページ表示時または次回呼び出しで再試行可能にする
- ユーザーの Refresh 操作は初期化済みでも実処理を再実行できる

単純な `bool _initialized` のみでは、プリロードとページ表示が同時に発生した場合の重複実行を防げないため使用しない。実行中 Task を共有する。

概念コード:

```csharp
private readonly object _initializationLock = new();
private Task? _initializationTask;
private bool _isInitialized;

public Task EnsureInitializedAsync()
{
    lock (_initializationLock)
    {
        if (_isInitialized)
            return Task.CompletedTask;

        return _initializationTask ??= InitializeAndTrackAsync();
    }
}

private async Task InitializeAndTrackAsync()
{
    try
    {
        await InitializeCoreAsync();
        lock (_initializationLock)
            _isInitialized = true;
    }
    finally
    {
        lock (_initializationLock)
            _initializationTask = null;
    }
}
```

実装時は、初期化失敗と同時呼び出しの race condition が起きないよう、成功状態と Task の更新を同じロックで管理する。

## ViewModel ごとの初期化方針

### Dashboard

- Page の `OnLoaded()` とプリローダーは `EnsureInitializedAsync()` を呼ぶ
- 初回 Core は既存 `RefreshAsync()` を使用する
- Refresh ボタン、自動更新タイマー、明示的更新は引き続き `RefreshAsync()` を直接呼び、初期化済みでも再取得する
- 初回ロード中に自動更新が重ならないよう、自動更新開始タイミングを初期化完了後にする
- `StateSnapshotService.ExportAsync()` の重複出力を防ぐ

### Settings

- `SettingsViewModel.Load()` の ViewModel データ読込部分を `EnsureInitializedAsync()` から呼べるようにする
- 同期処理を無理に `Task.Run()` へ移さない。UI Observable Property の更新は Dispatcher コンテキスト上で行う
- `PasswordBox` への値設定など、Page Control に依存する処理は `SettingsPage.OnLoaded()` に残す
- Page 表示時はデータ初期化完了後、Control への反映だけを実行する

### Editor

- 初回 Core は既存 `LoadProjectsAsync()` を使用する
- Page 未生成時でもプロジェクト一覧をロードできることを維持する
- `OnOpenInEditor` など Page 間コールバック未設定でも初期化が失敗しないこと
- 外部ナビゲーション要求がある場合は、Page 表示時に必要なファイルを開く既存動作を維持する

### Wiki

- 初回 Core は既存 `InitAsync()` を使用する
- 起動時はプロジェクト・ドメイン候補までをロードする
- 特定ドメインの `InitializeWikiDomainAsync()` は選択状態が明確な場合のみ既存どおり実行する
- 未完了トランザクション復旧を全ドメインに対して起動時実行しない

### Git Repos

- 初回 Core は既存 `InitAsync()` を使用する
- リポジトリの明示的な Scan はプリロード対象外とする
- Scan ボタンの動作は変更しない

### Asana Sync

- 初回 Core は既存 `InitAsync()` を使用する
- `StartScheduler()` は従来どおりアプリ起動時に1回だけ呼ぶ
- プリロードによって Asana Sync 自体を自動実行しない

### Setup

- 初回 Core は既存 `LoadProjectNamesAsync()` を使用する
- プロジェクト一覧と既定選択に必要な Workstream データまでロードする
- Setup 操作によるファイル作成・変更は自動実行しない

### Agent Chat

- 初回 Core は既存 `InitializeAsync()` を使用する
- 既存の `_historyLoaded` 相当のガードは `EnsureInitializedAsync()` の Task 共有へ統合するか、矛盾しないよう整理する
- CLI プロセスやチャット送信は開始しない

### Agent Hub

- 初回 Core は既存 `InitializeAsync()` を使用する
- 組み込み Skill の初回展開は既存の正常な初期化副作用として許容する
- Deploy、Undeploy などのユーザー操作は自動実行しない
- Page 固有のコールバックが未設定でもライブラリとプロジェクト一覧をロードできること

### Timeline

- 初回 Core は既存 `InitAsync()` を使用する
- `LoadEntriesAsync()` と `LoadHeatmapAsync()` の既存内部並列は維持してよい
- Timeline と他ページの重いロードは並行させない
- 初期化中に Timeline を開いた場合は同一 Task を待ち、二重走査を発生させない

### Schedule

- 最後にプリロードする
- 現在週のタスク、Schedule block、設定済み外部カレンダー予定までロードする
- ICS は URL が設定されている場合のみ通信する
- Outlook は連携が有効な場合のみ既存取得経路を実行する
- 外部連携が無効ならネットワーク通信や Outlook 起動を行わない
- Outlook COM に必要な apartment/thread 制約は既存実装を維持し、ViewModel 全体を `Task.Run()` で囲まない
- 初期ロード中に週移動が要求された場合、古い結果で新しい週を上書きしないようロード世代または CancellationToken で保護する

負荷が問題になる場合の第2案として、Schedule の初期化を次の2段階へ分離できるようにする。

1. 起動時: タスク、ローカル Schedule block をロード
2. 順次プリロードの最終段: ICS、Outlook 予定を取得してマージ

初期実装では既存 `LoadWeekAsync()` を最後に順次実行し、実測で UI 応答性に問題がある場合に2段階化する。

## 新規サービス

### `StartupPreloadService`

`Services/StartupPreloadService.cs` を追加し、プリロード順序、例外隔離、進行状態を一箇所に集約する。`App.xaml.cs` で Singleton 登録する。

責務:

- 対象 ViewModel の `EnsureInitializedAsync()` を定義順に await する
- プリロード全体の多重起動を防止する
- 各ページ単位で例外を捕捉し、後続処理を継続する
- アプリ終了時の CancellationToken を受け取れる構造にする
- UI ダイアログや MessageBox を表示しない
- Debug ログへ開始、成功、失敗、所要時間を記録する

想定 API:

```csharp
public sealed class StartupPreloadService
{
    public Task PreloadAsync(CancellationToken cancellationToken = default);
}
```

ログ例:

```text
[StartupPreload] Editor started.
[StartupPreload] Editor completed in 143 ms.
[StartupPreload] Timeline failed after 820 ms: ...
[StartupPreload] Schedule completed in 1,932 ms.
```

## MainWindow からの起動

`MainWindow` へ `StartupPreloadService` を DI し、Dashboard ナビゲーションと初回表示準備後に開始する。

要件:

- `Loaded` のたびに開始せず、Window 生存期間中1回だけ開始する
- Dispatcher の低優先度で開始し、初回描画を先に通す
- fire-and-forget にする場合も例外を未監視にしない。サービス内部でページ単位および全体の例外を処理する
- Window 終了時にキャンセルできるよう `CancellationTokenSource` を保持する
- 通常の Close はアプリ終了ではなく非表示になるため、非表示だけではキャンセルしない
- Shift+Close など実際のアプリ終了経路でキャンセルする

## UI スレッドとバックグラウンド処理

ViewModel の初期化メソッド全体を `Task.Run()` へ入れない。

各 ViewModel は `ObservableCollection` や Observable Property を更新するため、`EnsureInitializedAsync()` は WPF Dispatcher 上から開始し、await 後の UI 更新が Dispatcher に戻る既存動作を維持する。重いファイル走査や CPU 処理のみ、既存サービスまたは ViewModel 内部で `Task.Run()` / 非同期 I/O を使用する。

必要に応じて以下の形に分離する。

```text
Dispatcher thread:
  Loading 状態更新
    -> background I/O / scan
  ObservableCollection へ結果反映
  Loaded 状態更新
```

## 競合・再入対策

以下を必須確認項目とする。

- プリロード中にユーザーが同じタブを開いても初期ロードは1回だけ
- Dashboard の Page load とプリローダーが同じ初期化 Task を共有する
- プリロード完了後にタブを開いた場合、Page の `Loaded` で再ロードしない
- Page を離れて戻った際、`Loaded` が再発火しても不要な再ロードをしない
- Refresh ボタンや明示的な再同期は初期化ガードに阻害されない
- 初期化失敗後、対象タブを開けば再試行できる
- Timeline/Schedule ロード中にユーザー操作で条件が変わっても、古い結果が新しい状態を上書きしない
- Project Discovery の初回スキャンを複数ページが同時に実行しない

必要であれば `ProjectDiscoveryService` 自体にも実行中 Task 共有を追加し、将来の並列呼び出しやユーザー操作との競合を防ぐ。ただし、本タスクの初期実装では順次プリロードと ViewModel の Task 共有を優先する。

## エラー処理

- ページ単位で `try/catch` し、失敗しても次ページへ進む
- `OperationCanceledException` はアプリ終了時の正常な中断として扱う
- プリロード失敗時に MessageBox、ContentDialog、トーストを表示しない
- 失敗したタブは未初期化状態に戻す
- 対象タブを実際に開いたときは既存 UI の Status/Error 表示を利用して再試行結果を示す
- API キー、Asana Token、LLM Key、ICS URL の秘密部分をログへ出力しない

## 変更対象

### 新規

| ファイル | 内容 |
|---|---|
| `Services/StartupPreloadService.cs` | 順次プリロードのオーケストレーター |

### 中核変更

| ファイル | 内容 |
|---|---|
| `App.xaml.cs` | `StartupPreloadService` の Singleton 登録 |
| `MainWindow.xaml.cs` | 初回描画後の低優先度プリロード開始、終了時キャンセル |

### Page 変更

各 Page の `Loaded` から、既存の初期ロードメソッドではなく対応 ViewModel の `EnsureInitializedAsync()` を呼ぶ。

- `Views/Pages/DashboardPage.xaml.cs`
- `Views/Pages/EditorPage.xaml.cs`
- `Views/Pages/WeeklySchedulePage.xaml.cs`
- `Views/Pages/TimelinePage.xaml.cs`
- `Views/Pages/WikiPage.xaml.cs`
- `Views/Pages/GitReposPage.xaml.cs`
- `Views/Pages/AsanaSyncPage.xaml.cs`
- `Views/Pages/AgentHubPage.xaml.cs`
- `Views/Pages/AgentChatPage.xaml.cs`
- `Views/Pages/SetupPage.xaml.cs`
- `Views/Pages/SettingsPage.xaml.cs`

### ViewModel 変更

各 ViewModel に初回 Task 共有と成功状態管理を追加する。

- `ViewModels/DashboardViewModel.cs`
- `ViewModels/EditorViewModel.cs`
- `ViewModels/WeeklyScheduleViewModel.cs`
- `ViewModels/TimelineViewModel.cs`
- `ViewModels/WikiViewModel.cs`
- `ViewModels/GitReposViewModel.cs`
- `ViewModels/AsanaSyncViewModel.cs`
- `ViewModels/AgentHubViewModel.cs`
- `ViewModels/AgentChatViewModel.cs`
- `ViewModels/SetupViewModel.cs`
- `ViewModels/SettingsViewModel.cs`

既に `_initialized` や `_historyLoaded` を持つ ViewModel は、二重の状態管理を増やさず、共有 Task 方式へ統合する。

## 実装フェーズ

### Phase 1: 初期化の冪等化

1. 各 ViewModel の現行初期ロード経路を初回 Core と明示 Refresh に整理する
2. `EnsureInitializedAsync()` と実行中 Task 共有を追加する
3. 既存の `_initialized` / `_historyLoaded` を整理する
4. 各 Page の `Loaded` を `EnsureInitializedAsync()` 呼び出しへ変更する
5. Page の UI Control 依存処理は `Loaded` に残す

### Phase 2: 順次プリローダー

6. `StartupPreloadService` を追加する
7. 軽量ページから重いページへ順次 await する
8. ページ単位の例外隔離、キャンセル、所要時間ログを追加する
9. `App.xaml.cs` に Singleton 登録する
10. `MainWindow` の初回描画後に低優先度で開始する

### Phase 3: 重負荷・競合調整

11. Timeline と Schedule のロード中に UI が固まらないことを確認する
12. Schedule の ICS/Outlook 取得が重い場合は2段階ロードへ分離する
13. Project Discovery の重複スキャンが残る場合は Service 側で実行中 Task を共有する
14. アプリ終了時キャンセルと再試行動作を確認する

## 受入条件

### 基本動作

- アプリ起動直後は Dashboard が従来と同等以上の速度で表示される
- Dashboard 表示後、ほかの全タブの初期データロードが自動的に開始される
- プリロードは定義順に1件ずつ実行される
- プリロード完了後に各タブを初めて開いた際、初期データ待ちが原則発生しない
- Page の Visual Tree は、その Page へ初めてナビゲートするまで生成されない

### 競合

- プリロード中のタブをユーザーが開いても、同じ初期処理が二重実行されない
- タブを往復して `Loaded` が再発火しても初期処理が再実行されない
- Refresh、Scan、Sync、週移動などの明示操作は従来どおり再実行できる

### 負荷

- 起動直後に全タブを並列ロードしない
- Timeline と Schedule の重いロードが同時実行されない
- プリロード中もウィンドウ移動、ナビゲーション、ボタン操作が応答する
- 外部カレンダー設定がない場合、ICS 通信や Outlook 起動が発生しない

### 障害時

- 1タブの初期ロードが失敗しても、後続タブのプリロードが続く
- 失敗したタブを開くと初期化を再試行できる
- プリロード失敗による未処理例外でアプリが終了しない
- プリロード中にアプリを終了しても、終了処理が長時間ブロックされない

## 手動テスト

このリポジトリには自動テストがないため、以下を手動確認する。

1. 通常設定で起動し、Dashboard が先に表示されること
2. Debug ログで全対象タブが順次ロードされること
3. プリロード完了後、各タブを順に開き、初期データが即時表示されること
4. Timeline のプリロード中に Timeline を開き、二重ファイル走査が発生しないこと
5. Schedule のプリロード中に Schedule を開き、同じ週が二重ロードされないこと
6. プリロード中に Refresh、週移動などを操作しても表示内容が巻き戻らないこと
7. 1つの初期化処理を意図的に失敗させ、後続タブがロードされること
8. 失敗したタブを開き、再試行できること
9. ICS 未設定、Outlook 連携無効で外部アクセスが発生しないこと
10. ICS 設定済み、Outlook 連携有効の各条件で Schedule が正常に完成すること
11. プリロード途中で Shift+Close し、正常終了すること
12. `dotnet build` でコンパイルエラーがないこと
13. 最終確認として `dotnet publish -p:PublishProfile=SingleFile` が成功すること

## 対象外

- 全 Page の Visual Tree を起動時に生成・キャッシュすること
- Git Repos の自動 Scan
- Asana の自動手動 Sync 実行
- Wiki の全ドメイン本文一括ロード
- Editor の全ファイル本文一括ロード
- Agent Chat の CLI 自動起動
- プリロード進捗を表示する新規 UI
- プリロード順序や有効/無効を変更する新規設定画面
