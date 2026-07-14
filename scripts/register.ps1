#requires -Version 5.1
<#
.SYNOPSIS
    Hirake を .md / .markdown ファイルの「プログラムから開く」候補として HKCU に登録します。
    管理者権限は不要です（HKCU のみを使用）。

.PARAMETER ExePath
    Hirake.exe への明示パス。省略時は以下の順で自動検出します。
      1. <スクリプトの親>\publish\Hirake.exe
      2. <スクリプトの親>\src\Hirake\bin\Release\net10.0-windows\Hirake.exe
    どちらも見つからない場合はエラーを表示して終了します。

.EXAMPLE
    .\register.ps1
    .\register.ps1 -ExePath "C:\Tools\Hirake\Hirake.exe"
#>

[CmdletBinding()]
param(
    [string]$ExePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-HirakeExePath {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit -PathType Leaf) {
            return (Resolve-Path -LiteralPath $Explicit).ProviderPath
        }
        Write-Error "指定された -ExePath が見つかりません: $Explicit"
        exit 1
    }

    $candidate1 = Join-Path $PSScriptRoot '..\publish\Hirake.exe'
    if (Test-Path -LiteralPath $candidate1 -PathType Leaf) {
        return (Resolve-Path -LiteralPath $candidate1).ProviderPath
    }

    $candidate2 = Join-Path $PSScriptRoot '..\src\Hirake\bin\Release\net10.0-windows\Hirake.exe'
    if (Test-Path -LiteralPath $candidate2 -PathType Leaf) {
        return (Resolve-Path -LiteralPath $candidate2).ProviderPath
    }

    Write-Error @"
Hirake.exe が見つかりませんでした。以下のいずれかの方法で解決してください。
  1. -ExePath パラメータで exe のフルパスを明示指定する
  2. 次のいずれかの場所に exe を配置してビルドする
       $((Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath)\publish\Hirake.exe
       $((Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath)\src\Hirake\bin\Release\net10.0-windows\Hirake.exe
"@
    exit 1
}

# SHChangeNotify を P/Invoke で呼び出し、エクスプローラーに関連付け変更を通知する
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

$resolvedExePath = Resolve-HirakeExePath -Explicit $ExePath
Write-Host "Hirake.exe を検出しました: $resolvedExePath"

$progId = 'Hirake.md'
$progIdKey = "HKCU:\Software\Classes\$progId"
$openCommand = "`"$resolvedExePath`" `"%1`""

# 0. 旧名（MdViewer.md）で登録済みの関連付けを移行・削除
$legacyProgId = 'MdViewer.md'
$legacyProgIdKey = "HKCU:\Software\Classes\$legacyProgId"
if (Test-Path $legacyProgIdKey) {
    Remove-Item -Path $legacyProgIdKey -Recurse -Force
    Write-Host "旧 ProgId '$legacyProgId' を削除しました。"
}
foreach ($ext in @('.md', '.markdown')) {
    $extKey = "HKCU:\Software\Classes\$ext"
    $openWithKey = "$extKey\OpenWithProgIds"
    if (Test-Path $openWithKey) {
        try { Remove-ItemProperty -Path $openWithKey -Name $legacyProgId -ErrorAction Stop } catch {}
    }
    # 既定プログラムが旧 ProgId の場合はクリアし、後続の手順 3 で新 ProgId を設定させる
    if (Test-Path $extKey) {
        try {
            $current = (Get-ItemProperty -Path $extKey -Name '(default)' -ErrorAction Stop).'(default)'
            if ($current -eq $legacyProgId) {
                Remove-ItemProperty -Path $extKey -Name '(default)' -ErrorAction Stop
                Write-Host "'$ext' の既定プログラム（旧 '$legacyProgId'）をクリアしました。"
            }
        } catch {}
    }
}

# 1. ProgId の登録
New-Item -Path $progIdKey -Force | Out-Null
Set-ItemProperty -Path $progIdKey -Name '(default)' -Value 'Markdown Document'

New-Item -Path "$progIdKey\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$progIdKey\DefaultIcon" -Name '(default)' -Value "$resolvedExePath,0"

New-Item -Path "$progIdKey\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$progIdKey\shell\open\command" -Name '(default)' -Value $openCommand

Write-Host "ProgId '$progId' を登録しました。"

# 2. .md / .markdown の OpenWithProgIds に追加
foreach ($ext in @('.md', '.markdown')) {
    $extKey = "HKCU:\Software\Classes\$ext"
    $openWithKey = "$extKey\OpenWithProgIds"

    New-Item -Path $openWithKey -Force | Out-Null
    # OpenWithProgIds の値名として ProgId をそのまま使用し、値は空文字（既定の作法）
    New-ItemProperty -Path $openWithKey -Name $progId -Value '' -PropertyType String -Force | Out-Null

    Write-Host "拡張子 '$ext' の OpenWithProgIds に '$progId' を追加しました。"
}

# 3. .md の既定プログラムを設定（既存値がある場合は上書きしない）
$mdExtKey = 'HKCU:\Software\Classes\.md'
New-Item -Path $mdExtKey -Force | Out-Null
$existingDefault = $null
try {
    $existingDefault = (Get-ItemProperty -Path $mdExtKey -Name '(default)' -ErrorAction Stop).'(default)'
} catch {
    $existingDefault = $null
}

if ([string]::IsNullOrEmpty($existingDefault)) {
    Set-ItemProperty -Path $mdExtKey -Name '(default)' -Value $progId
    Write-Host "'.md' の既定プログラムとして '$progId' を設定しました。"
} else {
    Write-Host "'.md' には既に既定プログラム '$existingDefault' が設定されているため、上書きしませんでした。"
}

# 4. エクスプローラーへの反映通知
Send-ShellChangeNotification

Write-Host ''
Write-Host '===================================================================='
Write-Host 'Hirake の関連付け登録が完了しました。'
Write-Host 'Windows 11 では初回のみ、.md ファイルを右クリック →'
Write-Host '「プログラムから開く」→「別のプログラムを選択」→'
Write-Host 'Hirake を選び「常にこのアプリを使う」にチェックを入れる操作が'
Write-Host '必要な場合があります。'
Write-Host '===================================================================='
