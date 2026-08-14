#requires -Version 5.1
<#
.SYNOPSIS
    scripts/association-lib.ps1 の回帰テスト。

.DESCRIPTION
    実際の .md / .markdown 関連付けには一切触れず、使い捨てのレジストリキー
    （HKCU:\Software\Classes\ZZHirakeTest）と使い捨ての拡張子（.zzmdtest）だけを
    使って検証する。テスト後は必ず削除する。

    register.ps1 / unregister.ps1 が呼ぶのと同じ関数を、同じ引数の形で呼ぶため、
    「テスト用の再実装」にはならない。

    Pester には依存しない（追加インストール不要）。

.EXAMPLE
    .\tests\test-association-lib.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$libPath = Join-Path $PSScriptRoot '..\scripts\association-lib.ps1'
if (-not (Test-Path -LiteralPath $libPath -PathType Leaf)) {
    Write-Error "共通スクリプトが見つかりません: $libPath"
    exit 1
}
. $libPath

$script:failed = 0
$script:passed = 0

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

$testKey = 'HKCU:\Software\Classes\ZZHirakeTest'
$testExt = '.zzmdtest'
$testExtKey = "HKCU:\Software\Classes\$testExt"
$progId = 'Hirake.md'
$exeName = 'Hirake.exe'

function Reset-Sandbox {
    foreach ($path in @($testKey, $testExtKey)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

# キーの内容（値・サブキー）を再帰的に文字列化する。往復の完全一致確認に使う。
function Get-KeySnapshot {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return '<absent>'
    }

    $lines = @()
    $key = Get-Item -LiteralPath $Path
    foreach ($name in ($key.GetValueNames() | Sort-Object)) {
        $label = if ($name -eq '') { '(default)' } else { $name }
        $lines += "$Path :: $label = [$($key.GetValue($name))]"
    }
    if ($key.GetValueNames().Count -eq 0) {
        $lines += "$Path :: (値なし)"
    }
    foreach ($sub in ($key.GetSubKeyNames() | Sort-Object)) {
        $lines += Get-KeySnapshot -Path "$Path\$sub"
    }
    return ($lines -join "`n")
}

# 固定名を使うため、実行前に同名キーが存在しないことを確認する。
# 「使い捨てのつもりの名前」であることは、既存データを消してよい根拠にならない。
foreach ($path in @($testKey, $testExtKey)) {
    if (Test-Path -LiteralPath $path) {
        Write-Error "テスト用のキーが既に存在します。中身を確認して手動で削除してから再実行してください: $path"
        exit 1
    }
}

try {

    # ---- レジストリ層プリミティブ -------------------------------------

    Write-Host 'レジストリ層プリミティブ'

    # 既存キーを壊さないこと（不具合 1・2 の再発防止）
    Initialize-RegistryKey -Path "$testKey\Sub"
    Set-ItemProperty -Path $testKey -Name '(default)' -Value 'OtherApp'
    New-ItemProperty -Path $testKey -Name 'Extra' -Value 'x' -PropertyType String -Force | Out-Null
    Initialize-RegistryKey -Path $testKey
    Assert-Equal '既存キーの既定値が保持される'   (Get-RegistryDefaultValue -Path $testKey) 'OtherApp'
    Assert-Equal '既存キーのサブキーが保持される' (Test-Path -LiteralPath "$testKey\Sub") $true
    Assert-Equal '既存キーの他の値が保持される'   (Test-RegistryValueName -Path $testKey -Name 'Extra') $true

    Reset-Sandbox
    Initialize-RegistryKey -Path "$testKey\shell\open\command"
    Assert-Equal '中間キーごと作成できる' (Test-Path -LiteralPath "$testKey\shell\open\command") $true

    # 既定値の 3 状態（不具合 3 の前提）
    Reset-Sandbox
    Initialize-RegistryKey -Path $testKey
    Assert-Equal '既定値: 値が無ければ null' (Get-RegistryDefaultValue -Path $testKey) $null
    Set-ItemProperty -Path $testKey -Name '(default)' -Value ''
    Assert-Equal '既定値: 空文字は空文字'     (Get-RegistryDefaultValue -Path $testKey) ''
    Set-ItemProperty -Path $testKey -Name '(default)' -Value 'OtherApp.md'
    Assert-Equal '既定値: 非空はその値'       (Get-RegistryDefaultValue -Path $testKey) 'OtherApp.md'

    # 既定値の削除（Remove-ItemProperty では消せない）
    Assert-Equal '既定値の削除に成功する'   (Remove-RegistryDefaultValue -Path $testKey) $true
    Assert-Equal '削除後は値が無い'         (Get-RegistryDefaultValue -Path $testKey) $null
    Assert-Equal '二重削除は false（冪等）' (Remove-RegistryDefaultValue -Path $testKey) $false
    Assert-Equal '削除してもキーは残る'     (Test-Path -LiteralPath $testKey) $true

    # 空キーの掃除
    Assert-Equal '空キーは削除される'             (Remove-RegistryKeyIfEmpty -Path $testKey) $true
    Initialize-RegistryKey -Path "$testKey\Sub"
    Assert-Equal 'サブキーがあれば削除しない'     (Remove-RegistryKeyIfEmpty -Path $testKey) $false
    Reset-Sandbox
    Initialize-RegistryKey -Path $testKey
    Set-ItemProperty -Path $testKey -Name '(default)' -Value ''
    Assert-Equal '空文字の値があれば削除しない'   (Remove-RegistryKeyIfEmpty -Path $testKey) $false
    Assert-Equal '不在キーは false'               (Remove-RegistryKeyIfEmpty -Path "$testKey\nope") $false

    # HKCU 以外は扱わない
    $threw = $false
    try { ConvertTo-HkcuSubKeyPath -Path 'HKLM:\Software\Classes\.md' } catch { $threw = $true }
    Assert-Equal 'HKCU 以外のパスは例外' $threw $true

    # ---- コマンドラインからの実行ファイル名抽出 -----------------------

    Write-Host ''
    Write-Host '実行ファイル名の抽出（所有権判定の土台）'

    $exeCases = @(
        @{ Line = '"C:\a b\Hirake.exe" "%1"';       Expected = 'Hirake.exe' },
        @{ Line = 'C:\a\Hirake.exe %1';             Expected = 'Hirake.exe' },
        @{ Line = '"C:\a\Hirake.exe"';              Expected = 'Hirake.exe' },
        @{ Line = '   ';                            Expected = $null },
        @{ Line = '"C:\a\Hirake.exe';               Expected = $null },
        @{ Line = '"C:\Other\Hirake.exe".bak "%1"'; Expected = $null },
        @{ Line = '"C:\Other\Hirake.exe"x "%1"';    Expected = $null }
    )
    foreach ($case in $exeCases) {
        Assert-Equal ("抽出: " + $case.Line) (Get-CommandExecutableName -CommandLine $case.Line) $case.Expected
    }

    # ---- Hirake 名義キーの所有権判定 ----------------------------------

    Write-Host ''
    Write-Host 'Hirake 名義キーの所有権判定'

    Reset-Sandbox
    Assert-Equal 'キーが無ければ NotFound' (Test-HirakeOwnedKey -Path $testKey -ExeName $exeName) 'NotFound'

    Initialize-RegistryKey -Path $testKey
    Assert-Equal 'command が無ければ Unknown' (Test-HirakeOwnedKey -Path $testKey -ExeName $exeName) 'Unknown'
    Assert-Equal 'Unknown は削除しない'       (Remove-HirakeOwnedKey -Path $testKey -ExeName $exeName) 'Unknown'
    Assert-Equal 'Unknown でキーが残る'       (Test-Path -LiteralPath $testKey) $true

    $notOwnedCases = @(
        '"C:\Other\OtherApp.exe" "%1"',
        '"C:\Other\FakeHirake.exe.bak" "%1"',
        '"C:\Other\Hirake.exe.exe" "%1"',
        '"C:\Other\Hirake.exe".bak "%1"',
        '"C:\Other\Hirake.exe"x "%1"',
        '"C:\Other\OtherApp.exe" "C:\Tools\Hirake.exe"',
        'cmd /c echo Hirake.exe'
    )
    foreach ($command in $notOwnedCases) {
        Reset-Sandbox
        Initialize-RegistryKey -Path "$testKey\shell\open\command"
        Set-ItemProperty -Path "$testKey\shell\open\command" -Name '(default)' -Value $command
        Assert-Equal ("NotOwned: " + $command) (Test-HirakeOwnedKey -Path $testKey -ExeName $exeName) 'NotOwned'
        Assert-Equal 'NotOwned でキーが残る'   (Remove-HirakeOwnedKey -Path $testKey -ExeName $exeName) 'NotOwned'
        Assert-Equal 'NotOwned で削除されない' (Test-Path -LiteralPath $testKey) $true
    }

    foreach ($command in @('"C:\Tools\Hirake\Hirake.exe" "%1"', 'C:\Tools\Hirake\Hirake.exe %1')) {
        Reset-Sandbox
        Initialize-RegistryKey -Path "$testKey\shell\open\command"
        Set-ItemProperty -Path "$testKey\shell\open\command" -Name '(default)' -Value $command
        Assert-Equal ("Owned: " + $command)  (Test-HirakeOwnedKey -Path $testKey -ExeName $exeName) 'Owned'
        Assert-Equal 'Owned なら削除される'  (Remove-HirakeOwnedKey -Path $testKey -ExeName $exeName) 'Removed'
        Assert-Equal 'Owned で消えている'    (Test-Path -LiteralPath $testKey) $false
    }

    # register.ps1 と同じ手順（インプレース更新）でユーザー追加の verb が残ること
    Reset-Sandbox
    Initialize-RegistryKey -Path "$testKey\shell\edit\command"
    Set-ItemProperty -Path "$testKey\shell\edit\command" -Name '(default)' -Value '"C:\Editor\edit.exe" "%1"'
    Initialize-RegistryKey -Path $testKey
    Set-ItemProperty -Path $testKey -Name '(default)' -Value 'Markdown Document'
    Initialize-RegistryKey -Path "$testKey\shell\open\command"
    Set-ItemProperty -Path "$testKey\shell\open\command" -Name '(default)' -Value '"C:\Tools\Hirake\Hirake.exe" "%1"'
    Assert-Equal 'ユーザー追加の verb が残る' (Test-Path -LiteralPath "$testKey\shell\edit\command") $true

    # ---- 拡張子の登録・解除 -------------------------------------------

    Write-Host ''
    Write-Host '拡張子の登録・解除（他アプリ共存）'

    Reset-Sandbox
    Initialize-RegistryKey -Path $testExtKey
    Set-ItemProperty -Path $testExtKey -Name '(default)' -Value 'OtherApp.md'
    Initialize-RegistryKey -Path "$testExtKey\OpenWithProgIds"
    New-ItemProperty -Path "$testExtKey\OpenWithProgIds" -Name 'OtherApp.md' -Value '' -PropertyType String -Force | Out-Null
    Initialize-RegistryKey -Path "$testExtKey\ShellNew"
    Set-ItemProperty -Path "$testExtKey\ShellNew" -Name 'NullFile' -Value ''
    $before = Get-KeySnapshot -Path $testExtKey

    $result = Register-HirakeExtension -Extension $testExt -ProgId $progId -SetAsDefault
    Assert-Equal '登録: 書き込んだ'                 $result.Written $true
    Assert-Equal '登録: 既定値は上書きしない'       $result.DefaultSet $false
    Assert-Equal '登録: 既存の既定値を報告する'     $result.ExistingDefault 'OtherApp.md'
    Assert-Equal '登録: 既定は他アプリのまま'       (Get-RegistryDefaultValue -Path $testExtKey) 'OtherApp.md'
    Assert-Equal '登録: 他アプリの OpenWith が残る' (Test-RegistryValueName -Path "$testExtKey\OpenWithProgIds" -Name 'OtherApp.md') $true
    Assert-Equal '登録: 自分の OpenWith が入る'     (Test-RegistryValueName -Path "$testExtKey\OpenWithProgIds" -Name $progId) $true
    Assert-Equal '登録: 他アプリのサブキーが残る'   (Test-Path -LiteralPath "$testExtKey\ShellNew") $true

    $snapshot = Get-KeySnapshot -Path $testExtKey
    $again = Register-HirakeExtension -Extension $testExt -ProgId $progId -SetAsDefault
    Assert-Equal '再登録: 書き込まない（冪等）' $again.Written $false
    Assert-Equal '再登録: 状態が変わらない'     ((Get-KeySnapshot -Path $testExtKey) -eq $snapshot) $true

    $result = Unregister-HirakeExtension -Extension $testExt -ProgId $progId
    Assert-Equal '解除: 自分の値を削除した'         $result.RemovedProgId $true
    Assert-Equal '解除: 他アプリの既定値は残す'     $result.RemovedDefault $false
    Assert-Equal '解除: OpenWith キーは残す'        $result.RemovedOpenWithKey $false
    Assert-Equal '解除: 拡張子キーは残す'           $result.RemovedExtKey $false
    Assert-Equal '往復: 実行前と完全一致'           ((Get-KeySnapshot -Path $testExtKey) -eq $before) $true

    # 何も無い状態からの往復（空キーの掃除）
    Reset-Sandbox
    $result = Register-HirakeExtension -Extension $testExt -ProgId $progId -SetAsDefault
    Assert-Equal '新規: 既定値を設定する'   $result.DefaultSet $true
    Assert-Equal '新規: 既定値が自分'       (Get-RegistryDefaultValue -Path $testExtKey) $progId
    $result = Unregister-HirakeExtension -Extension $testExt -ProgId $progId
    Assert-Equal '新規往復: 既定値を削除'   $result.RemovedDefault $true
    Assert-Equal '新規往復: OpenWith 削除'  $result.RemovedOpenWithKey $true
    Assert-Equal '新規往復: 拡張子キー削除' $result.RemovedExtKey $true
    Assert-Equal '新規往復: キーごと消える' (Get-KeySnapshot -Path $testExtKey) '<absent>'

    # 既定値が空文字の場合は上書きしない
    Reset-Sandbox
    Initialize-RegistryKey -Path $testExtKey
    Set-ItemProperty -Path $testExtKey -Name '(default)' -Value ''
    $before = Get-KeySnapshot -Path $testExtKey
    $result = Register-HirakeExtension -Extension $testExt -ProgId $progId -SetAsDefault
    Assert-Equal '空文字の既定値は上書きしない' $result.DefaultSet $false
    Assert-Equal '空文字の既定値を報告する'     $result.ExistingDefault ''
    $result = Unregister-HirakeExtension -Extension $testExt -ProgId $progId
    Assert-Equal '空文字の既定値は削除しない'   $result.RemovedDefault $false
    Assert-Equal '空文字往復: 実行前と一致'     ((Get-KeySnapshot -Path $testExtKey) -eq $before) $true

    # 未登録の状態で解除しても何も報告しない
    Reset-Sandbox
    $result = Unregister-HirakeExtension -Extension $testExt -ProgId $progId
    Assert-Equal '未登録の解除: 何も削除しない (1)' $result.RemovedProgId $false
    Assert-Equal '未登録の解除: 何も削除しない (2)' $result.RemovedDefault $false
    Assert-Equal '未登録の解除: 何も削除しない (3)' $result.RemovedOpenWithKey $false
    Assert-Equal '未登録の解除: 何も削除しない (4)' $result.RemovedExtKey $false
    Assert-Equal '未登録の解除: キーを作らない'     (Test-Path -LiteralPath $testExtKey) $false

    # 引数の書式検証（パス組み立てへの混入を防ぐ）
    Write-Host ''
    Write-Host '引数の書式検証'
    foreach ($bad in @('..\..\Foo', '.md\..\..\Foo', 'md', '')) {
        $threw = $false
        try { Register-HirakeExtension -Extension $bad -ProgId $progId } catch { $threw = $true }
        Assert-Equal ("不正な拡張子を拒否: [" + $bad + "]") $threw $true
    }
    $threw = $false
    try { Unregister-HirakeExtension -Extension $testExt -ProgId 'A\B' } catch { $threw = $true }
    Assert-Equal '不正な ProgId を拒否' $threw $true
} finally {
    Reset-Sandbox
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
