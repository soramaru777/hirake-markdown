# インストーラのビルド

`Hirake.iss` は [Inno Setup 6](https://jrsoftware.org/isdl.php) 用のスクリプトです。
リリースでは `.github/workflows/build.yml`（`release-on-main.yml` から呼ばれる）が
自動でビルドしますが、ウィザードの文言や関連付けの挙動を確認したいときはローカルでも作れます。

> **リリースにタグ付けの操作は要りません（ISSUE #81）。** `src\Hirake\Hirake.csproj` の
> `<Version>` を上げて develop → main の PR をマージすると、`release-on-main.yml` が
> ビルドし、**通ってからタグを作り**、下書き Release まで用意します。公開は手動です。
>
> 手元からタグを打つ `scripts\tag-release.ps1`（ISSUE #76）は、その経路が壊れたときの
> 逃げ道として残しています。タグ名は同じく `<Version>` から生成され、打つ先は
> **`main` のマージコミット**です。手で打つと写し間違いが起こりえますが、
> `v*` タグは打ち直しも削除もできません。
>
> 手動経路の CI（`verify-tag`）が見るのは「タグのコミットが `main` **か** `develop` に含まれること」です。
> どちらにも含まれないコミットに `v*` タグを打つと `build` ジョブが失敗し、リリースは
> 作られません（未マージのコードで配布物ができるのを防ぐため・ISSUE #52）。
> **CI は develop の先端も通すので、`main` に打つことを守るのはスクリプト側です。**
>
> この検証は**操作ミスを防ぐためのもの**で、権限の制御ではありません。ワークフローはタグが
> 指すコミット側の定義で動くため、検証を消したコミットにタグを打てば素通りします。
> ルールセット `protect-release-tags` は打ち直しと削除だけを禁じており、作成は制限していません
> （制限すると自分もリリースできなくなるため・ISSUE #57）。

## 必要なもの

- Inno Setup 6（日本語の言語ファイル `Japanese.isl` は標準で同梱されています）
- .NET 10 SDK

```powershell
winget install JRSoftware.InnoSetup
```

`ISCC.exe`（コマンドライン コンパイラ）が入ります。場所はインストールのスコープで変わります。

| スコープ | 場所 |
|---|---|
| ユーザー（`--scope user`） | `%LocalAppData%\Programs\Inno Setup 6\ISCC.exe` |
| マシン | `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` |

## 手順

リポジトリのルートで実行します。

```powershell
# 1. 自己完結で発行する（.NET ランタイムを同梱するため --self-contained true）
dotnet publish src/Hirake -c Release -r win-x64 --self-contained true -o publish

# 2. インストーラをコンパイルする（版は csproj から取る）
[xml]$proj = Get-Content src\Hirake\Hirake.csproj
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]
& "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe" "/DAppVersion=$version" installer\Hirake.iss
```

`dist\HirakeSetup-1.0.0.exe` ができます。

`/DAppVersion` は必須です（省略するとコンパイルエラーになります）。版の出所は
`src/Hirake/Hirake.csproj` の `<Version>` だけ、という約束を崩さないためです。
CI も同じ値を使い、タグと一致しなければリリースを中断します。

`publish` 以外の場所を使いたい場合は `/DPublishDir=...` で上書きできます。

## 確認する項目

インストーラを作り直したときは、次を一通り見てください。

| # | 確認 | 期待する結果 |
|---|---|---|
| 1 | 実行 | 管理者権限のプロンプトが出ない |
| 2 | ウィザード | 日本語で表示される。既定のインストール先が `%LocalAppData%\Programs\Hirake` |
| 3 | タスク | 「関連付け」「スタートメニュー」が ON、「デスクトップ」が OFF |
| 4 | Hirake 起動中にインストール | 「Hirake を終了してください」と促される |
| 5 | インストール後 | `.md` をダブルクリックすると Hirake で開く |
| 6 | インストール後 | `hirake://` リンクが Hirake で開く |
| 7 | 上書きインストール | 旧版がアンインストールされず、そのまま上書きされる |
| 8 | アンインストール | 「設定 > アプリ」から削除できる。関連付けも消える |
| 9 | アンインストール後 | `%LocalAppData%\Hirake\`（設定・ワークスペース・索引）は**残る** |
| 10 | 上書きインストール | 前版にだけあったファイル（ダミー DLL を置いて確認）が消える |
| 11 | 関連付け OFF でインストール | 自分で登録した関連付けが**維持される**。アンインストールしても消えない |
| 12 | 他のファイルがあるフォルダを指定 | インストールが中止される（終了コード 7） |
| 13 | `Hirake.exe` を消してから上書き | 前版のダミー DLL が消える（目印で上書きと判定される） |

9 は仕様です。再インストール時に設定と索引（再構築に時間がかかる）を
失わないようにしています。完全に消す手順はルートの `README.md` に書いています。

## インストール先の扱い

インストール先（既定は `%LocalAppData%\Programs\Hirake`）は**インストーラが管理する場所**です。
上書きインストールのとき、前版が置いたファイルを消してから新しいものを展開します。
ここに自分のファイルを置かないでください。

新規インストールでは何も消しません（前版がその場所に入っていることを
アンインストール情報で確認できたときだけ削除します）。設定やデータは
`%LocalAppData%\Hirake` にあり、こちらはインストーラが触りません。

## 変更するときの注意

- `AppId` の GUID は**恒久的に固定**です。変えると別アプリ扱いになり、
  旧版が残ったまま二重にインストールされます
- `AppMutex` は `SingleInstanceManager.MutexName`（`Hirake_SingleInstance_Mutex`）と
  一致させてください。ずれると「実行中に上書きできてしまう」状態になります
- `MinVersion` は `Hirake.csproj` の `TargetPlatformMinVersion` と揃えてください
- 関連付けの登録・解除は `scripts\register.ps1` / `scripts\unregister.ps1` を
  呼びます。インストーラ側でレジストリを直接書かないでください
  （`tests/test-association-lib.ps1` などのテストが効かなくなります）
