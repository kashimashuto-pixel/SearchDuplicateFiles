# Search Duplicate Files

Windows Forms で動作する重複ファイル検出ツールです。指定した1つ以上のフォルダー内のファイルをサイズで絞り込み、候補だけを SHA-256 で確認して、内容が同じファイルを一覧表示します。

## 主な機能

- 1つ、または複数の対象フォルダーを指定した重複ファイル検索
- 複数フォルダーを比較するときの「対象フォルダー間の重複だけ表示」
- サブフォルダー検索のオン/オフ
- 隠しファイル、システムファイルを含める設定
- ファイル名とフォルダー名を別々に指定できる部分一致・ワイルドカード検索（`;` 区切り）
- 「フィルター適用」ボタンで、再スキャンせず保持している結果へ条件を適用
- グループ内の1件がパターンに一致すれば、比較対象を含むグループ全体を表示
- フィルター表示を「重複グループ全体」「条件に一致したファイルのみ」から選択
- 結果一覧の各列をクリックして昇順・降順に並べ替え
- 最小ファイルサイズ指定
- スキャンのキャンセル
- タイトルバー、フッター、タスクバーアイコンでの進捗表示
- グループごとの色分け表示
- 重複結果の CSV 保存
- 対象ファイルを開く、エクスプローラーで場所を開く
- 選択したファイルをごみ箱へ移動

## 動作環境

- Windows
- .NET 8 Desktop Runtime

開発、ビルドには .NET 8 SDK 以降が必要です。

## ビルド

```powershell
dotnet build
```

## 実行

```powershell
dotnet run --project src/SearchDuplicateFiles.WinForms/SearchDuplicateFiles.WinForms.csproj
```

## 配布用ビルド例

.NET ランタイムを同梱しない場合:

```powershell
dotnet publish src/SearchDuplicateFiles.WinForms/SearchDuplicateFiles.WinForms.csproj -c Release -r win-x64 --self-contained false
```

.NET ランタイムを同梱する場合:

```powershell
dotnet publish src/SearchDuplicateFiles.WinForms/SearchDuplicateFiles.WinForms.csproj -c Release -r win-x64 --self-contained true
```

出力先は `src/SearchDuplicateFiles.WinForms/bin/Release/net8.0-windows/win-x64/publish/` です。

## 使い方

1. `追加...` から対象フォルダーを1つ以上追加します。
2. 必要に応じて検索オプションを調整します。
3. フォルダー同士の比較だけを見たい場合は `対象フォルダー間の重複だけ表示` をオンにします。
4. `スキャン開始` を押します。
5. 検出結果はグループごとに色分けされます。

## 検出方法

1. ファイルサイズが同じものだけを候補にします。
2. 候補ファイルの SHA-256 ハッシュを計算します。
3. サイズとハッシュが同じファイルを重複グループとして表示します。
4. `対象フォルダー間の重複だけ表示` がオンの場合、2つ以上の対象フォルダーにまたがるグループだけを表示します。

スキャン中にアクセスできないファイルや、サイズが変更されたファイルはスキップして警告に記録します。ジャンクションやシンボリックリンクなどの再解析ポイントは、循環や意図しない大量スキャンを避けるため対象外にしています。

## ライセンス

MIT License です。商用利用、改変、再配布が可能です。

このプロジェクトは標準の .NET / Windows Forms API のみを使用しており、追加の外部 NuGet パッケージには依存していません。
