# リンク検出のテスト

リンク切れ・孤立ページの検出ビュー（ISSUE #56）の確認用。
**検出されるべきものと、誤検出してはいけないものを 1 つずつ並べてある。**

## 検出されるべきもの

- 存在しないファイル: [missing](missing.md)
- 走査範囲の外にある実在ファイル: [outside](../sample.md)
- 解決できない wikilink: [[存在しないページ]]

## 検出してはいけないもの

- 存在するファイル: [ok](ok.md)
- サブフォルダの実在ファイル: [note](sub/note.md)
- 自己リンク: [self](index.md)
- 解決できる wikilink: [[ok]]
- 画像（存在しないパスでも対象外）: ![img](nonexistent.png)
- 外部 URL: [example](https://example.com/nonexistent.md)
- 自動リンク: <https://example.com/>
- ページ内アンカーのみ: [この見出しへ](#確認手順)
- md 以外の拡張子: [pdf](nonexistent.pdf)

入れ子（表・リスト・引用・段落）の中のリンクは [nested.md](nested.md) を参照。

## 確認手順

1. このファイルを開いて `Ctrl+Shift+L`
2. **リンク切れが 3 件**（`missing.md` / `../sample.md` / `[[存在しないページ]]`）
3. 行番号をクリックして、参照元の該当行へ飛ぶこと
4. 表示を「孤立ページ」に切り替えると `orphan.md` が出ること
   （`index.md` は既定で除外。チェックを外すと出る）
