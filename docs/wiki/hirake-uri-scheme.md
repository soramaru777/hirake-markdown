---
title: hirake:// ディープリンク
type: concept
project: hirake
scope: shared
sources:
  - README.md
related: [[hirake-file-association]] [[hirake-workspaces]] [[hirake-multi-window]] [[hirake-directory-links]]
confidence: high
updated: 2026-08-16
---

ブラウザ・チャット・タスク管理ツール・エディタなど他アプリから、**特定文書の特定位置**へ直接リンクするための URL スキーム。利用には `scripts/register.ps1` での登録が必要（→ [[hirake-file-association]]）。

## 文法

verb 形式（`hirake://<verb>?<params>`）。現在の verb は `open` と `workspace` の 2 つ。

### open

```
hirake://open?path=<URLエンコードした絶対パス>[&line=<1始まりの行番号>][&heading=<URLエンコードした見出しテキスト>]
```

| パラメータ | 説明 |
|---|---|
| `path` | 必須。ローカルの絶対パス。拡張子は `.md` / `.markdown` のみ |
| `line` | 任意。1 始まりの行番号 |
| `heading` | 任意。**見出しテキストそのもの**（`概要` など）。指定すると `line` より優先される |

```powershell
Start-Process "hirake://open?path=C%3A%5Cdocs%5Csample.md&heading=%E6%A6%82%E8%A6%81"
Start-Process "hirake://open?path=C%3A%5Cdocs%5Csample.md&line=42"
```

**`heading` に見出し id（`section-1` 等）ではなく見出しテキストを使う**のは、日本語見出しの id が自動採番になり人間が書けないため。

この照合（`HirakeUri.FindHeadingLine`）は文書内リンクからも使われる。`[[target#見出し]]` と `[表示](target.md#見出し)` は
どちらもここを通り、URI 経由と同じ規約で位置が決まる（→ [[hirake-wikilinks]]）。

### workspace

```
hirake://workspace?name=<URLエンコードしたワークスペース名>
```

- 同名のワークスペースを開いているウィンドウがあれば**前面化するだけ**で、二重には開かない。無ければ**新しいウィンドウ**で開く（1 ワークスペース = 1 ウィンドウ → [[hirake-workspaces]]）
- 名前は前後の空白を除いた**大文字小文字を無視する完全一致**で照合する。一致するものが無ければ黙って無視
- `name` は**パスとして一切解釈しない**。ワークスペースは名前をファイル名に使わない設計なので、トラバーサルの経路自体が存在しない

## 開き先の決まり方（open）

- Hirake が起動していなければ**起動して**開く
- 起動済みなら**既存ウィンドウの新規タブ**で開いて前面化する
- 同じファイルが既に開いていれば**そのタブへジャンプ**する
- 複数ウィンドウがある場合の宛先は「そのファイルを開いているウィンドウ → 無ければ最後にアクティブだったウィンドウ」

## セキュリティ: fail-closed

ブラウザや他アプリから任意に叩かれる入口なので、**行うのは「Markdown をタブで開く」ことだけ**。

以下は**黙って無視**する（エラーダイアログを出さないのは、外部からダイアログを出させる嫌がらせを成立させないため）。

- UNC・ネットワークドライブのパス
- **シンボリックリンク（ジャンクション）を経由するパス。** 経路のどこか 1 か所でもリンクがあれば拒否する。
  走査側の `FollowDirectoryLinks` を有効にしても**この入口は緩めない**。ブラウザのリンクから叩かれる
  唯一の外部入力で、他の入口とは信頼度が違うため → [[hirake-directory-links]]（2026-08-16 追記）
- `.md` / `.markdown` 以外の拡張子
- 存在しないファイル
- 未知の verb

また **URI 経由で既存のタブが閉じられることはない**。`workspace` が新しいウィンドウで開く設計のため、破壊的な経路が構造的に存在しない。

## リンクの作り方

手で URI を組み立てる必要はない。**タブを右クリック →「この位置へのリンクをコピー」**で、現在のスクロール位置が `line` に入ったリンクが得られる。
