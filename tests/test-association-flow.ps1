#requires -Version 5.1
<#
.SYNOPSIS
    register.ps1 / unregister.ps1 の制御フロー（Invoke-Hirake*）の統合テスト。

.DESCRIPTION
    tests\test-association-lib.ps1 が「関数単体」を検証するのに対し、こちらは
    「関数を呼ぶ順序」と「どの失敗で何をやめるか」を検証する。

    実際の .md / .markdown / Hirake.md / hirake には一切触れず、使い捨ての
    ProgId・URL スキーム・拡張子だけを使う。register.ps1 / unregister.ps1 が
    呼ぶのと同じ関数を、同じ形で呼ぶ。

    Pester には依存しない（追加インストール不要）。

.EXAMPLE
    .\tests\test-association-flow.ps1
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

# 失敗注入で差し替えた関数を戻すためのスクリプトブロック（finally からも呼ぶ）。
$restoreShadows = $null

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

# ---- 使い捨ての対象 -------------------------------------------------

# 実行ごとに一意な名前にする。並列実行や、強制終了で残骸が残った後の再実行でも
# 衝突しない。後片付けはこの実行が作った対象だけを対象にする。
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)

$testProgId    = "ZZHirakeFlowProgId$runId"
$testProgIdKey = "HKCU:\Software\Classes\$testProgId"
$testSchemeKey = "HKCU:\Software\Classes\ZZHirakeFlowScheme$runId"
$testExt       = ".zzmdflow$runId"
$testExt2      = ".zzmdflow2$runId"
$testExtKey    = "HKCU:\Software\Classes$([char]92)$testExt"
$testExtKey2   = "HKCU:\Software\Classes$([char]92)$testExt2"
$testExeName   = 'ZZHirakeFlow.exe'

$allKeys = @($testProgIdKey, $testSchemeKey, $testExtKey, $testExtKey2)

# ExePath は Invoke-HirakeRegister 側で実在を要求しない（呼び出し側が解決済みの
# パスを渡す契約）が、実運用に近づけるため一時ファイルを作って渡す。
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "ZZHirakeFlowTest$runId"
$testExePath = Join-Path $tempDir $testExeName

function Reset-Sandbox {
    foreach ($path in $allKeys) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

# 対象キー全体のスナップショット。中止時に「レジストリを 1 つも変更していない」
# ことを確認するために使う。
function Get-Snapshot {
    $lines = @()
    foreach ($path in $allKeys) {
        $lines += Get-KeySnapshot -Path $path
    }
    return ($lines -join "`n")
}

function Get-KeySnapshot {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return "$Path <absent>"
    }

    $lines = @()
    $key = Get-Item -LiteralPath $Path
    foreach ($name in ($key.GetValueNames() | Sort-Object)) {
        $label = if ($name -eq '') { '(default)' } else { $name }
        # 値の型も含める。型だけが変わった場合（REG_EXPAND_SZ → REG_SZ 等）に
        # 「未変更」と誤判定しないため。環境変数も展開せず生の値で比較する。
        $kind = $key.GetValueKind($name)
        $raw = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $lines += "$Path :: $label :: $kind = [$raw]"
    }
    if ($key.GetValueNames().Count -eq 0) {
        $lines += "$Path :: (値なし)"
    }
    foreach ($sub in ($key.GetSubKeyNames() | Sort-Object)) {
        $lines += Get-KeySnapshot -Path "$Path\$sub"
    }
    return ($lines -join "`n")
}

# 「別アプリが使っている」状態を作る（command が別の exe を指す）。
function New-ForeignKey {
    param([Parameter(Mandatory)][string]$Path)
    Initialize-RegistryKey -Path "$Path\shell\open\command"
    Set-ItemProperty -Path "$Path\shell\open\command" -Name '(default)' -Value '"C:\Other\OtherApp.exe" "%1"'
}

# 「由来を判定できない」状態を作る（キーはあるが command が無い）。
function New-UnknownKey {
    param([Parameter(Mandatory)][string]$Path)
    Initialize-RegistryKey -Path $Path
}

function Invoke-TestRegister {
    param([string[]]$Extensions = @($testExt, $testExt2), [string]$ExePath = $testExePath)
    return Invoke-HirakeRegister `
        -ExePath          $ExePath `
        -ProgId           $testProgId `
        -SchemeKey        $testSchemeKey `
        -Extensions       $Extensions `
        -DefaultExtension $testExt `
        -ExpectedExeName  $testExeName
}

function Invoke-TestUnregister {
    param([string[]]$Extensions = @($testExt, $testExt2))
    return Invoke-HirakeUnregister `
        -ProgId          $testProgId `
        -SchemeKey       $testSchemeKey `
        -Extensions      $Extensions `
        -ExpectedExeName $testExeName
}

# ---- 実行前の確認 ---------------------------------------------------

foreach ($path in $allKeys) {
    if (Test-Path -LiteralPath $path) {
        Write-Error "テスト用のキーが既に存在します。中身を確認して手動で削除してから再実行してください: $path"
        exit 1
    }
}
if (Test-Path -LiteralPath $tempDir) {
    Write-Error "テスト用の一時フォルダが既に存在します。中身を確認して手動で削除してから再実行してください: $tempDir"
    exit 1
}

try {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    New-Item -ItemType File -Path $testExePath -Force | Out-Null

    # ---- 正常系 -----------------------------------------------------

    Write-Host '正常系'
    Reset-Sandbox

    $result = Invoke-TestRegister

    # 以降のアサーションは Extensions[0] 等を索引で参照する。ここで失敗している場合、
    # 索引外アクセスの例外になって原因が分からなくなるため、先に打ち切る。
    # （HKCU へ書き込めない環境ではここで止まる）
    if ($result.Status -ne 'Completed' -or @($result.Extensions).Count -ne 2) {
        Write-Host ("  FAIL 登録の正常系が成立しませんでした: Status=" + $result.Status +
            " Extensions=" + @($result.Extensions).Count + " AbortReason=" + $result.AbortReason)
        Write-Host '       HKCU へ書き込めない環境ではこの時点で失敗します。'
        $script:failed++
        return
    }

    Assert-Equal '登録: Status'                 $result.Status 'Completed'
    Assert-Equal '登録: ProgId を書いた'        $result.ProgIdWritten $true
    Assert-Equal '登録: スキームを書いた'       $result.SchemeWritten $true
    Assert-Equal '登録: 拡張子は 2 件'          $result.Extensions.Count 2
    Assert-Equal '登録: 失敗 0 件'              $result.FailureCount 0
    Assert-Equal '登録: 1 件目が既定'           $result.Extensions[0].IsDefault $true
    Assert-Equal '登録: 2 件目は既定でない'     $result.Extensions[1].IsDefault $false
    Assert-Equal '登録: 既定 ProgId を設定'     $result.Extensions[0].DefaultSet $true
    Assert-Equal '登録: 既定は 1 件目だけ'      $result.Extensions[1].DefaultSet $false
    Assert-Equal '登録: 2 件目に既定値は付かない' (Get-RegistryDefaultValue -Path $testExtKey2) $null
    Assert-Equal '登録: command が exe を指す'  ((Get-RegistryDefaultValue -Path "$testProgIdKey\shell\open\command") -like "*$testExeName*") $true
    Assert-Equal '登録: URL Protocol がある'    (Test-RegistryValueName -Path $testSchemeKey -Name 'URL Protocol') $true

    $afterRegister = Get-Snapshot
    $again = Invoke-TestRegister
    Assert-Equal '再登録: Status'               $again.Status 'Completed'
    Assert-Equal '再登録: 書き込まない'         $again.Extensions[0].Written $false
    Assert-Equal '再登録: 状態が変わらない'     ((Get-Snapshot) -eq $afterRegister) $true

    $result = Invoke-TestUnregister
    Assert-Equal '解除: Status'                 $result.Status 'Completed'
    Assert-Equal '解除: 見送っていない'         $result.SkippedTargets $false
    Assert-Equal '解除: ProgId を削除'          $result.Targets[0].Status 'Removed'
    Assert-Equal '解除: スキームを削除'         $result.Targets[1].Status 'Removed'
    Assert-Equal '往復: すべて消えている'       (Get-Snapshot) (($allKeys | ForEach-Object { "$_ <absent>" }) -join "`n")

    # ---- 中止: exe 名が違う -----------------------------------------

    Write-Host ''
    Write-Host '中止: 実行ファイル名が期待と違う'
    Reset-Sandbox
    $before = Get-Snapshot

    $renamed = Join-Path $tempDir 'Renamed.exe'
    New-Item -ItemType File -Path $renamed -Force | Out-Null
    $result = Invoke-TestRegister -ExePath $renamed
    Assert-Equal 'Status'                       $result.Status 'AbortedInvalidExeName'
    Assert-Equal 'ProgId を書いていない'        $result.ProgIdWritten $false
    Assert-Equal 'レジストリが実行前と一致'     ((Get-Snapshot) -eq $before) $true

    # ---- 中止: 別アプリが所有 ---------------------------------------

    Write-Host ''
    Write-Host '中止: 別アプリが同名キーを使用している'

    foreach ($foreign in @(@{ Path = $testProgIdKey; Name = 'ProgId' }, @{ Path = $testSchemeKey; Name = 'スキーム' })) {
        Reset-Sandbox
        New-ForeignKey -Path $foreign.Path
        $before = Get-Snapshot

        $result = Invoke-TestRegister
        Assert-Equal ("登録 [" + $foreign.Name + " が別アプリ]: Status")     $result.Status 'AbortedNotOwned'
        Assert-Equal ("登録 [" + $foreign.Name + " が別アプリ]: 中止対象")   $result.AbortedTarget $foreign.Path
        Assert-Equal ("登録 [" + $foreign.Name + " が別アプリ]: 未変更")     ((Get-Snapshot) -eq $before) $true

        $result = Invoke-TestUnregister
        Assert-Equal ("解除 [" + $foreign.Name + " が別アプリ]: Status")     $result.Status 'AbortedNotOwned'
        Assert-Equal ("解除 [" + $foreign.Name + " が別アプリ]: 中止対象")   $result.AbortedTarget $foreign.Path
        Assert-Equal ("解除 [" + $foreign.Name + " が別アプリ]: 未変更")     ((Get-Snapshot) -eq $before) $true
    }

    # ---- 中止: 由来を判定できない（解除のみ）------------------------
    #
    # #41 の最終レビューで見つかった不具合の再発防止。
    # Unknown の中止判定が拡張子処理より後にあると、拡張子側だけ先に変更されてから
    # 失敗する。関数単体テストでは検出できない種類の不具合。

    Write-Host ''
    Write-Host '中止: 由来を判定できないキーがある（解除）'

    foreach ($unknown in @(@{ Path = $testProgIdKey; Other = $testSchemeKey; Name = 'ProgId' },
                           @{ Path = $testSchemeKey; Other = $testProgIdKey; Name = 'スキーム' })) {
        Reset-Sandbox

        # 片方は正常に登録された状態、もう片方は command 無しにする。
        Invoke-TestRegister | Out-Null
        Remove-Item -LiteralPath "$($unknown.Path)\shell" -Recurse -Force
        $before = Get-Snapshot

        $result = Invoke-TestUnregister
        Assert-Equal ("解除 [" + $unknown.Name + " が Unknown]: Status")   $result.Status 'AbortedUnknown'
        Assert-Equal ("解除 [" + $unknown.Name + " が Unknown]: 中止対象") $result.AbortedTarget $unknown.Path
        Assert-Equal ("解除 [" + $unknown.Name + " が Unknown]: 未変更")   ((Get-Snapshot) -eq $before) $true
        Assert-Equal ("解除 [" + $unknown.Name + " が Unknown]: 拡張子が残る") `
            (Test-RegistryValueName -Path "$testExtKey\OpenWithProgIds" -Name $testProgId) $true
        Assert-Equal ("解除 [" + $unknown.Name + " が Unknown]: 相手のキーも残る") `
            (Test-Path -LiteralPath $unknown.Other) $true
    }

    # ---- 修復: register が Unknown のキーを直せる --------------------

    Write-Host ''
    Write-Host '修復: Unknown のキーを register で直せる'
    Reset-Sandbox
    New-UnknownKey -Path $testProgIdKey

    Assert-Equal '修復前は Unknown' (Test-HirakeOwnedKey -Path $testProgIdKey -ExeName $testExeName) 'Unknown'
    $result = Invoke-TestRegister
    Assert-Equal '修復: Status'     $result.Status 'Completed'
    Assert-Equal '修復後は Owned'   (Test-HirakeOwnedKey -Path $testProgIdKey -ExeName $testExeName) 'Owned'
    $result = Invoke-TestUnregister
    Assert-Equal '修復後は解除できる' $result.Status 'Completed'

    # ---- 他アプリ共存下の往復 ---------------------------------------

    Write-Host ''
    Write-Host '他アプリ共存下の往復'
    Reset-Sandbox

    Initialize-RegistryKey -Path $testExtKey
    Set-ItemProperty -Path $testExtKey -Name '(default)' -Value 'OtherApp.md'
    Initialize-RegistryKey -Path "$testExtKey\OpenWithProgIds"
    New-ItemProperty -Path "$testExtKey\OpenWithProgIds" -Name 'OtherApp.md' -Value '' -PropertyType String -Force | Out-Null
    Initialize-RegistryKey -Path "$testExtKey\ShellNew"
    Set-ItemProperty -Path "$testExtKey\ShellNew" -Name 'NullFile' -Value ''
    $before = Get-Snapshot

    $result = Invoke-TestRegister
    Assert-Equal '共存: 既定値を上書きしない'   $result.Extensions[0].DefaultSet $false
    Assert-Equal '共存: 既存の既定値を報告'     $result.Extensions[0].ExistingDefault 'OtherApp.md'
    Assert-Equal '共存: 他アプリの既定が残る'   (Get-RegistryDefaultValue -Path $testExtKey) 'OtherApp.md'
    Assert-Equal '共存: 他アプリの OpenWith が残る' (Test-RegistryValueName -Path "$testExtKey\OpenWithProgIds" -Name 'OtherApp.md') $true
    Assert-Equal '共存: 他アプリのサブキーが残る'   (Test-Path -LiteralPath "$testExtKey\ShellNew") $true

    $result = Invoke-TestUnregister
    Assert-Equal '共存: 解除 Status'            $result.Status 'Completed'
    Assert-Equal '共存往復: 実行前と完全一致'   ((Get-Snapshot) -eq $before) $true

    # ---- 未登録の状態で解除 -----------------------------------------

    Write-Host ''
    Write-Host '未登録の状態で解除'
    Reset-Sandbox
    $before = Get-Snapshot

    $result = Invoke-TestUnregister
    Assert-Equal '未登録: Status'               $result.Status 'Completed'
    Assert-Equal '未登録: ProgId は NotFound'   $result.Targets[0].Status 'NotFound'
    Assert-Equal '未登録: スキームは NotFound'  $result.Targets[1].Status 'NotFound'
    Assert-Equal '未登録: 何も削除していない'   $result.Extensions[0].RemovedProgId $false
    Assert-Equal '未登録: 状態が変わらない'     ((Get-Snapshot) -eq $before) $true

    # ---- 引数の検証 -------------------------------------------------

    Write-Host ''
    Write-Host '引数の検証'
    Reset-Sandbox

    $threw = $false
    try { Invoke-HirakeRegister -ExePath $testExePath -ProgId 'A\B' -SchemeKey $testSchemeKey -Extensions @($testExt) } catch { $threw = $true }
    Assert-Equal '不正な ProgId を拒否' $threw $true

    $threw = $false
    try { Invoke-HirakeUnregister -ProgId $testProgId -SchemeKey 'HKLM:\Software\Classes\x' -Extensions @($testExt) } catch { $threw = $true }
    Assert-Equal 'HKCU 以外のスキームキーを拒否' $threw $true

    $threw = $false
    try { Invoke-HirakeUnregister -ProgId $testProgId -SchemeKey $testSchemeKey -Extensions @('md') } catch { $threw = $true }
    Assert-Equal '不正な拡張子を拒否' $threw $true

    $threw = $false
    try { Invoke-HirakeUnregister -ProgId $testProgId -SchemeKey $testSchemeKey -Extensions @() } catch { $threw = $true }
    Assert-Equal '空の Extensions を拒否' $threw $true

    # 対象同士の衝突（同じキーを別の役割で二重に扱わせない）。
    Reset-Sandbox
    $before = Get-Snapshot

    $threw = $false
    try {
        Invoke-HirakeRegister -ExePath $testExePath -ProgId $testProgId -SchemeKey $testProgIdKey `
            -Extensions @($testExt) -DefaultExtension $testExt -ExpectedExeName $testExeName
    } catch { $threw = $true }
    Assert-Equal 'ProgId とスキームが同じキーなら拒否' $threw $true
    Assert-Equal '衝突検証は書き込み前に行う'          ((Get-Snapshot) -eq $before) $true

    $threw = $false
    try {
        Invoke-HirakeRegister -ExePath $testExePath -ProgId $testProgId -SchemeKey $testSchemeKey `
            -Extensions @($testExt, $testExt) -DefaultExtension $testExt -ExpectedExeName $testExeName
    } catch { $threw = $true }
    Assert-Equal '拡張子の重複を拒否'                  $threw $true

    $threw = $false
    try {
        Invoke-HirakeRegister -ExePath $testExePath -ProgId $testProgId -SchemeKey $testSchemeKey `
            -Extensions @($testExt, $testExt.ToUpperInvariant()) -DefaultExtension $testExt -ExpectedExeName $testExeName
    } catch { $threw = $true }
    Assert-Equal '大文字小文字違いの重複も拒否'        $threw $true
    Assert-Equal '衝突検証後もレジストリは未変更'      ((Get-Snapshot) -eq $before) $true

    # ---- 表示と終了コード（純粋関数） -------------------------------
    #
    # スクリプトの表示文言・表示順・終了コードも制御フローの一部で退行しうる。
    # レジストリに触れずに検証できるよう、結果オブジェクトから表示を組み立てる
    # 純粋関数を通して確認する。

    Write-Host ''
    Write-Host '表示と終了コード'

    function Get-RegisterPresentationFor {
        param($Result)
        return Get-HirakeRegisterPresentation -Result $Result -ExePath 'C:\x\Hirake.exe' `
            -ProgId 'Hirake.md' -SchemeKey 'HKCU:\Software\Classes\hirake' -ExpectedExeName 'Hirake.exe'
    }

    function New-RegisterResult {
        param([string]$Status = 'Completed', [int]$FailureCount = 0, $Extensions = @(), [string]$AbortedTarget, [string]$AbortReason,
              [bool]$ProgIdWritten = $true, [bool]$SchemeWritten = $true)
        return [pscustomobject]@{
            Status = $Status; AbortedTarget = $AbortedTarget; AbortReason = $AbortReason
            MayHaveModified = $true; ProgIdWritten = $ProgIdWritten; SchemeWritten = $SchemeWritten
            Extensions = $Extensions; FailureCount = $FailureCount
        }
    }

    # 表示は「行数」や「部分一致」ではなく、Stream と Message の全行を順序ごと
    # 固定する。順序の入れ替わりや空行の位置ずれも検出できるようにするため。
    function Format-Lines {
        param($Presentation)
        return (@($Presentation.Lines) | ForEach-Object { "$($_.Stream):$($_.Message)" }) -join "`n"
    }

    $p = Get-RegisterPresentationFor (New-RegisterResult -Status 'AbortedInvalidExeName' -AbortedTarget 'C:\x\Renamed.exe' -ProgIdWritten $false -SchemeWritten $false)
    Assert-Equal '登録表示 [別名 exe]: 終了コード'     $p.ExitCode 1
    Assert-Equal '登録表示 [別名 exe]: 通知しない'     $p.Notify $false
    Assert-Equal '登録表示 [別名 exe]: 1 行だけ'       @($p.Lines).Count 1
    Assert-Equal '登録表示 [別名 exe]: Error ストリーム' @($p.Lines)[0].Stream 'Error'

    $p = Get-RegisterPresentationFor (New-RegisterResult -Status 'AbortedNotOwned' -AbortedTarget 'HKCU:\Software\Classes\hirake' -ProgIdWritten $false -SchemeWritten $false)
    Assert-Equal '登録表示 [所有権]: 終了コード'       $p.ExitCode 1
    Assert-Equal '登録表示 [所有権]: 通知しない'       $p.Notify $false
    Assert-Equal '登録表示 [所有権]: 検出メッセージが先' @($p.Lines)[0].Message 'Hirake.exe を検出しました: C:\x\Hirake.exe'
    Assert-Equal '登録表示 [所有権]: スキームのラベル' (@($p.Lines)[1].Message -like "*URL プロトコル 'hirake://'*") $true

    $p = Get-RegisterPresentationFor (New-RegisterResult -Status 'CoreFailedPartiallyApplied' -AbortReason 'boom' -SchemeWritten $false)
    Assert-Equal '登録表示 [コア失敗]: 終了コード'     $p.ExitCode 1
    Assert-Equal '登録表示 [コア失敗]: 変更済みなら通知' $p.Notify $true
    Assert-Equal '登録表示 [コア失敗]: 警告 2 行'      @(@($p.Lines) | Where-Object { $_.Stream -eq 'Warning' }).Count 2

    $noChange = New-RegisterResult -Status 'CoreFailedPartiallyApplied' -AbortReason 'boom' -SchemeWritten $false
    $noChange.MayHaveModified = $false
    $p = Get-RegisterPresentationFor $noChange
    Assert-Equal '登録表示 [コア失敗・未変更]: 通知しない' $p.Notify $false

    $ok = [pscustomobject]@{ Extension = '.md'; Succeeded = $true; Error = $null; IsDefault = $true; Written = $true; DefaultSet = $true; ExistingDefault = $null }
    $ok2 = [pscustomobject]@{ Extension = '.markdown'; Succeeded = $true; Error = $null; IsDefault = $false; Written = $true; DefaultSet = $false; ExistingDefault = $null }
    $p = Get-RegisterPresentationFor (New-RegisterResult -Extensions @($ok, $ok2))
    Assert-Equal '登録表示 [正常]: 終了コード'         $p.ExitCode 0
    Assert-Equal '登録表示 [正常]: 通知する'           $p.Notify $true
    Assert-Equal '登録表示 [正常]: 全行が旧実装と一致' (Format-Lines $p) (@(
        'Host:Hirake.exe を検出しました: C:\x\Hirake.exe'
        "Host:ProgId 'Hirake.md' を登録しました。"
        "Host:URL プロトコル 'hirake://' を登録しました。"
        "Host:拡張子 '.md' の OpenWithProgIds に 'Hirake.md' を登録しました。"
        "Host:'.md' の既定プログラムとして 'Hirake.md' を設定しました。"
        "Host:拡張子 '.markdown' の OpenWithProgIds に 'Hirake.md' を登録しました。"
        'Host:'
        'Host:===================================================================='
        'Host:Hirake の関連付け登録が完了しました。'
        'Host:Windows 11 では初回のみ、.md ファイルを右クリック →'
        'Host:「プログラムから開く」→「別のプログラムを選択」→'
        'Host:Hirake を選び「常にこのアプリを使う」にチェックを入れる操作が'
        'Host:必要な場合があります。'
        'Host:===================================================================='
    ) -join "`n")

    $existing = [pscustomobject]@{ Extension = '.md'; Succeeded = $true; Error = $null; IsDefault = $true; Written = $false; DefaultSet = $false; ExistingDefault = 'OtherApp.md' }
    $p = Get-RegisterPresentationFor (New-RegisterResult -Extensions @($existing))
    Assert-Equal '登録表示 [他アプリが既定]: 先頭 3 行' ((Format-Lines $p) -split "`n" | Select-Object -First 3 | Join-String -Separator "`n") (@(
        'Host:Hirake.exe を検出しました: C:\x\Hirake.exe'
        "Host:ProgId 'Hirake.md' を登録しました。"
        "Host:URL プロトコル 'hirake://' を登録しました。"
    ) -join "`n")
    Assert-Equal '登録表示 [他アプリが既定]: 既に登録済みの文言' ((Format-Lines $p) -split "`n")[3] "Host:拡張子 '.md' の OpenWithProgIds には 'Hirake.md' が既に登録されています。"
    Assert-Equal '登録表示 [他アプリが既定]: 上書きしない文言'   ((Format-Lines $p) -split "`n")[4] "Host:'.md' には既に既定プログラム 'OtherApp.md' が設定されているため、上書きしませんでした。"

    $emptyDefault = [pscustomobject]@{ Extension = '.md'; Succeeded = $true; Error = $null; IsDefault = $true; Written = $true; DefaultSet = $false; ExistingDefault = '' }
    $p = Get-RegisterPresentationFor (New-RegisterResult -Extensions @($emptyDefault))
    Assert-Equal '登録表示 [既定値が空文字]: 専用の文言' ((Format-Lines $p) -split "`n")[4] "Host:'.md' には既定プログラムとして空の値が設定されているため、上書きしませんでした。"

    $ng = [pscustomobject]@{ Extension = '.markdown'; Succeeded = $false; Error = 'boom'; IsDefault = $false; Written = $false; DefaultSet = $false; ExistingDefault = $null }
    $p = Get-RegisterPresentationFor (New-RegisterResult -Status 'CompletedWithFailures' -FailureCount 1 -Extensions @($ok, $ng))
    Assert-Equal '登録表示 [一部失敗]: 終了コード'     $p.ExitCode 1
    Assert-Equal '登録表示 [一部失敗]: 通知する'       $p.Notify $true
    Assert-Equal '登録表示 [一部失敗]: 全行が旧実装と一致' (Format-Lines $p) (@(
        'Host:Hirake.exe を検出しました: C:\x\Hirake.exe'
        "Host:ProgId 'Hirake.md' を登録しました。"
        "Host:URL プロトコル 'hirake://' を登録しました。"
        "Host:拡張子 '.md' の OpenWithProgIds に 'Hirake.md' を登録しました。"
        "Host:'.md' の既定プログラムとして 'Hirake.md' を設定しました。"
        "Warning:拡張子 '.markdown' の登録に失敗しました: boom"
        'Host:'
        'Warning:1 件の拡張子で登録に失敗しました。上記の警告を確認してください。'
    ) -join "`n")

    function New-UnregisterResult {
        param([string]$Status = 'Completed', [int]$FailureCount = 0, $Extensions = @(), $Targets = @(),
              [bool]$SkippedTargets = $false, [string]$AbortedTarget)
        return [pscustomobject]@{
            Status = $Status; AbortedTarget = $AbortedTarget; Extensions = $Extensions
            Targets = $Targets; SkippedTargets = $SkippedTargets; FailureCount = $FailureCount
        }
    }

    function Get-UnregisterPresentationFor {
        param($Result)
        return Get-HirakeUnregisterPresentation -Result $Result -ProgId 'Hirake.md' -SchemeKey 'HKCU:\Software\Classes\hirake'
    }

    $p = Get-UnregisterPresentationFor (New-UnregisterResult -Status 'AbortedUnknown' -AbortedTarget 'HKCU:\Software\Classes\Hirake.md')
    Assert-Equal '解除表示 [Unknown]: 終了コード'      $p.ExitCode 1
    Assert-Equal '解除表示 [Unknown]: 通知しない'      $p.Notify $false
    Assert-Equal '解除表示 [Unknown]: register の案内' (@($p.Lines)[0].Message -like '*register.ps1 を実行して登録を修復*') $true

    $extOk = [pscustomobject]@{ Extension = '.md'; Succeeded = $true; Error = $null; RemovedProgId = $true; RemovedOpenWithKey = $true; ExistingDefault = 'Hirake.md'; RemovedDefault = $true; RemovedExtKey = $true }
    $extNg = [pscustomobject]@{ Extension = '.markdown'; Succeeded = $false; Error = 'boom'; RemovedProgId = $false; RemovedOpenWithKey = $false; ExistingDefault = $null; RemovedDefault = $false; RemovedExtKey = $false }
    $p = Get-UnregisterPresentationFor (New-UnregisterResult -Status 'CompletedWithFailures' -FailureCount 1 -Extensions @($extOk, $extNg) -SkippedTargets $true)
    Assert-Equal '解除表示 [見送り]: 終了コード'       $p.ExitCode 1
    Assert-Equal '解除表示 [見送り]: 見送りの警告'     @(@($p.Lines) | Where-Object { $_.Message -like '*削除しません（拡張子側に参照が残るため）*' }).Count 1
    Assert-Equal '解除表示 [見送り]: 完了バナーなし'   @(@($p.Lines) | Where-Object { $_.Message -eq 'Hirake の関連付け解除が完了しました。' }).Count 0

    $targets = @(
        [pscustomobject]@{ Path = 'HKCU:\Software\Classes\Hirake.md'; Status = 'Removed'; Error = $null },
        [pscustomobject]@{ Path = 'HKCU:\Software\Classes\hirake'; Status = 'NotFound'; Error = $null })
    $p = Get-UnregisterPresentationFor (New-UnregisterResult -Extensions @($extOk) -Targets $targets)
    Assert-Equal '解除表示 [正常]: 終了コード'         $p.ExitCode 0
    Assert-Equal '解除表示 [正常]: 通知する'           $p.Notify $true
    Assert-Equal '解除表示 [正常]: 全行が旧実装と一致' (Format-Lines $p) (@(
        "Host:拡張子 '.md' の OpenWithProgIds から 'Hirake.md' を削除しました。"
        "Host:'.md' の既定プログラム設定（'Hirake.md'）を削除しました。"
        "Host:空になった拡張子キー '.md' を削除しました。"
        "Host:ProgId 'Hirake.md' を削除しました。"
        "Host:URL プロトコル 'hirake://' は登録されていませんでした。"
        'Host:'
        'Host:===================================================================='
        'Host:Hirake の関連付け解除が完了しました。'
        'Host:===================================================================='
    ) -join "`n")

    # 他アプリが既定のまま残るケース（削除しない旨を出す）
    $extOther = [pscustomobject]@{ Extension = '.md'; Succeeded = $true; Error = $null; RemovedProgId = $true; RemovedOpenWithKey = $false; ExistingDefault = 'OtherApp.md'; RemovedDefault = $false; RemovedExtKey = $false }
    $p = Get-UnregisterPresentationFor (New-UnregisterResult -Extensions @($extOther) -Targets $targets)
    Assert-Equal '解除表示 [他アプリが既定]: 先頭 2 行' ((Format-Lines $p) -split "`n" | Select-Object -First 2 | Join-String -Separator "`n") (@(
        "Host:拡張子 '.md' の OpenWithProgIds から 'Hirake.md' を削除しました。"
        "Host:'.md' の既定プログラムは 'OtherApp.md' のままです（Hirake が設定したものではないため削除しません）。"
    ) -join "`n")

    # ---- Write-HirakePresentation のストリーム振り分け ----------------

    Write-Host ''
    Write-Host 'ストリーム振り分け'

    $sample = [pscustomobject]@{
        Lines = @(
            (New-PresentationLine -Stream Host -Message 'zzhost-line'),
            (New-PresentationLine -Stream Warning -Message 'zzwarning-line'),
            (New-PresentationLine -Stream Error -Message 'zzerror-line'))
        ExitCode = 0
        Notify = $true
    }

    # Write-Error は $ErrorActionPreference='Stop' 下では終端例外になるため、
    # この検証の間だけ Continue に戻す（本番の挙動を変えるものではない）。
    $savedErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $hostOut  = (Write-HirakePresentation -Presentation $sample 6>&1 3>$null 2>$null | Out-String)
        $warnOut  = (Write-HirakePresentation -Presentation $sample 3>&1 6>$null 2>$null | Out-String)
        $errorOut = (Write-HirakePresentation -Presentation $sample 2>&1 6>$null 3>$null | Out-String)
    } finally {
        $ErrorActionPreference = $savedErrorAction
    }

    # 3 つのストリームそれぞれについて、対象の行だけがあり他は混ざらないことを見る。
    foreach ($case in @(
            @{ Name = 'Host';    Out = $hostOut;  Expect = 'zzhost-line';    Others = @('zzwarning-line', 'zzerror-line') },
            @{ Name = 'Warning'; Out = $warnOut;  Expect = 'zzwarning-line'; Others = @('zzhost-line', 'zzerror-line') },
            @{ Name = 'Error';   Out = $errorOut; Expect = 'zzerror-line';   Others = @('zzhost-line', 'zzwarning-line') })) {
        Assert-Equal ("ストリーム [" + $case.Name + "]: 対象の行がある") ($case.Out -match $case.Expect) $true
        foreach ($other in $case.Others) {
            Assert-Equal ("ストリーム [" + $case.Name + "]: " + $other + " は混ざらない") ($case.Out -match $other) $false
        }
    }

    $threw = $false
    try {
        Invoke-HirakeRegister -ExePath $testExePath -ProgId $testProgId -SchemeKey $testSchemeKey `
            -Extensions @($testExt) -DefaultExtension $testExt2 -ExpectedExeName $testExeName
    } catch { $threw = $true }
    Assert-Equal 'Extensions に無い DefaultExtension を拒否' $threw $true

    # 拡張子 1 件でも配列として扱えること（索引・件数で参照するため）。
    Reset-Sandbox
    $single = Invoke-TestRegister -Extensions @($testExt)
    Assert-Equal '拡張子 1 件: 件数を取れる'   $single.Extensions.Count 1
    Assert-Equal '拡張子 1 件: 索引で取れる'   $single.Extensions[0].Extension $testExt
    $single = Invoke-TestUnregister -Extensions @($testExt)
    Assert-Equal '拡張子 1 件: 解除も配列'     $single.Extensions.Count 1
    Assert-Equal '拡張子 1 件: Targets も配列' $single.Targets.Count 2
    # ---- 失敗注入（関数シャドウイング） -----------------------------
    #
    # 「1 件目の拡張子だけ失敗したら、残りは続けるが ProgId とスキームは消さない」
    # といった経路は、失敗を起こさないと通らない。実際に権限エラーを起こす方法
    # （Deny ACE）は後片付けが壊れやすく、中断時にユーザーが手で直せないキーを
    # 残すため採らない。
    #
    # 代わりに、lib を dot-source した後で同名の関数を定義して差し替える。
    # PowerShell は呼び出し時に名前を解決するため、Invoke-Hirake* からの呼び出しが
    # こちらへ向く。本番コードにテスト用の分岐を一切入れずに済む。
    #
    # 差し替えた関数はこのスクリプトのスコープにのみ存在する。以降の実レジストリを
    # 使うテストはこのブロックより前で終えている。

    Write-Host ''
    Write-Host '失敗注入'
    Reset-Sandbox

    # 差し替えた関数は最後に必ず戻す。戻さないと、このブロックより後ろに
    # テストを足したときに、実関数を呼んでいるつもりでモックが呼ばれる。
    $originalFunctions = @{}
    foreach ($name in @('Register-HirakeExtension', 'Unregister-HirakeExtension',
                        'Remove-HirakeOwnedKey', 'Test-HirakeOwnedKey')) {
        $originalFunctions[$name] = (Get-Item "Function:\$name").ScriptBlock
    }

    # この区間で予期しない例外が起きても復元されるよう、finally からも呼べる形にする。
    $restoreShadows = {
        foreach ($name in @($originalFunctions.Keys)) {
            Set-Item -Path "Function:\$name" -Value $originalFunctions[$name]
        }
    }

    $script:failNext = @{}

    function Register-HirakeExtension {
        param([string]$Extension, [string]$ProgId, [switch]$SetAsDefault)
        if ($script:failNext.ContainsKey($Extension)) {
            throw "injected failure for $Extension"
        }
        return [pscustomobject]@{ Written = $true; DefaultSet = $SetAsDefault.IsPresent; ExistingDefault = $null }
    }

    function Unregister-HirakeExtension {
        param([string]$Extension, [string]$ProgId)
        if ($script:failNext.ContainsKey($Extension)) {
            throw "injected failure for $Extension"
        }
        return [pscustomobject]@{
            RemovedProgId = $true; RemovedOpenWithKey = $true; ExistingDefault = $null
            RemovedDefault = $true; RemovedExtKey = $true
        }
    }

    $script:removedTargets = @()
    function Remove-HirakeOwnedKey {
        param([string]$Path, [string]$ExeName)
        $script:removedTargets += $Path
        return 'Removed'
    }

    function Test-HirakeOwnedKey {
        param([string]$Path, [string]$ExeName)
        return 'Owned'
    }

    # 登録: 1 件目だけ失敗しても 2 件目を続ける
    $script:failNext = @{ $testExt = $true }
    $result = Invoke-TestRegister
    Assert-Equal '注入 [登録 1 件目失敗]: Status'       $result.Status 'CompletedWithFailures'
    Assert-Equal '注入 [登録 1 件目失敗]: 失敗 1 件'    $result.FailureCount 1
    Assert-Equal '注入 [登録 1 件目失敗]: 2 件処理した' @($result.Extensions).Count 2
    Assert-Equal '注入 [登録 1 件目失敗]: 1 件目が失敗' @($result.Extensions)[0].Succeeded $false
    Assert-Equal '注入 [登録 1 件目失敗]: 2 件目は成功' @($result.Extensions)[1].Succeeded $true
    Assert-Equal '注入 [登録 1 件目失敗]: コアは書けた' $result.ProgIdWritten $true

    # 登録: 2 件目だけ失敗
    $script:failNext = @{ $testExt2 = $true }
    $result = Invoke-TestRegister
    Assert-Equal '注入 [登録 2 件目失敗]: 失敗 1 件'    $result.FailureCount 1
    Assert-Equal '注入 [登録 2 件目失敗]: 1 件目は成功' @($result.Extensions)[0].Succeeded $true
    Assert-Equal '注入 [登録 2 件目失敗]: 2 件目が失敗' @($result.Extensions)[1].Succeeded $false

    # 解除: 拡張子が失敗したら ProgId とスキームを消さない（ダングリング防止）
    $script:failNext = @{ $testExt = $true }
    $script:removedTargets = @()
    $result = Invoke-TestUnregister
    Assert-Equal '注入 [解除 1 件目失敗]: Status'         $result.Status 'CompletedWithFailures'
    Assert-Equal '注入 [解除 1 件目失敗]: 見送った'       $result.SkippedTargets $true
    Assert-Equal '注入 [解除 1 件目失敗]: コアを消さない' @($script:removedTargets).Count 0
    Assert-Equal '注入 [解除 1 件目失敗]: Targets は空'   @($result.Targets).Count 0
    Assert-Equal '注入 [解除 1 件目失敗]: 2 件目も処理'   @($result.Extensions).Count 2

    # 解除: 拡張子がすべて成功したらコアを 2 つとも消す
    $script:failNext = @{}
    $script:removedTargets = @()
    $result = Invoke-TestUnregister
    Assert-Equal '注入 [解除 正常]: Status'           $result.Status 'Completed'
    Assert-Equal '注入 [解除 正常]: 見送らない'       $result.SkippedTargets $false
    Assert-Equal '注入 [解除 正常]: コアを 2 つ削除'  @($script:removedTargets).Count 2
    Assert-Equal '注入 [解除 正常]: ProgId が先'      @($script:removedTargets)[0] $testProgIdKey
    Assert-Equal '注入 [解除 正常]: スキームが後'     @($script:removedTargets)[1] $testSchemeKey

    # 解除: コア削除が Unknown / NotOwned を返したら失敗として数える
    function Remove-HirakeOwnedKey {
        param([string]$Path, [string]$ExeName)
        if ($Path -eq $testSchemeKey) { return 'Unknown' }
        return 'Removed'
    }
    $result = Invoke-TestUnregister
    Assert-Equal '注入 [コア削除 Unknown]: Status'    $result.Status 'CompletedWithFailures'
    Assert-Equal '注入 [コア削除 Unknown]: 失敗 1 件' $result.FailureCount 1
    Assert-Equal '注入 [コア削除 Unknown]: もう片方は処理する' @($result.Targets).Count 2

    # 解除: 片方のコア削除が例外でも、もう片方の削除を続ける
    function Remove-HirakeOwnedKey {
        param([string]$Path, [string]$ExeName)
        if ($Path -eq $testProgIdKey) { throw 'injected core failure' }
        return 'Removed'
    }
    $result = Invoke-TestUnregister
    Assert-Equal '注入 [コア削除 例外]: 失敗 1 件'      $result.FailureCount 1
    Assert-Equal '注入 [コア削除 例外]: 2 件とも記録'   @($result.Targets).Count 2
    Assert-Equal '注入 [コア削除 例外]: 1 件目は Error' @($result.Targets)[0].Status 'Error'
    Assert-Equal '注入 [コア削除 例外]: 2 件目は削除'   @($result.Targets)[1].Status 'Removed'

    # ここまでが失敗注入。以降のテストが誤ってモックを呼ばないよう必ず戻す。
    # （restoreShadows は finally でも呼ばれる。二重に呼んでも同じ結果になる）
    & $restoreShadows

    foreach ($name in @($originalFunctions.Keys)) {
        Assert-Equal ("注入後: " + $name + " が復元されている") `
            ((Get-Item "Function:\$name").ScriptBlock.ToString() -eq $originalFunctions[$name].ToString()) $true
    }
    Assert-Equal '注入後: 実関数として動作する' `
        (Test-HirakeOwnedKey -Path "HKCU:\Software\Classes\ZZHirakeNotExist$runId" -ExeName 'x.exe') 'NotFound'

    # ---- スクリプトの配線（AST） ------------------------------------
    #
    # 表示の組み立てと終了コードは純粋関数として検証したが、それを
    # 「呼び出して・通知して・その終了コードで終わる」配線そのものは
    # スクリプト本体にしかない。ここが崩れると全アサーションが通ったまま
    # 実挙動が壊れるため、構文木で最低限の配線を確認する。

    Write-Host ''
    Write-Host 'スクリプトの配線'

    foreach ($script in @(
            @{ Name = 'register.ps1';   Invoke = 'Invoke-HirakeRegister';   Present = 'Get-HirakeRegisterPresentation' },
            @{ Name = 'unregister.ps1'; Invoke = 'Invoke-HirakeUnregister'; Present = 'Get-HirakeUnregisterPresentation' })) {

        $path = Join-Path $PSScriptRoot "..\scripts\$($script.Name)"
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        Assert-Equal ("配線 [" + $script.Name + "]: 構文エラーなし") @($errors).Count 0

        # 名前の存在と出現順だけを見ると、到達不能な位置に置く・別の変数へ代入する・
        # 条件を反転する、といった書き方で検査を通せてしまう。データの流れを構造で見る。
        $prefix = "配線 [" + $script.Name + "]"

        # トップレベルの文だけを対象にする（関数定義や if の中に隠せないように）。
        $topLevel = @($ast.EndBlock.Statements)

        # 代入は「右辺のパイプラインが対象コマンドただ 1 つ」であることまで見る。
        # 最初の CommandAst の名前だけを見ると、結果を捨てるパイプ（| ForEach-Object）を
        # 挟んでも通ってしまう。
        function Get-SingleCommandAssignment {
            param($Statements, [string]$VariableName)

            $matches = @($Statements | Where-Object {
                $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $_.Left.Extent.Text -eq $VariableName
            })

            return [pscustomobject]@{
                Count      = $matches.Count
                Statement  = if ($matches.Count -eq 1) { $matches[0] } else { $null }
                CommandName = if ($matches.Count -eq 1 -and
                                  $matches[0].Right -is [System.Management.Automation.Language.CommandExpressionAst] -eq $false -and
                                  $matches[0].Right.PipelineElements.Count -eq 1 -and
                                  $matches[0].Right.PipelineElements[0] -is [System.Management.Automation.Language.CommandAst]) {
                    $matches[0].Right.PipelineElements[0].GetCommandName()
                } else { $null }
            }
        }

        $resultInfo = Get-SingleCommandAssignment -Statements $topLevel -VariableName '$result'
        Assert-Equal "$prefix`: `$result への代入はちょうど 1 つ" $resultInfo.Count 1
        Assert-Equal "$prefix`: `$result は制御フローの戻り値そのもの" $resultInfo.CommandName $script.Invoke

        $presentInfo = Get-SingleCommandAssignment -Statements $topLevel -VariableName '$presentation'
        Assert-Equal "$prefix`: `$presentation への代入はちょうど 1 つ" $presentInfo.Count 1
        Assert-Equal "$prefix`: `$presentation は組み立ての戻り値そのもの" $presentInfo.CommandName $script.Present

        $resultAssign = $resultInfo.Statement
        $presentAssign = $presentInfo.Statement

        # 変数を後から書き換える経路も塞ぐ。
        Assert-Equal "$prefix`: Set-Variable / Remove-Variable を使わない" `
            ($ast.Extent.Text -match '(Set|Remove)-Variable') $false

        # 組み立てには制御フローの結果そのものを渡すこと。
        Assert-Equal "$prefix`: 組み立てに -Result `$result を渡す" `
            ($presentAssign.Right.Extent.Text -match '-Result\s+\$result\b') $true

        # 書き出しはトップレベルのコマンドで、組み立て結果そのものを渡すこと。
        $writeStmt = $topLevel | Where-Object {
            $_ -is [System.Management.Automation.Language.PipelineAst] -and
            $_.PipelineElements.Count -eq 1 -and
            $_.PipelineElements[0] -is [System.Management.Automation.Language.CommandAst] -and
            $_.PipelineElements[0].GetCommandName() -eq 'Write-HirakePresentation'
        } | Select-Object -First 1
        Assert-Equal "$prefix`: 書き出しはトップレベルにある" ($null -ne $writeStmt) $true
        Assert-Equal "$prefix`: 書き出しに -Presentation `$presentation を渡す" `
            ($writeStmt.Extent.Text -match '-Presentation\s+\$presentation\b') $true

        # 通知は、否定されていない $presentation.Notify の if で守られていること。
        $notifyIf = $topLevel | Where-Object {
            $_ -is [System.Management.Automation.Language.IfStatementAst] -and
            $_.Extent.Text -match 'Send-ShellChangeNotification'
        } | Select-Object -First 1
        Assert-Equal "$prefix`: 通知はトップレベルの if で守られている" ($null -ne $notifyIf) $true
        Assert-Equal "$prefix`: 条件は `$presentation.Notify そのもの" `
            ($notifyIf.Clauses[0].Item1.Extent.Text.Trim()) '$presentation.Notify'

        # if の中身が通知 1 文だけであること（さらに内側の if で到達不能にできないように）。
        $notifyBody = @($notifyIf.Clauses[0].Item2.Statements)
        Assert-Equal "$prefix`: 通知の if は 1 文だけ" $notifyBody.Count 1

        $notifyCommandName = $null
        if ($notifyBody.Count -eq 1 -and
            $notifyBody[0] -is [System.Management.Automation.Language.PipelineAst] -and
            $notifyBody[0].PipelineElements.Count -eq 1 -and
            $notifyBody[0].PipelineElements[0] -is [System.Management.Automation.Language.CommandAst]) {
            $notifyCommandName = $notifyBody[0].PipelineElements[0].GetCommandName()
        }
        Assert-Equal "$prefix`: その 1 文が通知そのもの" $notifyCommandName 'Send-ShellChangeNotification'

        # exit はトップレベルにちょうど 1 つだけ、かつ最後の文であること。
        # 早い位置に exit 0 を置かれると、それより後ろは実行されない。
        $topLevelExits = @($topLevel | Where-Object {
            $_.Extent.Text -match '^\s*exit\b'
        })
        Assert-Equal "$prefix`: トップレベルの exit はちょうど 1 つ" $topLevelExits.Count 1
        $exitStmt = $topLevelExits[0]
        Assert-Equal "$prefix`: exit は `$presentation.ExitCode を使う" `
            ($exitStmt.Extent.Text.Trim()) 'exit $presentation.ExitCode'
        Assert-Equal "$prefix`: exit が最後の文" `
            ([object]::ReferenceEquals($topLevel[$topLevel.Count - 1], $exitStmt)) $true

        # 実行順序も構造で見る（トップレベルでの位置関係）。
        $indexOfStatement = {
            param($target)
            for ($i = 0; $i -lt $topLevel.Count; $i++) {
                if ([object]::ReferenceEquals($topLevel[$i], $target)) { return $i }
            }
            return -1
        }
        $resultAssign = $topLevel | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$result'
        } | Select-Object -First 1

        Assert-Equal "$prefix`: 組み立ては制御フローの後" `
            ((& $indexOfStatement $presentAssign) -gt (& $indexOfStatement $resultAssign)) $true
        Assert-Equal "$prefix`: 書き出しは組み立ての後" `
            ((& $indexOfStatement $writeStmt) -gt (& $indexOfStatement $presentAssign)) $true
        Assert-Equal "$prefix`: 通知は書き出しの後" `
            ((& $indexOfStatement $notifyIf) -gt (& $indexOfStatement $writeStmt)) $true
        Assert-Equal "$prefix`: exit は通知の後" `
            ((& $indexOfStatement $exitStmt) -gt (& $indexOfStatement $notifyIf)) $true
    }
} finally {
    # 後片付けは 3 つとも独立に試みる。1 つが失敗しても残りを実行し、
    # 失敗はまとめて報告する（握りつぶすと後片付け漏れに気づけない）。
    $cleanupErrors = @()

    if ($null -ne $restoreShadows) {
        try { & $restoreShadows } catch { $cleanupErrors += "シャドウ関数の復元: $($_.Exception.Message)" }
    }
    try { Reset-Sandbox } catch { $cleanupErrors += "レジストリの後片付け: $($_.Exception.Message)" }
    try {
        if (Test-Path -LiteralPath $tempDir) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force
        }
    } catch { $cleanupErrors += "一時フォルダの削除: $($_.Exception.Message)" }

    foreach ($cleanupError in $cleanupErrors) {
        Write-Host ("  FAIL 後片付けに失敗しました - " + $cleanupError)
        $script:failed++
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
