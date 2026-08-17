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
