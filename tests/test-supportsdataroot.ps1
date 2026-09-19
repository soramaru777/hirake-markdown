#requires -Version 5.1
<#
.SYNOPSIS
    起動前ゲート（Test-HirakeSupportsDataRoot）の回帰テスト（ISSUE #100）。

.DESCRIPTION
    検証ハーネスは「HIRAKE_DATA_ROOT を知らない exe を起動しない」ことで実利用の
    データ領域を守っている（→ #94）。以前はその判定に**サイズの推定**が混じっていた
    （マーカーの無い exe が 4MB 以下なら起動役 apphost と見なして隣の Hirake.dll を
    見る）。単一ファイル発行で 4MB 以下の exe が作れる以上、これは言い切れない。

    いまは束ねヘッダの位置（BundleSignature の直前 8 バイト・int64）で発行形態を
    確定させる。0 なら apphost、非 0 なら単一ファイル。**サイズは一切見ない。**

    ここでは実際の publish 出力から apphost を 1 つ用意し、そのコピーを加工して
    6 パターンを作る。28MB になる単一ファイル発行はテストでは作らない
    （ヘッダ位置を非 0 に書き換えれば、判定にとっては単一ファイルと同じため）。

    .NET SDK が要る（CI では既に setup-dotnet 済み）。-ExePath を渡せば発行を省ける。

.PARAMETER ExePath
    判定に使う既存の Hirake.exe。省略時は %TEMP% へ発行する。

.EXAMPLE
    pwsh -NoProfile -File tests\test-supportsdataroot.ps1
#>

[CmdletBinding()]
param([string]$ExePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\HirakeHost.ps1')

$script:failed = 0
$script:passed = 0

function Assert-Gate {
    <#
    .SYNOPSIS
        1 つの exe について判定と期待値を比べる。理由も併せて印字する。
    #>
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$Expected
    )

    $reason = ''
    $actual = Test-HirakeSupportsDataRoot -ExePath $Path -Reason ([ref]$reason)
    $detail = if ($reason) { "  理由: $reason" } else { '' }

    if ($actual -eq $Expected) {
        $script:passed++
        Write-Host ("  OK   {0}{1}" -f $Label, $detail)
    } else {
        $script:failed++
        Write-Host ("  FAIL {0}  actual={1} expected={2}{3}" -f $Label, $actual, $Expected, $detail)
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)][string]$Label,
        [AllowNull()]$Actual,
        [AllowNull()]$Expected
    )

    $a = if ($null -eq $Actual) { '<null>' } else { "[$Actual]" }
    $e = if ($null -eq $Expected) { '<null>' } else { "[$Expected]" }

    if ($a -eq $e) {
        $script:passed++
        Write-Host ("  OK   {0}" -f $Label)
    } else {
        $script:failed++
        Write-Host ("  FAIL {0}  actual={1} expected={2}" -f $Label, $a, $e)
    }
}

function Set-BundleHeaderOffset {
    <#
    .SYNOPSIS
        exe のコピーに「束ねヘッダの位置」を書き込み、単一ファイル発行に見せかける。
    .DESCRIPTION
        本物を 28MB 発行する代わりの合成。判定が見るのは signature の直前 8 バイト
        だけなので、そこを書き換えれば単一ファイルと同じ扱いになる。
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$Value
    )

    $signatureOffset = Find-BinaryBytesOffset -Path $Path -Bytes $script:DotnetBundleSignature
    if ($signatureOffset -lt 8) { throw "signature が見つかりません: $Path" }

    $bytes = [System.BitConverter]::GetBytes($Value)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $stream.Seek(($signatureOffset - 8), [System.IO.SeekOrigin]::Begin) | Out-Null
        $stream.Write($bytes, 0, 8)
    } finally {
        $stream.Dispose()
    }
}

function Test-ScanOffset {
    <#
    .SYNOPSIS
        走査が「一致した位置」を正しく返すかを、合成したファイルで確かめる。
    .DESCRIPTION
        1MB ずつ読んで末尾を持ち越す作りなので、**境界をまたぐ一致**と
        **絶対オフセットの持ち回り**がこの実装の要。ここが 1 バイトずれると
        bundle header（signature の直前 8 バイト）を読み違える。
    #>
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][int]$At,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$Size,
        [Parameter(Mandatory)][byte[]]$Needle,
        [Parameter(Mandatory)][string]$Marker
    )

    $buffer = New-Object byte[] $Size
    [Array]::Copy($Needle, 0, $buffer, $At, $Needle.Length)
    [System.IO.File]::WriteAllBytes($Path, $buffer)
    Assert-Equal $Label (Find-BinaryMarkerOffset -Path $Path -Marker $Marker) $At
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("hirake-gate-" + [guid]::NewGuid().ToString('N'))
$publishDir = Join-Path $workRoot 'publish'

Write-Host ''
Write-Host '===================================================================='
Write-Host ("  work    : {0}" -f $workRoot)

try {
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

    # --- 走査（位置を返すこと）--------------------------------------------
    # 3MB の合成ファイルで、1MB チャンクの境界・先頭・末尾を突く。
    $marker = 'HIRAKE_DATA_ROOT'
    $needle = [System.Text.Encoding]::GetEncoding(28591).GetBytes($marker)
    $scanFile = Join-Path $workRoot 'scan.bin'
    $scanSize = 3145728

    Test-ScanOffset 'ファイル先頭の一致' 0 $scanFile $scanSize $needle $marker
    Test-ScanOffset '1MB 境界をまたぐ一致' (1048576 - 5) $scanFile $scanSize $needle $marker
    Test-ScanOffset '2 チャンク目の先頭' 1048576 $scanFile $scanSize $needle $marker
    Test-ScanOffset '2MB 境界をまたぐ一致' (2097152 - 1) $scanFile $scanSize $needle $marker
    Test-ScanOffset 'ファイル末尾の一致' ($scanSize - $needle.Length) $scanFile $scanSize $needle $marker

    # 2 箇所にあるときは手前を返す（前の窓の結果を持ち越していないこと）
    $buffer = New-Object byte[] $scanSize
    [Array]::Copy($needle, 0, $buffer, 1500000, $needle.Length)
    [Array]::Copy($needle, 0, $buffer, 500000, $needle.Length)
    [System.IO.File]::WriteAllBytes($scanFile, $buffer)
    Assert-Equal '2 箇所にあるときは手前を返す' (Find-BinaryMarkerOffset -Path $scanFile -Marker $marker) 500000

    # UTF-16LE 側でも当たる（.NET の文字列リテラルはこちら）
    $utf16 = [System.Text.Encoding]::Unicode.GetBytes($marker)
    $buffer = New-Object byte[] $scanSize
    [Array]::Copy($utf16, 0, $buffer, 1048570, $utf16.Length)
    [System.IO.File]::WriteAllBytes($scanFile, $buffer)
    Assert-Equal 'UTF-16LE の一致も位置で返る' (Find-BinaryMarkerOffset -Path $scanFile -Marker $marker) 1048570

    [System.IO.File]::WriteAllBytes($scanFile, (New-Object byte[] 100))
    Assert-Equal '不在なら -1' (Find-BinaryMarkerOffset -Path $scanFile -Marker $marker) -1
    [System.IO.File]::WriteAllBytes($scanFile, (New-Object byte[] 0))
    Assert-Equal '空ファイルでも -1' (Find-BinaryMarkerOffset -Path $scanFile -Marker $marker) -1
    Remove-Item -LiteralPath $scanFile -Force
    Write-Host ''

    # --- 起動前ゲート -----------------------------------------------------
    if ($ExePath) {
        if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
            Write-Error "指定された exe が見つかりません: $ExePath"
            exit 1
        }
        $realExe = (Resolve-Path -LiteralPath $ExePath).Path
        $realDll = Join-Path (Split-Path -Parent $realExe) 'Hirake.dll'
        Write-Host ("  exe     : {0}（指定）" -f $realExe)
    } else {
        $project = Join-Path $PSScriptRoot '..\src\Hirake'
        Write-Host '  発行しています…（framework-dependent）'
        $publish = & dotnet publish $project -c Release -r win-x64 --self-contained false `
            -o $publishDir --nologo -v quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            $publish | ForEach-Object { Write-Host "    $_" }
            throw "発行に失敗しました。終了コード: $LASTEXITCODE"
        }
        $realExe = Join-Path $publishDir 'Hirake.exe'
        $realDll = Join-Path $publishDir 'Hirake.dll'
        Write-Host ("  exe     : {0}" -f $realExe)
    }

    if (-not (Test-Path -LiteralPath $realDll -PathType Leaf)) {
        throw "Hirake.dll が見つかりません: $realDll"
    }

    $exeSize = (Get-Item -LiteralPath $realExe).Length
    Write-Host ("  size    : {0:N0} バイト" -f $exeSize)
    Write-Host ''

    # 判定の土台。ここが崩れると以下のすべてが意味を失うので、値そのものを固定する。
    Assert-Equal '実際の apphost の束ねヘッダ位置は 0' (Get-DotnetBundleHeaderOffset -Path $realExe) 0
    Assert-Equal 'Hirake.dll には signature が無い（apphost ではない）' `
        (Get-DotnetBundleHeaderOffset -Path $realDll) $null

    # 1) 通常の発行構成（apphost + 隣の dll）
    Assert-Gate '通常の発行（apphost + Hirake.dll）は通る' $realExe $true

    # 2) apphost だけ（隣に中身が無い）
    $aloneDir = Join-Path $workRoot 'alone'
    New-Item -ItemType Directory -Path $aloneDir -Force | Out-Null
    $aloneExe = Join-Path $aloneDir 'Hirake.exe'
    Copy-Item -LiteralPath $realExe -Destination $aloneExe
    Assert-Gate '隣に Hirake.dll が無ければ通さない' $aloneExe $false

    # 3) **本題**: マーカーの無い単一ファイル相当（ヘッダ位置が非 0）＋ 隣に本物の dll。
    #    旧実装はサイズ（190KB ≦ 4MB）で apphost と見なし、隣の dll を見て通していた。
    $fakeDir = Join-Path $workRoot 'fake-singlefile'
    New-Item -ItemType Directory -Path $fakeDir -Force | Out-Null
    $fakeExe = Join-Path $fakeDir 'Hirake.exe'
    Copy-Item -LiteralPath $realExe -Destination $fakeExe
    Copy-Item -LiteralPath $realDll -Destination (Join-Path $fakeDir 'Hirake.dll')
    Set-BundleHeaderOffset -Path $fakeExe -Value ($exeSize - 1)
    Assert-Equal '合成した単一ファイル相当は 4MB 以下（旧実装なら通っていた大きさ）' `
        ((Get-Item -LiteralPath $fakeExe).Length -le 4MB) $true
    Assert-Gate 'マーカーの無い単一ファイルは、隣に dll があっても通さない' $fakeExe $false

    # 4) 中身を持つ単一ファイル相当（exe 自身がマーカーを持つ）
    $selfDir = Join-Path $workRoot 'self-contained-ish'
    New-Item -ItemType Directory -Path $selfDir -Force | Out-Null
    $selfExe = Join-Path $selfDir 'Hirake.exe'
    $merged = [System.IO.File]::ReadAllBytes($realExe) + [System.IO.File]::ReadAllBytes($realDll)
    [System.IO.File]::WriteAllBytes($selfExe, $merged)
    Set-BundleHeaderOffset -Path $selfExe -Value $exeSize
    Assert-Gate '単一ファイルでも中に HIRAKE_DATA_ROOT があれば通る' $selfExe $true

    # 5) .NET のホストでない exe
    $notepad = Join-Path $env:WINDIR 'System32\notepad.exe'
    if (Test-Path -LiteralPath $notepad -PathType Leaf) {
        Assert-Gate '.NET ホストでない exe は通さない' $notepad $false
    } else {
        Write-Host '  SKIP notepad.exe が見つからないため省略'
    }

    # 6) ヘッダ位置が壊れている（ファイルサイズを超える）
    $brokenDir = Join-Path $workRoot 'broken'
    New-Item -ItemType Directory -Path $brokenDir -Force | Out-Null
    $brokenExe = Join-Path $brokenDir 'Hirake.exe'
    Copy-Item -LiteralPath $realExe -Destination $brokenExe
    Copy-Item -LiteralPath $realDll -Destination (Join-Path $brokenDir 'Hirake.dll')
    Set-BundleHeaderOffset -Path $brokenExe -Value ($exeSize + 1024)
    Assert-Gate 'ヘッダ位置がファイルサイズを超えていれば通さない' $brokenExe $false
} finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host '===================================================================='
if ($script:failed -eq 0) {
    Write-Host "すべて成功しました（$($script:passed) 件）。"
    Write-Host '===================================================================='
    exit 0
}

Write-Host "失敗 $($script:failed) 件 / 成功 $($script:passed) 件。"
Write-Host '===================================================================='
exit 1
