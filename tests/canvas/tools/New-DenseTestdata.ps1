#requires -Version 5.1
<#
.SYNOPSIS
    ズーム下限に当たる規模の testdata（既定 500 ノード）を生成する（ISSUE #94）。

.DESCRIPTION
    #93 の実測では 1366x768 で必要倍率が 0.072 まで下がった。既定のズーム下限
    0.1 を確実に下回る規模を作り、Case-93 がそれを踏む。

    500 ファイルはリポジトリに置かない（履歴の雑音になる）。生成先は
    tests/canvas/testdata/dense500 で、testdata/.gitignore が除外している。

    べき等。既存の生成物は作り直す。

.PARAMETER Count
    生成するファイル数。

.PARAMETER Path
    生成先。省略時は tests/canvas/testdata/dense500。

.EXAMPLE
    pwsh -NoProfile -File tests\canvas\tools\New-DenseTestdata.ps1
#>

[CmdletBinding()]
param(
    [int]$Count = 500,
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $Path = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\testdata')).Path 'dense500'
}

if (Test-Path -LiteralPath $Path) {
    Remove-Item -LiteralPath $Path -Recurse -Force
}
New-Item -ItemType Directory -Path $Path -Force | Out-Null

Write-Host ("dense testdata を生成します: {0}（{1} 件）" -f $Path, $Count)

for ($i = 1; $i -le $Count; $i++) {
    $name = 'dense-{0:0000}.md' -f $i

    # リング + 弦。孤立ノードを作らず、力学モデルが広く展開するようにする。
    $links = @(
        (($i % $Count) + 1),
        ((($i + 16) % $Count) + 1),
        ((($i + 97) % $Count) + 1)
    ) | Sort-Object -Unique

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(('# dense-{0:0000}' -f $i))
    $lines.Add('')
    $lines.Add('ズーム下限（ISSUE #93）の素材。生成物なので履歴には載せない。')
    $lines.Add('')
    $lines.Add('## 参照')
    $lines.Add('')
    foreach ($l in $links) {
        $lines.Add(('- [dense-{0:0000}](dense-{0:0000}.md)' -f $l))
    }
    $lines.Add('')

    # BOM 無し UTF-8。ビューアの読み込みは厳密 UTF-8 → Shift-JIS フォールバック。
    [System.IO.File]::WriteAllLines(
        (Join-Path $Path $name),
        $lines,
        (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host ("生成しました: {0} 件" -f (Get-ChildItem -LiteralPath $Path -Filter '*.md').Count)
