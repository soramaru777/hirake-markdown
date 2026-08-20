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

.EXAMPLE
    .\scripts\tag-release.ps1 -Prefix tagtest-v
    tagtest-v1.1.0 を作成して push する。作成と push の経路を最後まで通すための
    確認用。v* 以外はルールセット protect-release-tags の対象外なので、
    確認後に消せる: git push origin :refs/tags/tagtest-v1.1.0; git tag -d ...
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # 先頭の - を許すと git の引数として解釈されうるので、英数字で始めさせる。
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._/-]*$')]
    [string]$Branch = 'main',

    # 本番のタグ名は csproj から生成する（v + 版）。ここを変えられるようにしてある
    # のは、消せない v* を使わずに作成〜push の経路を確認できるようにするため。
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$Prefix = 'v'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 取り消せない操作を扱うので、失敗は例外のスタックではなく読める 1 文で出す。
trap {
    Write-Host ''
    Write-Host ('中止しました: ' + $_.Exception.Message)
    exit 1
}

function Invoke-Git {
    <#
        git を呼び、終了コードと出力を返す。成否の判定はしない。

        Windows PowerShell 5.1 は $ErrorActionPreference = 'Stop' のとき、
        ネイティブコマンドが stderr へ 1 行書いただけで NativeCommandError を
        投げる。終了コードは見ない。git は成功時にも警告を stderr に出すため
        （例: warning: git-credential-manager-core was renamed to ...）、
        Stop のままでは成功した push が失敗として扱われる（ISSUE #80）。

        そこで git を呼ぶ間だけ Continue に戻し、終了コードだけで判断する。
    #>
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    # StrictMode 下では未代入の変数を読むだけで落ちる。git の起動自体に失敗した
    # 場合でも、下の return が「出力なし」として通るようにしておく。
    $output = $null

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $script:gitExe @Arguments 2>&1
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = $output
    }
}

function Invoke-GitOrThrow {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$What
    )

    $result = Invoke-Git -Arguments $Arguments
    if ($result.ExitCode -ne 0) {
        throw ("{0}に失敗しました（git の終了コード {1}）。`n{2}" -f $What, $result.ExitCode, ($result.Output -join "`n"))
    }

    # 2>&1 で混ざった stderr は ErrorRecord として来る。警告の行を出力の行と
    # 取り違えると、rev-parse の結果が警告になったり、タグの重複を誤検出したり
    # する。成功したときは標準出力の行だけを返す。
    return @($result.Output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
}

# git が無いと & git の呼び出し自体が失敗し、$LASTEXITCODE には前回の値が
# 残る。成功と誤判定しないよう、始めに 1 度だけ確かめる。
#
# 解決した実行ファイルのパスを覚えて、以後はそれを呼ぶ。`git` という名前のまま
# 呼ぶと、同名の関数や alias が先に当たりうる（確かめたものと実行するものが
# ずれる）。
$gitExe = (Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $gitExe) {
    throw 'git が見つかりません。git をインストールして PATH に通してください。'
}
$gitExe = $gitExe.Source

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

$tag = "$Prefix$version"

# v* だけがルールセット protect-release-tags に守られる。それ以外は確認用の
# タグであり、消せる。取り違えないよう、どちらなのかを先に明示する。
$isRelease = ($Prefix -ceq 'v')

# `vtest` のような接頭辞は、本番ではないのに `v*` に当たる。ルールセットの
# 対象なので消せないのに「確認用なので消せます」と案内することになる。
# 確認用の接頭辞は v で始めさせない。
if (-not $isRelease -and $Prefix -match '^[vV]') {
    throw "確認用の接頭辞は v で始められません（v* はルールセット protect-release-tags の対象で、消せなくなります）。指定された値: $Prefix"
}

# ls-remote の行（"<sha><TAB>refs/tags/<名前>"）が、このタグそのものを指している
# か。前方一致で見ると v9.9.9 が v9.9.90 に反応するため、末尾まで見る。
# ^{} が付く行は同じタグの参照先なので同一視する。
$tagRefPattern = '^\S+\s+refs/tags/' + [regex]::Escape($tag) + '(\^\{\})?$'

if (-not $isRelease) {
    Write-Host ''
    Write-Host "確認用のタグとして $tag を扱います（v* ではないためリリースにはなりません）。"
}

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
    if ($isRelease) {
        throw "ローカルに $tag が既にあります。csproj の <Version> を上げてください（既存のタグは打ち直せません）。"
    }
    throw "ローカルに $tag が既にあります。消してから実行してください: git tag --delete $tag"
}

$remoteTag = Invoke-GitOrThrow @('ls-remote', '--tags', 'origin', "refs/tags/$tag") 'リモートタグの確認'
if (@($remoteTag | Where-Object { $_ -match $tagRefPattern }).Count -gt 0) {
    if ($isRelease) {
        throw "リモートに $tag が既にあります。csproj の <Version> を上げてください（ルールセットにより打ち直しも削除もできません）。"
    }
    throw "リモートに $tag が既にあります。消してから実行してください: git push origin :refs/tags/$tag"
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
    #
    # ただし戻してよいのは「リモートに無い」と確かめられたときだけ。v1.1.0 では
    # push に成功していたのにここが走り、リモートにあるタグがローカルから消えた
    # （ISSUE #80）。確かめられなければ、残す方を選ぶ。
    $probe = Invoke-Git -Arguments @('ls-remote', '--tags', 'origin', "refs/tags/$tag")
    $onRemote = @($probe.Output |
        Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } |
        Where-Object { $_ -match $tagRefPattern }).Count -gt 0

    if ($probe.ExitCode -ne 0) {
        Write-Warning "リモートを確認できなかったため、ローカルの $tag は残します。git ls-remote --tags origin refs/tags/$tag で確かめてください。"
    }
    elseif ($onRemote) {
        Write-Warning "リモートには $tag があります。push 自体は届いていたため、ローカルの $tag は残します。"
    }
    else {
        $deleted = Invoke-Git -Arguments @('tag', '--delete', $tag)
        if ($deleted.ExitCode -ne 0) {
            # 消せないまま黙って進むと、次回の実行が「ローカルに既にあります」で
            # 止まり、原因が分からなくなる。何をすればよいかを出す。
            Write-Warning "ローカルの $tag を戻せませんでした。手で消してください: git tag --delete $tag"
        }
    }
    throw
}

if ($isRelease) {
    Write-Host "$tag を push しました。release.yml が下書きの Release を作ります。"
    Write-Host 'https://github.com/soramaru777/hirake-markdown/actions'
}
else {
    Write-Host "$tag を push しました（v* ではないので release.yml は動きません）。"
    Write-Host "後始末: git push origin :refs/tags/$tag  および  git tag --delete $tag"
}
