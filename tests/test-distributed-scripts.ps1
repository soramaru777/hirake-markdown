#requires -Version 5.1
<#
.SYNOPSIS
    インストーラが配布する PowerShell スクリプトが、Windows PowerShell 5.1 で
    読めることを確認する。

.DESCRIPTION
    インストーラは Windows 標準の powershell.exe（Windows PowerShell 5.1）から
    register.ps1 / unregister.ps1 を呼ぶ。5.1 は BOM の無い UTF-8 ファイルを
    ロケールのコードページ（日本語環境なら CP932）として読むため、日本語の
    コメントを含むスクリプトは BOM が無いと構文エラーになり、関連付けの登録が
    必ず失敗する（ISSUE #40）。

    tests\test-association-lib.ps1 などは pwsh（PowerShell 7）で実行されるため、
    この問題を検出できない。そのため、このテストだけは 5.1 でも実行できるように
    書き、CI からは powershell.exe で呼ぶ。

    レジストリには一切触れない（構文の確認だけ）。

.EXAMPLE
    powershell.exe -NoProfile -File .\tests\test-distributed-scripts.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$targets = @(
    'scripts\association-lib.ps1'
    'scripts\register.ps1'
    'scripts\unregister.ps1'
)

$root = Split-Path -Parent $PSScriptRoot
$failed = 0
$passed = 0

function Assert-Ok {
    param([string]$Label, [bool]$Ok, [string]$Detail = '')

    if ($Ok) {
        $script:passed++
        Write-Host ("  OK   " + $Label)
    }
    else {
        $script:failed++
        Write-Host ("  FAIL " + $Label + $(if ($Detail) { "  " + $Detail } else { '' }))
    }
}

Write-Host ("PowerShell " + $PSVersionTable.PSVersion)
Write-Host ''
Write-Host '=== UTF-8 BOM があること ==='

foreach ($target in $targets) {
    $path = Join-Path $root $target
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Assert-Ok $target $false 'ファイルが見つかりません'
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    Assert-Ok $target $hasBom 'BOM がありません（5.1 が CP932 として読み、構文エラーになります）'
}

Write-Host ''
Write-Host '=== 構文エラーが無いこと ==='

foreach ($target in $targets) {
    $path = Join-Path $root $target
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        continue
    }

    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors) | Out-Null

    $count = @($errors).Count
    Assert-Ok $target ($count -eq 0) "$count 件"

    if ($count -gt 0) {
        @($errors) | Select-Object -First 3 | ForEach-Object {
            Write-Host ("       " + $_.Message)
        }
    }
}

Write-Host ''
if ($failed -eq 0) {
    Write-Host ("すべて成功しました（" + $passed + " 件）。")
    exit 0
}

Write-Host ("失敗 " + $failed + " 件 / 成功 " + $passed + " 件。")
exit 1
