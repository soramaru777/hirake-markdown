#requires -Version 5.1
<#
.SYNOPSIS
    register.ps1 で作成した Hirake の .md / .markdown 関連付け（HKCU）を削除します。
    管理者権限は不要です。他アプリが設定した既定プログラムには影響しません。
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Send-ShellChangeNotification {
    if (-not ('Hirake.NativeMethods' -as [type])) {
        Add-Type -Namespace Hirake -Name NativeMethods -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(long wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
'@
    }
    $SHCNE_ASSOCCHANGED = 0x08000000
    $SHCNF_IDLIST = 0x0000
    [Hirake.NativeMethods]::SHChangeNotify($SHCNE_ASSOCCHANGED, $SHCNF_IDLIST, [IntPtr]::Zero, [IntPtr]::Zero)
}

$progId = 'Hirake.md'
$progIdKey = "HKCU:\Software\Classes\$progId"

# 1. .md の既定プログラムが Hirake.md の場合のみ削除（他アプリの関連付けを壊さない）
$mdExtKey = 'HKCU:\Software\Classes\.md'
if (Test-Path -LiteralPath $mdExtKey) {
    $existingDefault = $null
    try {
        $existingDefault = (Get-ItemProperty -Path $mdExtKey -Name '(default)' -ErrorAction Stop).'(default)'
    } catch {
        $existingDefault = $null
    }

    if ($existingDefault -eq $progId) {
        Remove-ItemProperty -Path $mdExtKey -Name '(default)' -ErrorAction SilentlyContinue
        Write-Host "'.md' の既定プログラム設定（'$progId'）を削除しました。"
    } elseif ($existingDefault) {
        Write-Host "'.md' の既定プログラムは '$existingDefault' のままです（Hirake が設定したものではないため削除しません）。"
    }
}

# 2. .md / .markdown の OpenWithProgIds から Hirake.md を削除
foreach ($ext in @('.md', '.markdown')) {
    $openWithKey = "HKCU:\Software\Classes\$ext\OpenWithProgIds"
    if (Test-Path -LiteralPath $openWithKey) {
        $prop = Get-ItemProperty -Path $openWithKey -Name $progId -ErrorAction SilentlyContinue
        if ($prop) {
            Remove-ItemProperty -Path $openWithKey -Name $progId -ErrorAction SilentlyContinue
            Write-Host "拡張子 '$ext' の OpenWithProgIds から '$progId' を削除しました。"
        }
    }
}

# 3. ProgId 自体を削除
if (Test-Path -LiteralPath $progIdKey) {
    Remove-Item -Path $progIdKey -Recurse -Force
    Write-Host "ProgId '$progId' を削除しました。"
} else {
    Write-Host "ProgId '$progId' は登録されていませんでした。"
}

# 3a. URL プロトコル 'hirake' を削除（Hirake 専用のキーなので丸ごと消してよい）
$schemeKey = 'HKCU:\Software\Classes\hirake'
if (Test-Path -LiteralPath $schemeKey) {
    Remove-Item -Path $schemeKey -Recurse -Force
    Write-Host "URL プロトコル 'hirake://' を削除しました。"
} else {
    Write-Host "URL プロトコル 'hirake://' は登録されていませんでした。"
}

# 4. エクスプローラーへの反映通知
#    （SHChangeNotify はファイル関連付け向けの通知。URL プロトコルには不要。）
Send-ShellChangeNotification

Write-Host ''
Write-Host '===================================================================='
Write-Host 'Hirake の関連付け解除が完了しました。'
Write-Host '===================================================================='
