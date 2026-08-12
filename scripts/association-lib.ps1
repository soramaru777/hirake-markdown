#requires -Version 5.1
<#
.SYNOPSIS
    register.ps1 / unregister.ps1 が共用する、HKCU のレジストリ操作ヘルパー。

.DESCRIPTION
    このファイルは単体では何もしない。呼び出し側から dot-source して使う。

        . (Join-Path $PSScriptRoot 'association-lib.ps1')

    呼び出し側が `Set-StrictMode -Version Latest` と `$ErrorActionPreference = 'Stop'`
    を設定している前提で書かれている（関数は呼び出し側のスコープで動くため設定を引き継ぐ）。
    操作に失敗した場合は例外を投げるので、呼び出し側で try/catch して報告すること。
    「実際に何が起きたか」は戻り値で返し、メッセージは一切表示しない。

.NOTES
    ■ キーの所有権による使い分け（これがこのファイルの設計の軸）

      ★Hirake 専用キー（Hirake.md / hirake）
          Hirake が所有するキーだが、同名キーを別アプリや旧バージョンが使っている
          可能性は否定できない。登録時は削除せず Initialize-RegistryKey で用意して
          値だけを上書きし、削除時は Remove-HirakeOwnedKey で「Hirake の exe を
          指しているか」を確認してから消す。

      ☆共有キー（.md / .markdown / OpenWithProgIds）
          他アプリや OS のデータが同居する。作り直すと巻き添えで消えるため、
          Initialize-RegistryKey（無い時だけ作る）を使い、値は 1 つずつ操作する。

    どちらの分類でも「キーを作り直す」操作は行わない。削除と再作成の間に失敗すると
    元の設定が失われ、ロールバックできないため。

    ■ PowerShell のレジストリプロバイダに関する注意（実測で確認した挙動）

      - 既存キーへの New-Item -Force は、キーを削除して再作成する（サブキーも値も消える）。
      - 一方、親が存在しないパスへの New-Item は -Force が無いと失敗する。
        つまり -Force は「中間キーをまとめて作る」ためだけに必要であり、
        Test-Path による存在チェックと必ず併用しなければならない。
      - Remove-ItemProperty -Name '(default)' は既定値には使えず PSArgumentException になる。
        既定値の削除は .NET の RegistryKey.DeleteValue('') で行う必要がある。
      - Get-ItemProperty -Name '(default)' は値が未設定のとき例外になるため、
        「値が無い」と「空文字が入っている」の区別には GetValueNames() を使う。
#>

# HKCU:\... 形式のパスを .NET 用の相対サブキー名へ変換する。
# HKCU 以外が渡されたら例外にする（他ハイブを誤って操作しないための安全弁）。
function ConvertTo-HkcuSubKeyPath {
    param([Parameter(Mandatory)][string]$Path)

    if ($Path -notmatch '^HKCU:\\') {
        throw "HKCU 配下のパスではありません: $Path"
    }

    return $Path.Substring('HKCU:\'.Length)
}

# 読み取り専用 / 書き込み可でレジストリキーを開く。存在しなければ $null。
function Open-HkcuSubKey {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$Writable
    )

    $subKeyPath = ConvertTo-HkcuSubKeyPath -Path $Path
    return [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKeyPath, [bool]$Writable)
}

# 指定した値名が存在するか。既定値を調べる場合は -Name '' を渡す。
function Test-RegistryValueName {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name
    )

    $key = Open-HkcuSubKey -Path $Path
    if ($null -eq $key) {
        return $false
    }

    try {
        return ($key.GetValueNames() -contains $Name)
    } finally {
        $key.Close()
    }
}

# ☆共有キー用。キーが無ければ作る。既にあれば一切触らない。
# 親キーを再帰的に用意してから自分を作るため、New-Item に -Force を渡さない。
# （-Force は「既存キーなら作り直す」意味も併せ持つため、共有キーには一切使わない）
function Initialize-RegistryKey {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        return
    }

    $parent = Split-Path -Path $Path -Parent
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        Initialize-RegistryKey -Path $parent
    }

    New-Item -Path $Path | Out-Null
}

# command 文字列の先頭にある実行ファイルのファイル名を取り出す。
#   '"C:\a b\Hirake.exe" "%1"' -> 'Hirake.exe'
#   'C:\a\Hirake.exe %1'       -> 'Hirake.exe'
# 取り出せなければ $null。部分一致だと 'FakeHirake.exe.bak' のような文字列も
# 通ってしまうため、必ずファイル名を切り出してから完全一致で比較する。
function Get-CommandExecutableName {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$CommandLine)

    $line = $CommandLine.Trim()
    if (-not $line) {
        return $null
    }

    if ($line.StartsWith('"')) {
        $end = $line.IndexOf('"', 1)
        if ($end -lt 0) {
            return $null
        }
        # 閉じ引用符の直後に非空白が続く場合、Windows はそれも第 1 引数として連結する
        # （"C:\x\Hirake.exe".bak → C:\x\Hirake.exe.bak）。引用符の中だけを見ると
        # 別の実行ファイルを Hirake だと誤判定するため、曖昧な形式は判定不能とする。
        if (($end + 1) -lt $line.Length -and $line[$end + 1] -notmatch '\s') {
            return $null
        }
        $exe = $line.Substring(1, $end - 1)
    } else {
        $exe = ($line -split '\s+', 2)[0]
    }

    if (-not $exe) {
        return $null
    }

    try {
        return Split-Path -Path $exe -Leaf
    } catch {
        return $null
    }
}

# Hirake 名義のキー（Hirake.md / hirake）を Hirake が使っているとみなせるか判定する。
# 戻り値:
#   'NotFound' キーが無い
#   'Owned'    shell\open\command が Hirake の exe を指している
#   'NotOwned' shell\open\command が別の exe を指している
#   'Unknown'  shell\open\command が無く、由来を判定できない
#
# 'Unknown' の扱いは登録側と解除側で意図的に分ける。
#   登録側: 自分の作りかけである可能性が高く、上書きすれば整合が取れるので許可する
#           （ここで拒否すると、途中で失敗した登録を修復する手段が無くなる）。
#   解除側: 由来不明のキーを再帰削除するのは危険なので削除しない。
#           修復したい場合は register.ps1 を実行してから unregister.ps1 を実行する。
function Test-HirakeOwnedKey {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExeName
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return 'NotFound'
    }

    $command = Get-RegistryDefaultValue -Path "$Path\shell\open\command"
    if ($null -eq $command) {
        return 'Unknown'
    }

    if ((Get-CommandExecutableName -CommandLine $command) -ne $ExeName) {
        return 'NotOwned'
    }

    return 'Owned'
}

# Hirake のものと判定できた場合だけ、キーを丸ごと削除する。
# 戻り値: 'Removed' / 'NotFound' / 'NotOwned' / 'Unknown'
function Remove-HirakeOwnedKey {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExeName
    )

    $status = Test-HirakeOwnedKey -Path $Path -ExeName $ExeName
    if ($status -ne 'Owned') {
        return $status
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
    return 'Removed'
}

# 既定値を 3 状態で返す。
#   値が存在しない -> $null / 空文字が入っている -> '' / それ以外 -> その文字列
function Get-RegistryDefaultValue {
    param([Parameter(Mandatory)][string]$Path)

    $key = Open-HkcuSubKey -Path $Path
    if ($null -eq $key) {
        return $null
    }

    try {
        if ($key.GetValueNames() -notcontains '') {
            return $null
        }
        return [string]$key.GetValue('', '')
    } finally {
        $key.Close()
    }
}

# 既定値を削除する。削除できたら $true、元から無ければ $false。
function Remove-RegistryDefaultValue {
    param([Parameter(Mandatory)][string]$Path)

    $key = Open-HkcuSubKey -Path $Path -Writable
    if ($null -eq $key) {
        return $false
    }

    try {
        if ($key.GetValueNames() -notcontains '') {
            return $false
        }
        $key.DeleteValue('', $false)
        return $true
    } finally {
        $key.Close()
    }
}

# 値もサブキーも無い場合だけキーを削除する。削除したら $true。
function Remove-RegistryKeyIfEmpty {
    param([Parameter(Mandatory)][string]$Path)

    $key = Open-HkcuSubKey -Path $Path
    if ($null -eq $key) {
        return $false
    }

    try {
        $valueCount = $key.ValueCount
        $subKeyCount = $key.SubKeyCount
    } finally {
        # ハンドルを開いたまま削除しないよう、判定が終わったら必ず閉じる。
        $key.Close()
    }

    if ($valueCount -ne 0 -or $subKeyCount -ne 0) {
        return $false
    }

    # 判定と削除の間に他プロセスがサブキーを追加した場合に巻き添えにしないよう、
    # サブキーがあると例外になる DeleteSubKey で削除する（Remove-Item -Recurse と違い
    # 中身ごと消してしまうことがない）。値の追加までは防げないため多層防御に留まる。
    $parent = Open-HkcuSubKey -Path (Split-Path -Path $Path -Parent) -Writable
    if ($null -eq $parent) {
        return $false
    }

    try {
        $parent.DeleteSubKey((Split-Path -Path $Path -Leaf), $false)
    } finally {
        $parent.Close()
    }

    return $true
}

# 拡張子 1 つ分の登録。
#   -SetAsDefault を付けた場合のみ、既定 ProgId が未設定のときに限り設定する。
# 戻り値: Written（実際に書き込んだか） / DefaultSet / ExistingDefault
function Register-HirakeExtension {
    param(
        # 生成するキーのパスに直接埋め込むため、書式を厳密に制限する。
        [Parameter(Mandatory)][ValidatePattern('^\.[A-Za-z0-9]+$')][string]$Extension,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$ProgId,
        [switch]$SetAsDefault
    )

    $extKey = "HKCU:\Software\Classes\$Extension"
    $openWithKey = "$extKey\OpenWithProgIds"

    # ☆共有キー。既にある場合は作り直さない（他アプリのエントリを巻き添えにしないため）。
    Initialize-RegistryKey -Path $extKey
    Initialize-RegistryKey -Path $openWithKey

    # OpenWithProgIds は「値名 = ProgId / 値 = 空文字」が既定の作法。他の値名には触れない。
    # 既に正しい形（REG_SZ の空文字）で登録されていれば書き込まない。
    # 再実行を完全な no-op にして、レポートする内容と実際の変更を一致させるため。
    $written = $true
    $key = Open-HkcuSubKey -Path $openWithKey
    if ($null -ne $key) {
        try {
            if ($key.GetValueNames() -contains $ProgId -and
                $key.GetValueKind($ProgId) -eq [Microsoft.Win32.RegistryValueKind]::String -and
                "$($key.GetValue($ProgId))" -eq '') {
                $written = $false
            }
        } finally {
            $key.Close()
        }
    }

    if ($written) {
        New-ItemProperty -Path $openWithKey -Name $ProgId -Value '' -PropertyType String -Force | Out-Null
    }

    $existingDefault = $null
    $defaultSet = $false

    if ($SetAsDefault) {
        $existingDefault = Get-RegistryDefaultValue -Path $extKey

        # 「値が存在しない」ときだけ設定する。空文字が入っている場合は他アプリが
        # 明示的に書いた設定とみなして尊重する（上書きすると解除時に元へ戻せないため）。
        if ($null -eq $existingDefault) {
            Set-ItemProperty -Path $extKey -Name '(default)' -Value $ProgId
            $defaultSet = $true
        }
    }

    return [pscustomobject]@{
        Written         = $written
        DefaultSet      = $defaultSet
        ExistingDefault = $existingDefault
    }
}

# 拡張子 1 つ分の解除。register で作られた空キーの掃除まで行う。
# 戻り値: RemovedProgId / RemovedOpenWithKey / ExistingDefault / RemovedDefault / RemovedExtKey
function Unregister-HirakeExtension {
    param(
        [Parameter(Mandatory)][ValidatePattern('^\.[A-Za-z0-9]+$')][string]$Extension,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$ProgId
    )

    $extKey = "HKCU:\Software\Classes\$Extension"
    $openWithKey = "$extKey\OpenWithProgIds"

    # 1. OpenWithProgIds から自分の ProgId だけを削除する。
    $removedProgId = $false
    if (Test-RegistryValueName -Path $openWithKey -Name $ProgId) {
        Remove-ItemProperty -Path $openWithKey -Name $ProgId
        $removedProgId = $true
    }

    # 2. 空になった場合のみ OpenWithProgIds キーを削除する。
    #    他アプリのエントリが残っていれば削除されない。
    $removedOpenWithKey = Remove-RegistryKeyIfEmpty -Path $openWithKey

    # 3. 既定 ProgId が自分のものである場合だけ削除する。
    $existingDefault = Get-RegistryDefaultValue -Path $extKey
    $removedDefault = $false
    if ($null -ne $existingDefault -and $existingDefault -eq $ProgId) {
        $removedDefault = Remove-RegistryDefaultValue -Path $extKey
    }

    # 4. 拡張子キー自体が空になった場合のみ削除する。
    #    register.ps1 が作った空キーだけが消え、OS や他アプリのサブキーがあれば残る。
    $removedExtKey = Remove-RegistryKeyIfEmpty -Path $extKey

    return [pscustomobject]@{
        RemovedProgId      = $removedProgId
        RemovedOpenWithKey = $removedOpenWithKey
        ExistingDefault    = $existingDefault
        RemovedDefault     = $removedDefault
        RemovedExtKey      = $removedExtKey
    }
}

# SHChangeNotify を P/Invoke で呼び出し、エクスプローラーに関連付け変更を通知する。
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
