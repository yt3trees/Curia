# Wikiタブ初期画面 Wikiカード表示 実装計画

- 作成日: 2026-07-18
- 状態: 実装前

## 1. 目的

Wiki タブを開いた直後の初期画面に、現在存在する Wiki をカードで表示する。
カードから選択した Wiki のプロジェクトとドメインを指定して、既存の Wiki Pages 画面を直接開けるようにする。

## 2. 現状確認

- `MainWindow` の Wiki ナビゲーション先は `WikiPage` で、Wiki タブを開くと singleton の `WikiViewModel` が利用される。
- `WikiPage` のヘッダーには Project / Domain の選択コンボボックスがあり、コンテンツ部分は `HasWiki` と `IsProjectSelected` に応じて状態を切り替えている。
- 現在はプロジェクト未選択時に `No Project Selected` と表示し、カード一覧は表示していない。
- Wiki は各プロジェクトのコンテキスト配下にある `wiki/<domain>` ディレクトリを単位に管理している。
- 既存ドメインの列挙は `WikiService.GetDomains(contextPath)` が担当している。
- `WikiViewModel` は `SelectedProject` → `SelectedDomain` の順に選択されると、Wiki のページツリーや設定を非同期ロードする。
- Wiki の既存仕様書にはページ数・最終更新日の表示案があるが、現行 WikiPage には Wiki カード集合はまだ実装されていない。

## 3. 表示仕様

### 3.1 初期状態

- Wiki タブを開いた直後、`SelectedProject == null` の間は Wiki ランディング画面を表示する。
- 既存の Wiki ヘッダーと Project / Domain セレクターは残し、カード選択以外の手動操作も維持する。
- 初期状態では Pages / Query / Lint / Prompts の編集領域を表示せず、Wiki カード一覧をコンテンツ領域の主役にする。
- Wiki をカードから選択すると、既存の Pages タブとページツリー・プレビューへ切り替える。
- プロジェクトをコンボボックスから選択した場合は、既存の `Select a Wiki Domain` 状態を継続して利用する。
- Wiki が1件も存在しない場合は空のカード一覧を表示せず、Wiki を作成できる既存のプロジェクト選択フローを妨げない。
- 既存 WikiPage のテーマリソース、境界線、角丸、余白をカードにも使用する。
- カードが増えても初期画面を占有しすぎないよう、WrapPanel とスクロール領域を組み合わせる。

### 3.2 画面イメージ

Wiki タブを開いた直後の初期表示:

```text
+------------------------------------------------------------------+
| Wiki                         Project: [Select project     v]     |
+------------------------------------------------------------------+
|                                                                  |
| Select a Wiki                                      [Refresh]     |
|                                                                  |
| +------------------+  +------------------+  +------------------+ |
| | Curia            |  | Sales            |  | Curia            | |
| | ERP              |  | Web              |  | Infrastructure   | |
| | Pages       24   |  | Pages        8   |  | Pages       31   | |
| | Updated   2h ago |  | Updated   1d ago |  | Updated   5d ago | |
| +------------------+  +------------------+  +------------------+ |
|                                                                  |
| +------------------+                                             |
| | Project B        |  ... more Wiki cards in a scroll area       |
| | Domain           |                                             |
| +------------------+                                             |
+------------------------------------------------------------------+
```

カードをクリックした場合:

```text
[Curia / ERP card]
        |
        v
Wiki page (existing Pages view)
  Project: Curia
  Domain:  ERP
  Tab:     Pages
  Content: ERP Wiki のページツリーと index.md のプレビュー
```

Wiki が1件も存在しない場合:

```text
+------------------------------------------------------------------+
| Wiki                         Project: [Select project     v]     |
+------------------------------------------------------------------+
|                                                                  |
| No Wiki Found                                                    |
| Select a project above to initialize or open a Wiki domain.      |
|                                                                  |
+------------------------------------------------------------------+
```

### 3.3 カード単位

1枚のカードを「プロジェクト + Wiki ドメイン」の組み合わせとして作る。
同じプロジェクトに複数のドメインがある場合は、ドメインごとに別カードを表示する。

カードに表示する情報:

- プロジェクト表示名
- Wiki ドメイン名
- `pages/` 配下のページ数
- Wiki 内ファイルの最終更新日時

カード全体をクリック可能にし、クリック時はそのカードのプロジェクトとドメインを Wiki 画面へ渡す。
カード内に編集・削除などの操作は追加せず、今回の範囲を Wiki の閲覧入口に限定する。

### 3.4 可視性

- `WikiViewModel.InitAsync` が現在読み込んでいる表示対象プロジェクトをカード生成元にする。
- 現行仕様どおり、非表示プロジェクトは初期カード一覧から除外する。
- 非表示プロジェクトの表示切り替えを Wiki タブへ新規追加することは今回の範囲外とする。

## 4. 実装方針

### 4.1 Wiki カード用 ViewModel

`ViewModels/WikiViewModel.cs` に Wiki タブの初期画面専用 `WikiCardViewModel` を追加する。

保持する値の候補:

- `ProjectInfo Project`
- `string Domain`
- `string WikiRoot`
- `int PageCount`
- `DateTime? LastUpdated`
- 表示用のプロジェクト名、更新日時文字列

WikiViewModel には次を追加する。

- 表示用の `ObservableCollection<WikiCardViewModel>`
- `HasWikiCards` と `IsLanding` の表示用プロパティ
- 初期化時に全 Wiki カードを構築する処理
- ランディング画面から対象 Wiki を選択するコマンド
- カード一覧を再読み込みするコマンド

### 4.2 データ取得

- 既存の `WikiViewModel.InitAsync` で読み込んだ表示対象プロジェクトをカード生成元にする。
- 各 `ProjectInfo` から既存の `GetContextPath` でコンテキストパスを解決し、`WikiService.GetDomains` で存在するドメインだけを列挙する。
- 各ドメインについて `WikiService.GetAllPages` を読み、`IsRoot == false` のページ数と `LastModified` の最大値をカード統計に使う。
- ファイル列挙は Wiki タブの UI スレッドを塞がないようバックグラウンドで実行する。
- プロジェクト名、ドメイン名の順で安定して並べ、毎回の起動でカード順が不必要に変わらないようにする。
- 1プロジェクトのパス不備や読み取り失敗で Wiki タブ全体の初期化を失敗させず、その Wiki だけをスキップしてデバッグログに残す。
- 初回の `InitAsync` に加え、ランディング画面の Refresh 操作と Wiki ドメイン作成後にもカードを再集計する。

### 4.3 WikiPage の XAML

`Views/Pages/WikiPage.xaml` のプロジェクト未選択状態を Wiki ランディング画面へ置き換える。

- `WikiCards` を `ItemsSource` とする `ItemsControl` を追加する。
- `WrapPanel` とスクロール領域でカードを配置する。
- カード幅と主要な余白を固定し、長いプロジェクト名・ドメイン名は省略表示とツールチップで扱う。
- `HasWikiCards` が false の場合は `No Wiki Found` の空状態を表示する。
- カード全体を `SelectWikiCommand` に結びつけ、`WikiCardViewModel` をコマンドパラメーターとして渡す。
- ランディング画面では Pages / Query / Lint / Prompts のタブバーを非表示または無効化し、カード選択後に表示する。
- 既存の Project / Domain コンボボックスと、新規ドメイン作成の導線は壊さない。
- UI の表示文言は既存画面に合わせて英語にする。

### 4.4 カード選択と既存 Wiki 画面への接続

カードクリック時の処理は `WikiViewModel` 内にまとめ、`MainWindow` や Dashboard から WikiViewModel の内部状態を直接変更しない。

- `ActiveTab` を Pages に戻し、`IsCreatingNewDomain` を false にする。
- `ProjectInfo.HiddenKey` で `Projects` 内の対象プロジェクトを解決する。
- `SelectedProject` を先に設定する。既存の `OnSelectedProjectChanged` により、そのプロジェクトの `Domains` が再構築される。
- `SelectedDomain` を次に設定する。既存の `OnSelectedDomainChanged` からページツリーなどのロードを開始させる。
- 対象プロジェクトまたはドメインが見つからない場合は、別の Wiki を無言で開かず、現在のランディング画面を維持する。
- 正常に選択できた場合だけ、既存の Pages タブを表示する。

### 4.5 初期化と更新

- WikiPage の `Loaded` から既存の `EnsureInitializedAsync` を通して、プロジェクト一覧と Wiki カードを初期化する。
- Wiki タブから離れて戻ってきた場合にも、Refresh 操作でファイルシステム上の最新状態を反映できるようにする。
- Wiki ドメインを新規作成して成功した後は、カード一覧を再読み込みし、新しいカードを表示する。
- カード選択後の既存のページ編集、Query、Lint の状態管理は変更しない。

## 5. 変更予定ファイル

- `ViewModels/WikiViewModel.cs`
  - WikiCardViewModel、カード集約、ランディング状態、選択コマンド、更新処理
- `Views/Pages/WikiPage.xaml`
  - プロジェクト未選択時の Wiki カード一覧、空状態、カードテンプレート
- `Views/Pages/WikiPage.xaml.cs`
  - XAML コマンドだけで足りない場合に限り、ランディング画面の表示同期を追加

新しい永続化ファイルや Wiki ディレクトリ構造の変更は行わない。
`DashboardPage.xaml`、`DashboardViewModel.cs`、`MainWindow.xaml.cs` の変更は行わない。

## 6. 実装手順

1. WikiViewModel にカード表示用の型、コレクション、`IsLanding` を追加する。
2. Wiki 初期化処理から、表示対象プロジェクトの全 Wiki ドメインを集約する。
3. WikiPage のプロジェクト未選択状態をカード一覧と空状態へ置き換える。
4. カードクリックで `SelectedProject` → `SelectedDomain` の順に設定するコマンドを追加する。
5. Refresh 操作と Wiki ドメイン作成後のカード再読み込みを接続する。
6. ビルドと手動確認で初期表示・選択・既存機能への回帰を検証する。

## 7. 検証項目

自動テスト基盤がないため、実装後は `dotnet build Curia.csproj` を実行し、次の手動確認を行う。

- Wiki タブを初めて開いたとき、プロジェクト未選択のまま Wiki カード一覧が表示される。
- 1プロジェクトに複数ドメインがある場合、ドメインごとに1枚のカードが表示される。
- 複数プロジェクトの Wiki が混在しても、プロジェクト名とドメイン名の組み合わせを取り違えない。
- カードをクリックすると、対応するプロジェクト・ドメインが選択された Wiki の Pages タブが開く。
- Wiki のページ数と最終更新日時が、ファイル追加・削除後の Force Refresh で更新される。
- Project コンボボックスから手動選択した場合も、既存の Domain 選択と初期化フローが動作する。
- Wiki が存在しない場合は `No Wiki Found` を表示し、プロジェクト選択とドメイン初期化の導線を失わない。
- コンテキスト junction が未接続または壊れているプロジェクトがあっても、他プロジェクトのカード表示は継続する。
- Wiki タブの初期化中に画面が固まらず、読み込み中の状態を既存 `IsLoading` で表現できる。
- Wiki タブを離れて戻った後の Refresh で、追加されたドメインがカードに現れる。
- カード選択後も既存のページ編集、Query、Lint、Prompts が利用できる。

最終確認では必要に応じて `dotnet publish -p:PublishProfile=SingleFile` も実行する。

## 8. 受け入れ条件

- Wiki タブを開いた直後の初期画面に、現在存在する Wiki ドメインがカードとして表示される。
- カードはプロジェクトとドメインを識別でき、ページ数と更新状態を確認できる。
- カードクリックで、選択した Wiki の Pages タブを直接開ける。
- Wiki がない場合や読み取れない場合に、既存の Project / Domain 選択と Wiki 初期化を壊さない。
- Project コンボボックスからの従来操作とカードからの操作が同じ Wiki 初期化処理を通る。
- 既存の Wiki ファイル形式、保存処理、WikiPage の編集・Query・Lint 機能に変更を加えない。

## 9. 今回の範囲外

- Dashboard に Wiki カードを表示する機能
- Wiki カード上で Wiki ページ本文をプレビューする機能
- Wiki の新規作成・削除・ドメイン名変更
- Wiki ページの編集や Query 結果のカード化
- Wiki 統計を新しい永続化形式へ移行すること