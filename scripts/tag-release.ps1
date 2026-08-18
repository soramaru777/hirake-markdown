#requires -Version 5.1
<#
.SYNOPSIS
    csproj の <Version> からリリースタグを作成して push する。

.DESCRIPTION
    版の出所は src\Hirake\Hirake.csproj の <Version> だけ、という約束を
    タグ付けにも適用する。手で書き写すと打ち間違いが起こりうるが、v* タグは
    ルールセット protect-release-tags が update と deletion を拒否するため
    消せない（ISSUE #57）。1 回のタイプミスが恒久的に残る。

    push する前に次を確認し、1 つでも満たさなければタグを作らずに中止する。

      1. 版が数値 3〜4 要素であること（release.yml の verify-tag と同じ規則）
      2. いま居るコミットがリリース対象ブランチの先端であること
      3. 同名のタグがローカルにもリモートにも無いこと

    3 は「版を上げ忘れた」ことの検出でもある。タグ名を csproj から生成する以上、
    verify-tag の「タグ名と版が一致すること」は必ず成功するようになり、
    上げ忘れは不一致ではなく重複として現れる。

.PARAMETER Branch
    リリース対象のブランチ。既定は main。

    実際の v1.0.0 は main のマージコミットに打たれている。verify-tag は
    「main または develop に含まれること」しか見ないため develop でも通るが、
    Releases と main を一致させる運用に合わせて main を既定にする。

.EXAMPLE
    .\scripts\tag-release.ps1 -WhatIf
    何をするかだけ表示する。タグは作らない。

.EXAMPLE
    .\scripts\tag-release.ps1
    タグを作成して push する。push すると release.yml が起動する。

.EXAMPLE
    .\scripts\tag-release.ps1 -Branch develop
    develop の先端に打つ。
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^[A-Za-z0-9._/-]+$')]
    [string]$Branch = 'main'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 取り消せない操作を扱うので、失敗は例外のスタックではなく読める 1 文で出す。
trap {
    Write-Host ''
    Write-Host ('中止しました: ' + $_.Exception.Message)
    exit 1
}

function Invoke-GitOrThrow {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$What
    )

    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ("{0}に失敗しました（git の終了コード {1}）。`n{2}" -f $What, $LASTEXITCODE, ($output -join "`n"))
    }
    return $output
}

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\Hirake\Hirake.csproj'

if (-not (Test-Path -LiteralPath $csproj -PathType Leaf)) {
    throw "$csproj が見つかりません。リポジトリの中で実行してください。"
}

# ---- 1. 版を読む -------------------------------------------------------

[xml]$proj = Get-Content -LiteralPath $csproj
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "$csproj に <Version> がありません。"
}

# Inno Setup の VersionInfoVersion は数値のみ。プレリリース表記を通すと
# ビルドの終盤まで進んでから落ちるため、ここで弾く（verify-tag と同じ規則）。
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "版は数値 3〜4 要素で指定してください（例 1.0.0）。csproj の値: $version"
}

$tag = "v$version"

# ---- 2. リリース対象ブランチの先端に居ることを確認する -----------------

Invoke-GitOrThrow @('fetch', '--quiet', '--tags', 'origin', $Branch) "origin/$Branch の取得" | Out-Null

$head = (Invoke-GitOrThrow @('rev-parse', 'HEAD') '現在のコミットの取得') | Select-Object -First 1
$tip  = (Invoke-GitOrThrow @('rev-parse', "origin/$Branch") "origin/$Branch の取得") | Select-Object -First 1

if ($head -ne $tip) {
    throw @"
いま居るコミットが origin/$Branch の先端ではありません。
  HEAD           : $head
  リリース対象   : $tip (origin/$Branch)
リリース対象を取り込んでから実行してください: git switch $Branch; git pull
"@
}

$status = Invoke-GitOrThrow @('status', '--porcelain') '作業ツリーの確認'
if (@($status).Count -gt 0) {
    Write-Warning '作業ツリーに未コミットの変更があります。タグはコミット済みの内容だけを指します。'
}

# ---- 3. 同名タグが無いことを確認する -----------------------------------

$localTag = Invoke-GitOrThrow @('tag', '--list', $tag) 'ローカルタグの確認'
if (@($localTag | Where-Object { $_ }).Count -gt 0) {
    throw "ローカルに $tag が既にあります。csproj の <Version> を上げてください（既存のタグは打ち直せません）。"
}

$remoteTag = Invoke-GitOrThrow @('ls-remote', '--tags', 'origin', "refs/tags/$tag") 'リモートタグの確認'
if (@($remoteTag | Where-Object { $_ }).Count -gt 0) {
    throw "リモートに $tag が既にあります。csproj の <Version> を上げてください（ルールセットにより打ち直しも削除もできません）。"
}

# ---- 4. 作成して push する ---------------------------------------------

$subject = (Invoke-GitOrThrow @('log', '-1', '--format=%h %s') 'コミットの取得') | Select-Object -First 1

Write-Host ''
Write-Host "  タグ      : $tag"
Write-Host "  ブランチ  : $Branch"
Write-Host "  コミット  : $subject"
Write-Host ''

if (-not $PSCmdlet.ShouldProcess("origin $tag", 'タグを作成して push する')) {
    Write-Host 'WhatIf: タグは作成していません。'
    return
}

Invoke-GitOrThrow @('tag', $tag) "タグ $tag の作成" | Out-Null

try {
    Invoke-GitOrThrow @('push', 'origin', $tag) "タグ $tag の push" | Out-Null
}
catch {
    # push できなかったローカルタグを残すと、次回の実行が「既にあります」で
    # 止まる。原因を直して再実行できるよう、ここだけは戻す。
    & git tag --delete $tag | Out-Null
    throw
}

Write-Host "$tag を push しました。release.yml が下書きの Release を作ります。"
Write-Host 'https://github.com/soramaru777/hirake-markdown/actions'
