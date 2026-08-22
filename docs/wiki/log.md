# Hirake wiki — Operation Log

追記のみ。先頭に `YYYY-MM-DD` を付けて、新しいものを下に足す。

---

2026-08-14 ingest — `README.md` を取り込み。wiki 初回構築。13 ページを新規作成（overview / viewer-features / knowledge-exploration / canvas / workspaces / multi-window / uri-scheme / file-association / distribution / build / data-paths / shortcuts / third-party）と `index.md`。
  - 判断: **README.md は `docs/raw/` へコピーしない。** すでに git 追跡された生きたファイルで、複製すると 2 つが乖離するため。出典は `README.md` を直接指す
  - `hirake-distribution.md` の「なぜ自己完結発行か（.NET 10 Desktop Runtime のインストーラが管理者権限を要求する）」だけ README 外の出典（ISSUE #2 の進捗コメント）。README には理由が書かれていないため補った
  - `hirake-data-paths.md` は confidence: medium。README が明示するのは 3 パスのみで、WebView2 / temp は `CLAUDE.md` 由来、MSIX 化時の仮想化は未検証
  - リンク切れ 1 件を意図的に残した: `[[hirake-architecture]]`（出典は `CLAUDE.md`。次に書く候補）

2026-08-16 update — ISSUE #57（`v*` タグのルールセット）を受けて `hirake-distribution.md` に「リリースの手順と決まりごと」を追加。`index.md` の 1 行説明も更新。
  - 出典に ISSUE #57 と `.github/workflows/release.yml` を追加
  - **「権限の分離ではない」ことを明記した。** リポジトリは個人アカウントの単独所有で、ルールセット自体を編集できるのはタグを打つ本人と同じ。柵であって権限制御ではない
  - ルールセットの作成自体はまだ未実施（管理画面での操作のため人の作業）。**設定が入る前にページを書いている**点は、確認後に `confidence` を見直す余地がある

2026-08-16 ingest — この会話（ISSUE #54 / #57 / #59 / #60 / #61 / #66 の実装とレビュー）を取り込み。
  - 新規 `hirake-directory-links.md`。リンクで束ねたフォルダの走査は、機能としても安全判定としても
    独立した概念で、既存のどのページにも収まらなかったため
  - 更新 5 ページ: `hirake-knowledge-exploration`（走査の範囲）、`hirake-workspaces`（保存パスが実体になる。
    2026-08-16 更新として明示）、`hirake-uri-scheme`（無視するものにリンク経由を追記）、
    `hirake-data-paths`（settings.json を直接編集する設定の表を新設）、
    `hirake-distribution`（#66 でリリース検証が壊れていた事実を追記）
  - **昇格 1 件**: 「GitHub Actions の `shell: bash` は `-e` 付きで起動する」を
    `~/wiki/knowledge/github-actions-shell-bash-errexit.md` へ。GitHub Actions 一般の挙動で、
    このリポジトリを他人に渡しても一緒に行くべき知識ではないため
  - 判断: `#59`（TOCTOU のハンドルベース化）は**未着手のまま**。調査結果だけを
    `hirake-directory-links.md` の脅威モデル節に書いた。「対応済み」と読めないよう
    「防げないこと」として明記している
  - 出典に ISSUE の URL を使った。`docs/raw/` へのコピーは行っていない
    （ISSUE は GitHub 側が正で、複製すると乖離するため。README を raw に置かない判断と同じ）

2026-08-16 ingest — `README.md` を再取り込み（初回は 2026-08-14）。
  - **新規ページは無し。** README の ## 見出し 13 件すべてに受け皿があり、全 14 ページが sources に README.md を挙げている状態を確認した
  - ショートカット表を機械的に照合。README 20 行と wiki 20 行が完全一致（差分なし）
  - **取りこぼしを 1 件回収**: 「リンクを張り替えて参照先を切り替える運用では、ワークスペースに保存した時点の実体が記録される」（README の「シンボリックリンクで束ねたフォルダ」節）。
    `hirake-directory-links.md` に節を新設し、`hirake-workspaces.md` の注記からも触れるようにした
  - 前回（2026-08-16 の会話取り込み）で作った `hirake-directory-links.md` は ISSUE 由来で書いたが、README にも同じ機能の記述がある。**出典としては README を正とし、ISSUE は経緯と実測値の出典**という位置づけで両方を sources に残す
  - 判断（前回の再確認）: **README.md は `docs/raw/` へコピーしない。** git 追跡された生きたファイルで、複製すると乖離するため

2026-08-16 update — ISSUE #70（PR でビルドとテストを走らせる）の実装にあわせて `hirake-distribution.md` を更新。
  - 「リリースの手順と決まりごと」に「PR の段階で分かること」の節を追加。出典に ISSUE #70 を追加
  - **Inno Setup の SHA-256 照合が PR でも走ることを要点として書いた。** 取得元の差し替えは初回のリリースが成功しても消えないリスクで、PR で回していればタグを打つ前に気づける
  - #66 の注記に「PR でも同じビルドが走るため、タグ時点で初めて失敗する範囲は狭い」を追記

2026-08-17 update — ISSUE #53（`[[wikilink]]` 記法）の実装にあわせて更新。
  - **新規ページ `hirake-wikilinks.md`。** 設計の芯（`LinkInline` に載せると描画・ナビゲーション・グラフ・バックリンクが追随する）、
    名前解決と同名衝突の優先順位、誤爆させない受理条件 9 項目、URL を相対パスで出す理由を書いた
  - `hirake-viewer-features.md` に「`[[wikilink]]` 記法」と「`#見出し` つきのリンク」を追加
  - `hirake-knowledge-exploration.md` のナレッジグラフ行に「wikilink もエッジになる」を追記
  - `hirake-uri-scheme.md` に「`FindHeadingLine` は文書内リンクからも使われる」を追記。
    **`[表示](other.md#見出し)` は 2026-08-17 までスクロールしていなかった**（フラグメントを見ていなかった）ことが分かったため
  - 実測値を本文に残した: 索引はパス列挙のみで 1〜6 ms、実経路で 34〜47 ms（500 ファイル）。本文まで読むと初回 4.6 秒

2026-08-17 ingest — この会話（Wiki 形式の文書を読ませたときの弱点分析、v1.0.0 リリース、ルールセットの動作確認）を取り込み。
  - **新規 `hirake-editing-boundary.md`。** 「Hirake は書き換えない」という方針そのものが、既存のどのページにも
    書かれていなかった。#55（外部エディタ）と #56（検出に留める）はどちらもこの方針から出た判断なので、
    個別ページに散らすより 1 枚にまとめた
  - **confidence: medium にした。** #55 / #56 は**未実装**で、書いたのは決定した方針であって動作ではない。
    本文の冒頭にも同じ注記を置いた（実装済みと読まれると、あるはずの機能を探すことになるため）
  - `hirake-overview.md` の「設計の芯」を 4 点 → 5 点に。5 番目「ファイルを書き換えない」を追加
  - `hirake-uri-scheme.md` に「外部への受け渡し口としても使える」を追記。
    **「この位置へのリンクをコピー」は Hirake 同士のリンク用に作ったが、パスと行番号が入っているため
    そのまま AI に渡せる。** 書き換えない方針のもとで既存機能だけで成立している唯一の編集経路
  - `hirake-viewer-features.md` の自動リロードに「ライブプレビューとして使える」を追記。
    **「編集 → 表示」は既に成立していて、欠けているのは逆方向だけ**という整理がこのページからも辿れるように
  - `hirake-distribution.md` に「ルールセットが効いているか確かめる」を新設（2026-08-16 実施、5 項目すべて期待どおり）。
    v1.0.0 公開の事実も冒頭に記録
  - 昇格候補（今回は上げない）: 「タグ保護のルールセットは**検証用タグも消せなくする**ため、
    後始末に Enforcement の一時無効化が要る」。GitHub 一般の挙動だが、手順がこのリポジトリの
    ルールセット ID に紐づいており、汎用形に切り出すには 2 例目が要ると判断した
  - この会話は `docs/raw/` に置いていない。`docs/raw/README.md` が「LLM はここを読むだけ」と定めているため、
    出典は ISSUE の URL を使った（過去 2 回の会話取り込みと同じ扱い）

2026-08-18 update — ISSUE #55（読んでいる位置を外部エディタで開く）の実装にあわせて更新。
  - **新規ページ `hirake-external-editor.md`。** コマンドラインを組み立てない理由、UseShellExecute を false 固定にする理由、
    検出の 3 経路、プリセットを検証済みだけに絞る方針、Ctrl+E が 2 経路要ることを書いた
  - `hirake-viewer-features.md` に「エディタで開く（Ctrl+E）」を追加
  - `hirake-shortcuts.md` に Ctrl+E を追加し、「WebView2 にフォーカスがあると届かない」キーの一覧へ Ctrl+E を加えた
  - **実測を本文に残した**: App Paths の登録は開発機で 84 件あるが、インストール済みの VS Code もサクラエディタも含まれていない。
    検出を App Paths だけに頼ると取りこぼす、という根拠
  - **`hirake-editing-boundary.md` との住み分けを整理した**（develop のマージ時）。
    境界ページは「なぜ書き換えないか」という方針、`hirake-external-editor.md` は「実装した仕組み」。
    前者に残っていた「#55 は未実装」の記述を実装済みへ改め、confidence を medium → high に上げた
    （未実装なのは #56 だけになったため、その節にのみ注記を残す）

2026-08-18 update — ISSUE #56（リンク切れ・孤立ページの検出ビュー）の実装にあわせて更新。
  - **新規ページ `hirake-link-check.md`。** 「捨てるのをやめる」が起点であること、未解決の理由 4 分類、
    誤検出させない境界、上限を必ず画面に出す方針、実測値を書いた
  - **訂正を本文に残した**: ISSUE 起票時の「wikilink 対応が入れば壊れた `[[...]]` も自動的に検出対象に乗る」は誤り。
    解決できない `[[...]]` は `LinkInline` にならず `HtmlInline`（span）になるため、素通りしていた。
    `hirake-editing-boundary.md` に残っていた同じ見立ても訂正した
  - **新たに見つかった見落とし**: 2MB 超のファイルは以前から中身を 1 行も読んでいなかったが、
    そのことがどこにも出ていなかった。`UnreadableFileCount` として画面に出す設計にした
  - `hirake-editing-boundary.md` の「#56 は未実装」を実装済みへ改めた（方針は境界ページ、仕組みは新ページ）
  - `hirake-knowledge-exploration.md` / `hirake-shortcuts.md` / `hirake-viewer-features.md` に
    Ctrl+Shift+L と仮想タブ「リンク検出」を追加

2026-08-18 update — ISSUE #76（リリースタグを csproj の <Version> から生成する）にあわせて更新。
  - `hirake-distribution.md` の「タグを打つ」を、手打ちから `scripts/tag-release.ps1` に差し替えた
  - **記述と実態の食い違いを 1 件直した。** これまで手順は `git switch develop` と書いていたが、
    実際の `v1.0.0` は main のマージコミット `a36761a` に打たれている。`verify-tag` は
    「main **または** develop に含まれること」しか見ないため、どちらでも CI は通り、
    食い違いに気づけない状態だった。Releases と main を一致させる方を正とした
  - **柵の引き継ぎを明記した**: タグ名を csproj から生成すると、`verify-tag` の
    「タグ名と版が一致すること」は必ず成功するようになる。版の上げ忘れは不一致ではなく
    **重複**として現れるため、スクリプト側の重複検出がその役目を引き継ぐ

2026-08-19 update — タグを打つ先を **main に一本化**する決定を反映。
  - `hirake-distribution.md` の「守る決まり」1 を「main **または** develop」から
    「**main のマージコミットへ打つ**」に改め、「打つ先は main のマージコミット」の節を新設した
  - **CI との差を明記した。** `verify-tag` が見るのは「main か develop に含まれること」＝
    未マージでないこと（#52）だけで、develop の先端に打っても通る。
    **運用ルールの方が CI より厳しい**状態なので、守っているのは
    `scripts/tag-release.ps1` の既定（`-Branch main`）と手順書であって CI ではない、と書いた
  - あわせて `installer/README.md`（wiki 外）の「main か develop に打ってください」も直した。
    命令形で書かれており、放置すると手順書どうしが矛盾するため

2026-08-20 update — `tag-release.ps1` が push 成功を失敗と報告していた件（#80）を反映。
  - `hirake-distribution.md` に「成否は git の終了コードだけで見る（#80）」を新設した。
    Windows PowerShell 5.1 は `Stop` のとき、ネイティブコマンドが stderr へ書いただけで例外を投げる。
    `git push` は成功時にも `To <remote>` を stderr に出すため、成功が失敗になっていた
  - **#66 と同じ種類の誤り**（失敗ではないものを失敗と見なす）である、と両者を結んだ。
    片方だけ直しても同じ形の誤りが別の場所で再発するため
  - ロールバックの決まりを書いた。**「リモートに無い」と確かめたときだけローカルタグを戻す。**
    確かめられないときは残す
  - `-Prefix` で `v*` 以外のタグ名にして作成〜push を試せることを書いた。
    #76 の検証はこの経路だけ通っておらず、そこに不具合があった

2026-08-21 update — リリースの起点を **develop → main のマージ**へ移した件（#81）を反映。
  - `hirake-distribution.md` の「タグを打つ」を「**リリースする（main へマージする）**」に置き換え、
    `release-on-main.yml` の 3 ジョブ（check-version / build / publish-release）と、
    **ビルドが通ってからタグを作る**という順序の意味を書いた
  - 従来の手順は「**手元からタグを打つ（逃げ道）**」として残した。`release.yml` と
    `scripts/tag-release.ps1` は消さず、手動経路のために維持する
  - **下書きの Release ではタグが作られない**（GitHub は公開時に作る）ため、
    Git refs API で明示的に作ってから Release を作る、という設計上の要点を明記した
  - 「打つ先は main のマージコミット」の節に、マージ起点では `github.sha` により
    構造的に守られる（運用ルールと CI の差が消える）ことを追記した
  - 版の上げ忘れ検出（`version-bumped`）は**必須ステータスチェックではない**ことを明記。
    ルールセットの変更は #81 の非スコープのため、赤く見えるところまでが実装範囲

2026-08-21 update — 戻る・上階層移動の 2 ボタン追加（#84）を反映。
  - `hirake-viewer-features.md` の「操作」に 2 つを追加した。**戻るはタブを閉じた後でも効く**
    （履歴をタブ参照ではなくパスで持ち、タブが無ければ開き直す）ことと、
    **上階層はタブを動かさずツリーのルートだけを移す**ことが要点
  - `hirake-shortcuts.md` に `Alt+←` / `Alt+↑` を追加し、「実装上の注意」へ 2 点を書いた。
    (1) WebView2 は既定で `Alt+←` を自分の履歴の戻るとして扱うため viewer.js で
    `preventDefault()` してから転送する (2) WPF は Alt 併用時に `Key` が `Key.System` になり、
    実際のキーは `SystemKey` に入る
  - 上げたルートの保持規則（読んでいるファイルが配下にある限り維持し、外れたら解除）は
    「上げた直後に上のフォルダのファイルを開くと元へ戻ってしまう」を避けるための設計

2026-08-23 update — ISSUE #89（Ctrl+G の全体俯瞰でノードが見切れる）を受けて `hirake-canvas.md` に「俯瞰の収め方」を追加。
  - **収める範囲をノード中心から描画範囲（円 + ラベル）へ変えた**ことと、その理由を書いた。
    全角 15 文字のファイル名は半分だけで 82.5 に達し、従来の `pad = 80` を食い潰す
  - **ラベル幅を `getBBox()` ではなくオフスクリーン canvas で測る理由**を明記した。
    初回の俯瞰は `k = 1.0 > CARD_THRESHOLD` のため `body.mode-card` が付いており、
    `.cv-node-label` が `display: none`。DOM 実測だと円もラベルも含まない BBox が返る
  - **余白を画面 px に移した理由**（ワールド単位の余白は `pad * k` で痩せ、`k = 0.15` では実質 12px）
  - ズーム下限 `0.1` は今回のスコープ外。到達を `console.info` で記録するだけに留めた
