#requires -Version 5.1
<#
.SYNOPSIS
    Hirake を .md / .markdown ファイルの「プログラムから開く」候補として HKCU に登録します。
    管理者権限は不要です（HKCU のみを使用）。

    他アプリが既に設定している .md の既定プログラムや OpenWithProgIds のエントリは
    変更しません。unregister.ps1 で現時点の Hirake 関連付けを解除できますが、
    実行前のレジストリ状態を復元するものではありません。

    失敗時は終了コード 1 で終了します。値ごとのロールバックは行わないため、
    その時点で一部の変更が適用済みのことがあります。原因を解消してから
    再実行してください（何度実行しても同じ結果になります）。

.PARAMETER ExePath
    Hirake.exe への明示パス。ファイル名は 'Hirake.exe' である必要があります
    （unregister.ps1 がこの名前を手掛かりに Hirake の登録かどうかを判定するため）。
    省略時は以下の順で自動検出します。
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

# レジストリ操作は unregister.ps1 と共通のヘルパーに集約している。
$libPath = Join-Path $PSScriptRoot 'association-lib.ps1'
if (-not (Test-Path -LiteralPath $libPath -PathType Leaf)) {
    Write-Error "共通スクリプトが見つかりません: $libPath"
    exit 1
}
. $libPath

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

$resolvedExePath = Resolve-HirakeExePath -Explicit $ExePath

# unregister.ps1 は shell\open\command の実行ファイル名が 'Hirake.exe' であることを
# 手掛かりに「Hirake が登録したキーか」を判定する。別名の exe を登録できてしまうと
# 解除できない状態を作れるため、ここで弾く。
$exeName = 'Hirake.exe'
if ((Split-Path -Path $resolvedExePath -Leaf) -ne $exeName) {
    Write-Error "'$exeName' 以外の実行ファイルは登録できません（unregister.ps1 が解除できなくなるため）: $resolvedExePath"
    exit 1
}

Write-Host "Hirake.exe を検出しました: $resolvedExePath"

$progId = 'Hirake.md'
$progIdKey = "HKCU:\Software\Classes\$progId"
$schemeKey = 'HKCU:\Software\Classes\hirake'
$openCommand = "`"$resolvedExePath`" `"%1`""

# 1. Hirake 名義のキーが別アプリに使われていないか先に確認する。
#    使われていた場合は 1 つも書き換えずに中止する（奪い取らない）。
foreach ($target in @(
        @{ Path = $progIdKey; Label = "ProgId '$progId'" },
        @{ Path = $schemeKey; Label = "URL プロトコル 'hirake://'" })) {
    if ((Test-HirakeOwnedKey -Path $target.Path -ExeName $exeName) -eq 'NotOwned') {
        Write-Error "$($target.Label) のキーは Hirake 以外のプログラムが使用しているため、登録を中止しました: $($target.Path)"
        exit 1
    }
}

# 2. ProgId と URL プロトコルの登録
#    キーは作り直さず、必要な値だけを上書きする（削除→再作成の間に失敗すると
#    元の設定が失われ、ユーザーが追加した verb なども巻き添えになるため）。
#    ここで失敗した場合は拡張子側に手を付けず中止する（参照先の無い
#    OpenWithProgIds エントリを作らないため）。
try {
    Initialize-RegistryKey -Path $progIdKey
    Set-ItemProperty -Path $progIdKey -Name '(default)' -Value 'Markdown Document'

    Initialize-RegistryKey -Path "$progIdKey\DefaultIcon"
    Set-ItemProperty -Path "$progIdKey\DefaultIcon" -Name '(default)' -Value "$resolvedExePath,0"

    Initialize-RegistryKey -Path "$progIdKey\shell\open\command"
    Set-ItemProperty -Path "$progIdKey\shell\open\command" -Name '(default)' -Value $openCommand

    Write-Host "ProgId '$progId' を登録しました。"

    # 'URL Protocol' は値が空文字のまま「プロパティが存在すること」に意味がある。
    Initialize-RegistryKey -Path $schemeKey
    Set-ItemProperty -Path $schemeKey -Name '(default)' -Value 'URL:Hirake Protocol'
    New-ItemProperty -Path $schemeKey -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null

    Initialize-RegistryKey -Path "$schemeKey\DefaultIcon"
    Set-ItemProperty -Path "$schemeKey\DefaultIcon" -Name '(default)' -Value "$resolvedExePath,0"

    Initialize-RegistryKey -Path "$schemeKey\shell\open\command"
    Set-ItemProperty -Path "$schemeKey\shell\open\command" -Name '(default)' -Value $openCommand

    Write-Host "URL プロトコル 'hirake://' を登録しました。"
} catch {
    Write-Warning "ProgId / URL プロトコルの登録に失敗しました: $($_.Exception.Message)"
    Write-Warning '一部だけ書き込まれている可能性があります。原因を解消してから register.ps1 を再実行してください。'
    exit 1
}

# 3. .md / .markdown の登録
#    ☆共有キーなので作り直さず、自分のエントリだけを足す。
#    .md のみ、既定プログラムが未設定の場合に限り Hirake を設定する。
$failures = 0

foreach ($ext in @('.md', '.markdown')) {
    $setAsDefault = ($ext -eq '.md')

    try {
        $result = Register-HirakeExtension -Extension $ext -ProgId $progId -SetAsDefault:$setAsDefault

        if ($result.Written) {
            Write-Host "拡張子 '$ext' の OpenWithProgIds に '$progId' を登録しました。"
        } else {
            Write-Host "拡張子 '$ext' の OpenWithProgIds には '$progId' が既に登録されています。"
        }

        if ($setAsDefault) {
            if ($result.DefaultSet) {
                Write-Host "'$ext' の既定プログラムとして '$progId' を設定しました。"
            } elseif ($result.ExistingDefault -eq '') {
                Write-Host "'$ext' には既定プログラムとして空の値が設定されているため、上書きしませんでした。"
            } else {
                Write-Host "'$ext' には既に既定プログラム '$($result.ExistingDefault)' が設定されているため、上書きしませんでした。"
            }
        }
    } catch {
        $failures++
        Write-Warning "拡張子 '$ext' の登録に失敗しました: $($_.Exception.Message)"
    }
}

# 4. エクスプローラーへの反映通知
Send-ShellChangeNotification

if ($failures -gt 0) {
    Write-Host ''
    Write-Warning "$failures 件の拡張子で登録に失敗しました。上記の警告を確認してください。"
    exit 1
}

Write-Host ''
Write-Host '===================================================================='
Write-Host 'Hirake の関連付け登録が完了しました。'
Write-Host 'Windows 11 では初回のみ、.md ファイルを右クリック →'
Write-Host '「プログラムから開く」→「別のプログラムを選択」→'
Write-Host 'Hirake を選び「常にこのアプリを使う」にチェックを入れる操作が'
Write-Host '必要な場合があります。'
Write-Host '===================================================================='
