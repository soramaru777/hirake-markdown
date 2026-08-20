#requires -Version 5.1
<#
.SYNOPSIS
    tag-release.ps1 を、作成から push まで実際に通して確かめる。

.DESCRIPTION
    `v*` タグは打ち直せず消せないため（ISSUE #57）、本番のリモートでは
    リリース経路を試せない。#76 の確認が 4 パターンで止まり、**実際に push する
    経路だけ通っていなかった**結果が ISSUE #80 である。

    ここでは一時フォルダに bare リポジトリを作って origin とし、tag-release.ps1 を
    最後まで走らせる。GitHub には触れない。

    git は PATH の先頭に置く shim 越しに呼ぶ。shim は push のときだけ stderr へ
    警告を書く。**Windows PowerShell 5.1 が「stderr に書いた ＝ 失敗」と扱う**
    条件を再現するため（#80 の原因）。実際の git も成功時に `To <remote>` を
    stderr へ出すので、これは作り話ではなく通常の状態である。

    確かめるのは 8 通り。

      ok        push 成功            → 成功と表示し、ローカルにもタグが残る
      pushfail  push 失敗            → ローカルタグを戻す（リモートにも無い）
      pushflaky push は届いたが失敗  → ローカルタグを残す（消してはいけない）
      prefix    v 以外の接頭辞       → 作成〜push を最後まで通せる
      whatif    -WhatIf              → どこにもタグを作らない
      dup       同名タグが既にある   → 作らずに中止する
      nearmiss  似た名前のタグがある → v9.9.90 は v9.9.9 を邪魔しない
      badprefix v で始まる接頭辞    → 消せない v* になるので受け付けない

.EXAMPLE
    .\scripts\test-tag-release.ps1
    すべての場合を試し、結果の一覧を出す。1 つでも失敗すると終了コード 1。

.EXAMPLE
    .\scripts\test-tag-release.ps1 -Case ok
    1 つだけ試す。出力もそのまま見せる。
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'ok', 'pushfail', 'pushflaky', 'prefix', 'whatif', 'dup', 'nearmiss', 'badprefix')]
    [string]$Case = 'all',

    # 確かめたい tag-release.ps1。既定はこのリポジトリのもの（下で補う）。
    [string]$Script
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 既定値を param に書かない。Windows PowerShell 5.1 では `powershell.exe -File`
# で起動したとき、param の既定値を評価する時点で $PSScriptRoot が空になり、
# 本体が動く前に落ちる。
if ([string]::IsNullOrWhiteSpace($Script)) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Script = Join-Path $here 'tag-release.ps1'
}
if (-not (Test-Path -LiteralPath $Script -PathType Leaf)) {
    throw "$Script が見つかりません。"
}

$version = '9.9.9'
$realGit = (Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $realGit) { throw 'git が見つかりません。' }
$realGit = $realGit.Source

# 5.1 の挙動を確かめるものなので、対象は必ず Windows PowerShell で走らせる。
$targetShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $targetShell)) { throw "$targetShell が見つかりません。" }

function Invoke-FixtureGit {
    <#
        足場を組むための git。ここが黙って失敗すると、肝心の経路を一度も通らない
        まま「成功」と出る（偽陰性）。失敗したらその場で止める。

        テスト自身も #80 の罠を踏まないよう、判定は終了コードだけで行う。
    #>
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = $null
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $realGit @Arguments 2>&1
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($LASTEXITCODE -ne 0) {
        throw ("足場の git {0} が失敗しました（終了コード {1}）。`n{2}" -f ($Arguments -join ' '), $LASTEXITCODE, ($output -join "`n"))
    }
    return @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
}

function New-Sandbox {
    param([string]$Name)

    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) "tag-release-test-$Name"
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    New-Item -ItemType Directory -Path $sandbox | Out-Null

    # push のときだけ stderr へ警告を出す git。$FailPush が真なら、git を通した
    # うえで終了コードだけ 1 にする（＝リモートには届いたのに失敗した場合）。
    $shimDir = Join-Path $sandbox 'shim'
    New-Item -ItemType Directory -Path $shimDir | Out-Null

    $before = if ($Name -eq 'pushfail') { 'if "%1"=="push" (echo fatal: unable to access remote 1>&2 & exit /b 128)' } else { 'rem そのまま通す' }
    $after = if ($Name -eq 'pushflaky') { 'if "%1"=="push" set rc=1' } else { 'rem 終了コードはそのまま' }

    # どの git サブコマンドが呼ばれたかを残す。push へ到達しないまま終わった実行を
    # 「rollback が効いた」と読み違えないため。
    $callLog = Join-Path $sandbox 'git-calls.log'

    $shim = @"
@echo off
echo %1>>"$callLog"
$before
"$realGit" %*
set rc=%errorlevel%
if "%1"=="push" echo warning: git-credential-manager-core was renamed to git-credential-manager 1>&2
$after
exit /b %rc%
"@
    Set-Content -LiteralPath (Join-Path $shimDir 'git.bat') -Value $shim -Encoding ASCII

    $origin = Join-Path $sandbox 'origin.git'
    $work = Join-Path $sandbox 'work'
    Invoke-FixtureGit @('init', '--bare', '--quiet', $origin) | Out-Null
    Invoke-FixtureGit @('init', '--quiet', '-b', 'main', $work) | Out-Null

    New-Item -ItemType Directory -Path (Join-Path $work 'src\Hirake') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $work 'scripts') -Force | Out-Null
    "<Project><PropertyGroup><Version>$version</Version></PropertyGroup></Project>" |
        Set-Content -LiteralPath (Join-Path $work 'src\Hirake\Hirake.csproj') -Encoding UTF8
    Copy-Item -LiteralPath $Script -Destination (Join-Path $work 'scripts\tag-release.ps1')

    Push-Location $work
    try {
        Invoke-FixtureGit @('config', 'user.email', 'test@example.invalid') | Out-Null
        Invoke-FixtureGit @('config', 'user.name', 'tag-release test') | Out-Null
        Invoke-FixtureGit @('add', '-A') | Out-Null
        Invoke-FixtureGit @('commit', '--quiet', '-m', 'test fixture') | Out-Null
        Invoke-FixtureGit @('remote', 'add', 'origin', $origin) | Out-Null
        Invoke-FixtureGit @('push', '--quiet', '-u', 'origin', 'main') | Out-Null

        # 足場が本当に組めたか（push が届いたか）をここで確かめる。ここを見ないと、
        # 一度も push せずに「rollback が正しく効いた」と誤って報告しうる。
        $mainOnRemote = Invoke-FixtureGit @('ls-remote', '--heads', $origin, 'refs/heads/main')
        if (@($mainOnRemote | Where-Object { $_ }).Count -eq 0) {
            throw '足場の main が origin に届いていません。テストになりません。'
        }
    }
    finally {
        Pop-Location
    }

    return [pscustomobject]@{ Root = $sandbox; Origin = $origin; Work = $work; ShimDir = $shimDir; CallLog = $callLog }
}

function Get-Tags {
    param([string]$Repo, [string]$Origin)

    Push-Location $Repo
    try {
        $local = @(Invoke-FixtureGit @('tag', '-l') | Where-Object { $_ })
        # 出力は "<sha><TAB>refs/tags/<名前>"。名前だけ取り出す。
        $remote = @(Invoke-FixtureGit @('ls-remote', '--tags', $Origin) |
            Where-Object { $_ } |
            ForEach-Object { ($_ -split "`t")[-1] -replace '^refs/tags/', '' })
        return [pscustomobject]@{ Local = $local; Remote = $remote }
    }
    finally {
        Pop-Location
    }
}

function Invoke-Case {
    param([string]$Name, [switch]$Show)

    $env:PATH = $script:originalPath
    $box = New-Sandbox -Name $Name
    $tag = "v$version"

    if ($Name -eq 'dup' -or $Name -eq 'nearmiss') {
        # リモートにだけ同名／似た名前のタグがある状態を作る。
        # nearmiss は v9.9.90。前方一致で見ていると v9.9.9 と取り違える名前。
        $existing = if ($Name -eq 'dup') { "v$version" } else { "v${version}0" }
        Push-Location $box.Work
        try {
            Invoke-FixtureGit @('tag', $existing) | Out-Null
            Invoke-FixtureGit @('push', '--quiet', 'origin', "refs/tags/$existing") | Out-Null
            Invoke-FixtureGit @('tag', '--delete', $existing) | Out-Null

            $onRemote = Invoke-FixtureGit @('ls-remote', '--tags', $box.Origin, "refs/tags/$existing")
            if (@($onRemote | Where-Object { $_ }).Count -eq 0) {
                throw "足場のタグ $existing が origin に届いていません。テストになりません。"
            }
        }
        finally {
            Pop-Location
        }
    }

    $scriptArgs = @('-Branch', 'main')
    if ($Name -eq 'prefix') {
        $tag = "tagtest-v$version"
        $scriptArgs += @('-Prefix', 'tagtest-v')
    }
    if ($Name -eq 'badprefix') {
        # vtest9.9.9 は v* に当たる ＝ ルールセットの対象で消せない。確認用として
        # 案内してはいけない組み合わせ。
        $tag = "vtest$version"
        $scriptArgs += @('-Prefix', 'vtest')
    }
    if ($Name -eq 'whatif') { $scriptArgs += '-WhatIf' }

    $env:PATH = $box.ShimDir + ';' + $script:originalPath
    Push-Location $box.Work

    # 対象が stderr へ書くのは日常（中止の理由も、git の警告もそこへ出る）。
    # Stop のままだと、このテスト自身が #80 と同じ罠を踏んで途中で落ちる。
    # ここでも判断は終了コードだけで行う。
    #
    # 出力の文字コードも固定する。既定のコードページのままだと、子プロセスが書いた
    # 日本語を取り違えて読み、「理由」の照合が環境によって落ちる。
    $previousEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $targetShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $box.Work 'scripts\tag-release.ps1') @scriptArgs 2>&1
        $exit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
        [Console]::OutputEncoding = $previousEncoding
        Pop-Location
        $env:PATH = $script:originalPath
    }

    $tags = Get-Tags -Repo $box.Work -Origin $box.Origin

    # shim を通ったのは対象スクリプトの git だけ（足場は実体を直接呼んでいる）。
    $pushCalled = $false
    if (Test-Path -LiteralPath $box.CallLog) {
        $pushCalled = @(Get-Content -LiteralPath $box.CallLog | Where-Object { $_.Trim() -eq 'push' }).Count -gt 0
    }
    $text = ($output | Out-String)

    if ($Show) {
        Write-Host ($output | Out-String)
        Write-Host "終了コード: $exit"
        Write-Host ("ローカル  : " + ($tags.Local -join ', '))
        Write-Host ("リモート  : " + ($tags.Remote -join ', '))
    }

    # 期待する結果: 終了コード / ローカルにタグが残るか / リモートにタグがあるか。
    # $null は「どちらでもよい」。
    #
    # dup のローカルは見ない。中止するのが要点であって、タグがローカルに在るか
    # 無いかは `fetch --tags` が同名タグを降ろすかどうかで決まる（＝ここで縛ると、
    # 正しい実装が取り込み方の違いだけで落ちる）。
    # Message は「その理由で終わったか」。終了コードとタグ状態だけ見ていると、
    # 版の取得やブランチ確認など push より手前で落ちた実行も条件を満たしてしまう。
    # Push は「push の経路まで実際に到達したか」。
    $expected = switch ($Name) {
        'ok'        { @{ Exit = 0; Local = $true;  Remote = $true;  Message = 'push しました';                 Push = $true } }
        'pushfail'  { @{ Exit = 1; Local = $false; Remote = $false; Message = 'push.*失敗しました.*128';        Push = $true } }
        'pushflaky' { @{ Exit = 1; Local = $true;  Remote = $true;  Message = 'リモートには.*残します';          Push = $true } }
        'prefix'    { @{ Exit = 0; Local = $true;  Remote = $true;  Message = '確認用のタグとして';              Push = $true } }
        'whatif'    { @{ Exit = 0; Local = $false; Remote = $false; Message = 'WhatIf';                        Push = $false } }
        'dup'       { @{ Exit = 1; Local = $null;  Remote = $true;  Message = '既にあります';                   Push = $false } }
        'nearmiss'  { @{ Exit = 0; Local = $true;  Remote = $true;  Message = 'push しました';                  Push = $true } }
        'badprefix' { @{ Exit = 1; Local = $false; Remote = $false; Message = '接頭辞は v で始められません';     Push = $false } }
    }

    $problems = @()
    if ($exit -ne $expected.Exit) { $problems += "終了コードが $exit（期待 $($expected.Exit)）" }
    if ($null -ne $expected.Local -and ($tags.Local -contains $tag) -ne $expected.Local) {
        $problems += "ローカルの $tag が期待と違う（期待 $($expected.Local)）"
    }
    if ($null -ne $expected.Remote -and ($tags.Remote -contains $tag) -ne $expected.Remote) {
        $problems += "リモートの $tag が期待と違う（期待 $($expected.Remote)）"
    }
    if ($text -notmatch $expected.Message) {
        $problems += "出力に『$($expected.Message)』が無い（別の理由で終わった可能性）"
    }
    if ($pushCalled -ne $expected.Push) {
        $problems += "push への到達が期待と違う（期待 $($expected.Push) / 実際 $pushCalled）"
    }

    if ($problems.Count -eq 0) {
        Remove-Item -LiteralPath $box.Root -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host "  調べる場合はここを見る: $($box.Root)"
    }

    return [pscustomobject]@{ Case = $Name; Ok = ($problems.Count -eq 0); Problems = $problems; Output = ($output | Out-String) }
}

$script:originalPath = $env:PATH
$cases = if ($Case -eq 'all') { @('ok', 'pushfail', 'pushflaky', 'prefix', 'whatif', 'dup', 'nearmiss', 'badprefix') } else { @($Case) }

$results = foreach ($name in $cases) {
    Write-Host "実行中: $name"
    Invoke-Case -Name $name -Show:($Case -ne 'all')
}

Write-Host ''
foreach ($r in $results) {
    if ($r.Ok) {
        Write-Host "  OK   $($r.Case)"
    }
    else {
        Write-Host "  NG   $($r.Case): $($r.Problems -join ' / ')"
        Write-Host ($r.Output -split "`n" | Select-Object -Last 6 | Out-String)
    }
}

$failed = @($results | Where-Object { -not $_.Ok })
Write-Host ''
Write-Host ("{0} 件中 {1} 件成功。" -f @($results).Count, (@($results).Count - $failed.Count))
if ($failed.Count -gt 0) { exit 1 }
