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

# ---- 制御フロー層 ---------------------------------------------------
#
# register.ps1 / unregister.ps1 の「手順そのもの」をここへ置く。
# スクリプト側に残すのは、引数の解決・メッセージ表示・終了コードだけ。
#
# 対象（ProgId・スキーム・拡張子）を引数にしているのは、テストから使い捨ての
# 対象を渡して本番と同一の順序・同一の中止判断を実行できるようにするため。
# 関数単体がすべて正しくても、呼ぶ順序が間違っていれば壊れる。実際に #41 では
# 「Unknown の中止判定が拡張子処理より後にあり、拡張子側だけ先に変更されてから
# 失敗する」不具合が、関数単体テスト 89 件をすべて通過したまま残っていた。
#
# メッセージは出さず、何が起きたかを戻り値で返す（このファイル全体の契約）。
# シェル通知も行わない（使い捨て対象を扱うテストで呼ぶ理由がないため、
# 呼び出し側が担当する）。

# 対象キー同士が衝突していないことを確認する（書き込みの前に行うこと）。
# 書式が正しくても、ProgId キーとスキームキーが同じ、スキーム名が拡張子と同じ、
# 拡張子が重複、といった組み合わせは同じキーを別の役割で二重に扱うことになり、
# 後半の処理が前半の結果を壊す。呼び出し側の誤りなので例外にする。
function Assert-DistinctHirakeTargets {
    param(
        [Parameter(Mandatory)][string]$ProgIdKey,
        [Parameter(Mandatory)][string]$SchemeKey,
        [Parameter(Mandatory)][string[]]$Extensions
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($ext in $Extensions) {
        if (-not $seen.Add($ext)) {
            throw "-Extensions に同じ拡張子が複数指定されています: $ext"
        }
    }

    $keys = @($ProgIdKey, $SchemeKey) + ($Extensions | ForEach-Object { "HKCU:\Software\Classes\$_" })
    $seenKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $keys) {
        if (-not $seenKeys.Add($key)) {
            throw "対象のキーが重複しています（ProgId・スキーム・拡張子は互いに異なる必要があります）: $key"
        }
    }
}

# 登録の制御フロー。
# 戻り値: Status / AbortedTarget / AbortReason / MayHaveModified / ProgIdWritten /
#         SchemeWritten / Extensions / FailureCount
#   Status:
#     'Completed'                  すべて成功
#     'CompletedWithFailures'      拡張子の一部が失敗（コアは成功）
#     'AbortedInvalidExeName'      レジストリ未変更で中止
#     'AbortedNotOwned'            レジストリ未変更で中止
#     'CoreFailedPartiallyApplied' コア登録の途中で失敗（変更済みの可能性あり）
#
#   Aborted* は「レジストリを 1 つも変更していない」ことを意味する。
#   途中まで適用された失敗は Aborted* とは呼ばない（MayHaveModified も参照）。
function Invoke-HirakeRegister {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$ProgId,
        [Parameter(Mandatory)][ValidatePattern('^HKCU:\\Software\\Classes\\[A-Za-z0-9._-]+$')][string]$SchemeKey,
        # 各要素に適用される。呼び出し側の書式誤りは「失敗 1 件」ではなく
        # 例外にする（プログラミングエラーを実行時の失敗に化けさせない）。
        # 空配列は Mandatory が拒否する（コアだけの登録・解除は用途が無い）。
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][ValidatePattern('^\.[A-Za-z0-9]+$')][string[]]$Extensions,
        [string]$DefaultExtension,
        [ValidatePattern('^[A-Za-z0-9._-]+\.exe$')][string]$ExpectedExeName = 'Hirake.exe',
        [string]$ProgIdDisplayName = 'Markdown Document',
        [string]$SchemeDisplayName = 'URL:Hirake Protocol'
    )

    # 既定にする拡張子は、登録する拡張子に含まれていなければならない。
    # 黙って「既定を設定しない」で通すと、綴り誤りが実行時まで表に出ない。
    if ($DefaultExtension -and $Extensions -notcontains $DefaultExtension) {
        throw "-DefaultExtension は -Extensions に含まれている必要があります: $DefaultExtension"
    }

    $progIdKey = "HKCU:\Software\Classes\$ProgId"
    Assert-DistinctHirakeTargets -ProgIdKey $progIdKey -SchemeKey $SchemeKey -Extensions $Extensions

    $openCommand = "`"$ExePath`" `"%1`""

    $result = [pscustomobject]@{
        Status        = 'Completed'
        AbortedTarget = $null
        AbortReason   = $null
        # 書き込みフェーズへ入ったか。「実際に変更した」ではなく
        # 「変更した可能性がある」を表す（最初の書き込みが権限エラーで
        #  何も変えずに失敗した場合も $true になる。安全側に倒している）。
        MayHaveModified = $false
        ProgIdWritten = $false
        SchemeWritten = $false
        Extensions    = @()
        FailureCount  = 0
    }

    # 1. 実行ファイル名の検証。
    #    解除側は shell\open\command の実行ファイル名で「Hirake が登録したキーか」を
    #    判定するため、別名の exe を登録できると解除できない状態を作れる。
    if ((Split-Path -Path $ExePath -Leaf) -ne $ExpectedExeName) {
        $result.Status = 'AbortedInvalidExeName'
        $result.AbortedTarget = $ExePath
        return $result
    }

    # 2. Hirake 名義のキーが別アプリに使われていないか、書き込む前に全部確認する。
    foreach ($path in @($progIdKey, $SchemeKey)) {
        if ((Test-HirakeOwnedKey -Path $path -ExeName $ExpectedExeName) -eq 'NotOwned') {
            $result.Status = 'AbortedNotOwned'
            $result.AbortedTarget = $path
            return $result
        }
    }

    # 3. ProgId と URL プロトコルの登録。
    #    キーは作り直さず値だけを上書きする（削除→再作成の間に失敗すると
    #    元の設定が失われ、ユーザーが追加した verb も巻き添えになる）。
    #    ここで失敗したら拡張子側には手を付けない（参照先の無い
    #    OpenWithProgIds エントリを作らないため）。
    try {
        # ここから先は 1 行でも成功すればレジストリが変わる。
        # 途中で失敗しても巻き戻せない（他プロセスとの競合があり、
        # レジストリに完全なトランザクションを張れない）ため、
        # 「変更済みかもしれない」ことを MayHaveModified で明示する。
        $result.MayHaveModified = $true

        Initialize-RegistryKey -Path $progIdKey
        Set-ItemProperty -Path $progIdKey -Name '(default)' -Value $ProgIdDisplayName

        Initialize-RegistryKey -Path "$progIdKey\DefaultIcon"
        Set-ItemProperty -Path "$progIdKey\DefaultIcon" -Name '(default)' -Value "$ExePath,0"

        Initialize-RegistryKey -Path "$progIdKey\shell\open\command"
        Set-ItemProperty -Path "$progIdKey\shell\open\command" -Name '(default)' -Value $openCommand

        $result.ProgIdWritten = $true

        # 'URL Protocol' は値が空文字のまま「プロパティが存在すること」に意味がある。
        Initialize-RegistryKey -Path $SchemeKey
        Set-ItemProperty -Path $SchemeKey -Name '(default)' -Value $SchemeDisplayName
        New-ItemProperty -Path $SchemeKey -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null

        Initialize-RegistryKey -Path "$SchemeKey\DefaultIcon"
        Set-ItemProperty -Path "$SchemeKey\DefaultIcon" -Name '(default)' -Value "$ExePath,0"

        Initialize-RegistryKey -Path "$SchemeKey\shell\open\command"
        Set-ItemProperty -Path "$SchemeKey\shell\open\command" -Name '(default)' -Value $openCommand

        $result.SchemeWritten = $true
    } catch {
        # 「中止」ではなく「途中まで適用された失敗」。名前でそれを表す
        # （Aborted* は「レジストリを 1 つも変更していない」ことを意味する）。
        $result.Status = 'CoreFailedPartiallyApplied'
        $result.AbortReason = $_.Exception.Message
        return $result
    }

    # 4. 拡張子の登録。1 件の失敗で残りを止めない。
    $extensionResults = @()
    foreach ($ext in $Extensions) {
        $setAsDefault = ($ext -eq $DefaultExtension)
        try {
            $registered = Register-HirakeExtension -Extension $ext -ProgId $ProgId -SetAsDefault:$setAsDefault
            $extensionResults += [pscustomobject]@{
                Extension       = $ext
                Succeeded       = $true
                Error           = $null
                IsDefault       = $setAsDefault
                Written         = $registered.Written
                DefaultSet      = $registered.DefaultSet
                ExistingDefault = $registered.ExistingDefault
            }
        } catch {
            $result.FailureCount++
            $extensionResults += [pscustomobject]@{
                Extension       = $ext
                Succeeded       = $false
                Error           = $_.Exception.Message
                IsDefault       = $setAsDefault
                Written         = $false
                DefaultSet      = $false
                ExistingDefault = $null
            }
        }
    }

    # 拡張子が 1 件のときも配列のままにする（呼び出し側が索引・件数で扱うため）。
    $result.Extensions = @($extensionResults)
    if ($result.FailureCount -gt 0) {
        $result.Status = 'CompletedWithFailures'
    }
    return $result
}

# 解除の制御フロー。
# 戻り値: Status / AbortedTarget / Extensions / Targets / SkippedTargets / FailureCount
#   Status: 'Completed' / 'CompletedWithFailures' / 'AbortedNotOwned' / 'AbortedUnknown'
function Invoke-HirakeUnregister {
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$ProgId,
        [Parameter(Mandatory)][ValidatePattern('^HKCU:\\Software\\Classes\\[A-Za-z0-9._-]+$')][string]$SchemeKey,
        # 各要素に適用される。呼び出し側の書式誤りは「失敗 1 件」ではなく
        # 例外にする（プログラミングエラーを実行時の失敗に化けさせない）。
        # 空配列は Mandatory が拒否する（コアだけの登録・解除は用途が無い）。
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][ValidatePattern('^\.[A-Za-z0-9]+$')][string[]]$Extensions,
        [ValidatePattern('^[A-Za-z0-9._-]+\.exe$')][string]$ExpectedExeName = 'Hirake.exe'
    )

    $progIdKey = "HKCU:\Software\Classes\$ProgId"
    Assert-DistinctHirakeTargets -ProgIdKey $progIdKey -SchemeKey $SchemeKey -Extensions $Extensions

    $result = [pscustomobject]@{
        Status         = 'Completed'
        AbortedTarget  = $null
        Extensions     = @()
        Targets        = @()
        SkippedTargets = $false
        FailureCount   = 0
    }

    # 1. Hirake 名義のキーの状態を、拡張子側に手を付ける前にすべて確認する。
    #    後から気付く形にすると、削除できないキーを参照したままの
    #    OpenWithProgIds エントリだけが先に消える中途半端な解除になる。
    foreach ($path in @($progIdKey, $SchemeKey)) {
        switch (Test-HirakeOwnedKey -Path $path -ExeName $ExpectedExeName) {
            'NotOwned' {
                $result.Status = 'AbortedNotOwned'
                $result.AbortedTarget = $path
                return $result
            }
            'Unknown' {
                $result.Status = 'AbortedUnknown'
                $result.AbortedTarget = $path
                return $result
            }
        }
    }

    # 2. 拡張子の解除。
    $extensionResults = @()
    foreach ($ext in $Extensions) {
        try {
            $removed = Unregister-HirakeExtension -Extension $ext -ProgId $ProgId
            $extensionResults += [pscustomobject]@{
                Extension          = $ext
                Succeeded          = $true
                Error              = $null
                RemovedProgId      = $removed.RemovedProgId
                RemovedOpenWithKey = $removed.RemovedOpenWithKey
                ExistingDefault    = $removed.ExistingDefault
                RemovedDefault     = $removed.RemovedDefault
                RemovedExtKey      = $removed.RemovedExtKey
            }
        } catch {
            $result.FailureCount++
            $extensionResults += [pscustomobject]@{
                Extension          = $ext
                Succeeded          = $false
                Error              = $_.Exception.Message
                RemovedProgId      = $false
                RemovedOpenWithKey = $false
                ExistingDefault    = $null
                RemovedDefault     = $false
                RemovedExtKey      = $false
            }
        }
    }
    $result.Extensions = @($extensionResults)

    # 3. ProgId と URL プロトコルの削除。
    #    拡張子側の解除に失敗している場合は、参照先だけが消えたダングリング状態を
    #    作らないよう見送る。
    if ($result.FailureCount -gt 0) {
        $result.SkippedTargets = $true
        $result.Status = 'CompletedWithFailures'
        return $result
    }

    $targetResults = @()
    foreach ($path in @($progIdKey, $SchemeKey)) {
        try {
            $status = Remove-HirakeOwnedKey -Path $path -ExeName $ExpectedExeName
            if ($status -notin @('Removed', 'NotFound')) {
                $result.FailureCount++
            }
            $targetResults += [pscustomobject]@{
                Path   = $path
                Status = $status
                Error  = $null
            }
        } catch {
            $result.FailureCount++
            $targetResults += [pscustomobject]@{
                Path   = $path
                Status = 'Error'
                Error  = $_.Exception.Message
            }
        }
    }

    $result.Targets = @($targetResults)
    if ($result.FailureCount -gt 0) {
        $result.Status = 'CompletedWithFailures'
    }
    return $result
}

# ---- 表示の組み立て -------------------------------------------------
#
# 「何を表示するか」と「終了コードをいくつにするか」も制御フローの一部であり、
# 退行が起きうる。ここでは組み立てるだけで表示はしない（このファイルの契約は
# 変わらない）。スクリプトは結果を受け取って書き出すだけになる。
#
# 戻り値: Lines（Stream = 'Host'|'Warning'|'Error' と Message の配列） /
#         ExitCode / Notify（シェル通知を行うか）

function New-PresentationLine {
    param(
        [Parameter(Mandatory)][ValidateSet('Host', 'Warning', 'Error')][string]$Stream,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Message
    )
    return [pscustomobject]@{ Stream = $Stream; Message = $Message }
}

# 登録結果の表示内容を組み立てる。
function Get-HirakeRegisterPresentation {
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$ProgId,
        [Parameter(Mandatory)][string]$SchemeKey,
        [Parameter(Mandatory)][string]$ExpectedExeName
    )

    $lines = @()

    if ($Result.Status -eq 'AbortedInvalidExeName') {
        $lines += New-PresentationLine -Stream Error -Message `
            "'$ExpectedExeName' 以外の実行ファイルは登録できません（unregister.ps1 が解除できなくなるため）: $($Result.AbortedTarget)"
        return [pscustomobject]@{ Lines = $lines; ExitCode = 1; Notify = $false }
    }

    # 表示順は従来どおり（exe 名の検証 → 検出メッセージ → 所有権の中止）。
    $lines += New-PresentationLine -Stream Host -Message "Hirake.exe を検出しました: $ExePath"

    if ($Result.Status -eq 'AbortedNotOwned') {
        $label = if ($Result.AbortedTarget -eq $SchemeKey) { "URL プロトコル 'hirake://'" } else { "ProgId '$ProgId'" }
        $lines += New-PresentationLine -Stream Error -Message `
            "$label のキーは Hirake 以外のプログラムが使用しているため、登録を中止しました: $($Result.AbortedTarget)"
        return [pscustomobject]@{ Lines = $lines; ExitCode = 1; Notify = $false }
    }

    if ($Result.ProgIdWritten) {
        $lines += New-PresentationLine -Stream Host -Message "ProgId '$ProgId' を登録しました。"
    }
    if ($Result.SchemeWritten) {
        $lines += New-PresentationLine -Stream Host -Message "URL プロトコル 'hirake://' を登録しました。"
    }

    if ($Result.Status -eq 'CoreFailedPartiallyApplied') {
        $lines += New-PresentationLine -Stream Warning -Message "ProgId / URL プロトコルの登録に失敗しました: $($Result.AbortReason)"
        $lines += New-PresentationLine -Stream Warning -Message '一部だけ書き込まれている可能性があります。原因を解消してから register.ps1 を再実行してください。'
        # 途中まで書き込まれている可能性があるなら、エクスプローラーへは通知する。
        # 通知しないと、中途半端に変わった関連付けが古いまま表示され続ける。
        return [pscustomobject]@{ Lines = $lines; ExitCode = 1; Notify = [bool]$Result.MayHaveModified }
    }

    foreach ($ext in @($Result.Extensions)) {
        if (-not $ext.Succeeded) {
            $lines += New-PresentationLine -Stream Warning -Message "拡張子 '$($ext.Extension)' の登録に失敗しました: $($ext.Error)"
            continue
        }

        if ($ext.Written) {
            $lines += New-PresentationLine -Stream Host -Message "拡張子 '$($ext.Extension)' の OpenWithProgIds に '$ProgId' を登録しました。"
        } else {
            $lines += New-PresentationLine -Stream Host -Message "拡張子 '$($ext.Extension)' の OpenWithProgIds には '$ProgId' が既に登録されています。"
        }

        if ($ext.IsDefault) {
            if ($ext.DefaultSet) {
                $lines += New-PresentationLine -Stream Host -Message "'$($ext.Extension)' の既定プログラムとして '$ProgId' を設定しました。"
            } elseif ($ext.ExistingDefault -eq '') {
                $lines += New-PresentationLine -Stream Host -Message "'$($ext.Extension)' には既定プログラムとして空の値が設定されているため、上書きしませんでした。"
            } else {
                $lines += New-PresentationLine -Stream Host -Message "'$($ext.Extension)' には既に既定プログラム '$($ext.ExistingDefault)' が設定されているため、上書きしませんでした。"
            }
        }
    }

    if ($Result.FailureCount -gt 0) {
        $lines += New-PresentationLine -Stream Host -Message ''
        $lines += New-PresentationLine -Stream Warning -Message "$($Result.FailureCount) 件の拡張子で登録に失敗しました。上記の警告を確認してください。"
        return [pscustomobject]@{ Lines = $lines; ExitCode = 1; Notify = $true }
    }

    foreach ($message in @(
            '',
            '====================================================================',
            'Hirake の関連付け登録が完了しました。',
            'Windows 11 では初回のみ、.md ファイルを右クリック →',
            '「プログラムから開く」→「別のプログラムを選択」→',
            'Hirake を選び「常にこのアプリを使う」にチェックを入れる操作が',
            '必要な場合があります。',
            '====================================================================')) {
        $lines += New-PresentationLine -Stream Host -Message $message
    }

    return [pscustomobject]@{ Lines = $lines; ExitCode = 0; Notify = $true }
}

# 解除結果の表示内容を組み立てる。
function Get-HirakeUnregisterPresentation {
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][string]$ProgId,
        [Parameter(Mandatory)][string]$SchemeKey
    )

    $lines = @()
    $labelOf = {
        param($path)
        if ($path -eq $SchemeKey) { "URL プロトコル 'hirake://'" } else { "ProgId '$ProgId'" }
    }

    if ($Result.Status -eq 'AbortedNotOwned') {
        $lines += New-PresentationLine -Stream Error -Message `
            "$(& $labelOf $Result.AbortedTarget) のキーは Hirake 以外のプログラムが使用しているため、解除を中止しました（レジストリは変更していません）: $($Result.AbortedTarget)"
        return [pscustomobject]@{ Lines = $lines; ExitCode = 1; Notify = $false }
    }

    if ($Result.Status -eq 'AbortedUnknown') {
        $lines += New-PresentationLine -Stream Error -Message `
            "$(& $labelOf $Result.AbortedTarget) のキーは shell\open\command が無く由来を判定できないため、解除を中止しました（レジストリは変更していません）。register.ps1 を実行して登録を修復してから、再度 unregister.ps1 を実行してください: $($Result.AbortedTarget)"
        return [pscustomobject]@{ Lines = $lines; ExitCode = 1; Notify = $false }
    }

    foreach ($ext in @($Result.Extensions)) {
        if (-not $ext.Succeeded) {
            $lines += New-PresentationLine -Stream Warning -Message "拡張子 '$($ext.Extension)' の解除に失敗しました: $($ext.Error)"
            continue
        }

        if ($ext.RemovedProgId) {
            $lines += New-PresentationLine -Stream Host -Message "拡張子 '$($ext.Extension)' の OpenWithProgIds から '$ProgId' を削除しました。"
        }

        if ($ext.RemovedDefault) {
            $lines += New-PresentationLine -Stream Host -Message "'$($ext.Extension)' の既定プログラム設定（'$ProgId'）を削除しました。"
        } elseif ($ext.ExistingDefault) {
            $lines += New-PresentationLine -Stream Host -Message "'$($ext.Extension)' の既定プログラムは '$($ext.ExistingDefault)' のままです（Hirake が設定したものではないため削除しません）。"
        }

        if ($ext.RemovedExtKey) {
            $lines += New-PresentationLine -Stream Host -Message "空になった拡張子キー '$($ext.Extension)' を削除しました。"
        }
    }

    if ($Result.SkippedTargets) {
        $lines += New-PresentationLine -Stream Warning -Message `
            "拡張子の解除に失敗したため、ProgId '$ProgId' と URL プロトコル 'hirake://' は削除しません（拡張子側に参照が残るため）。"
    } else {
        foreach ($target in @($Result.Targets)) {
            $label = & $labelOf $target.Path
            switch ($target.Status) {
                'Removed'  { $lines += New-PresentationLine -Stream Host -Message "$label を削除しました。" }
                'NotFound' { $lines += New-PresentationLine -Stream Host -Message "$label は登録されていませんでした。" }
                'Unknown'  {
                    $lines += New-PresentationLine -Stream Warning -Message `
                        "$label は shell\open\command が無く由来を判定できないため削除しませんでした。register.ps1 を実行してから再度 unregister.ps1 を実行してください: $($target.Path)"
                }
                'NotOwned' {
                    $lines += New-PresentationLine -Stream Warning -Message `
                        "$label は Hirake 以外のプログラムを指しているため削除しませんでした。"
                }
                'Error' {
                    $lines += New-PresentationLine -Stream Warning -Message "$label を削除できませんでした: $($target.Error)"
                }
            }
        }
    }

    if ($Result.FailureCount -gt 0) {
        $lines += New-PresentationLine -Stream Host -Message ''
        $lines += New-PresentationLine -Stream Warning -Message "$($Result.FailureCount) 件の処理に失敗しました。上記の警告を確認してください。"
        return [pscustomobject]@{ Lines = $lines; ExitCode = 1; Notify = $true }
    }

    foreach ($message in @(
            '',
            '====================================================================',
            'Hirake の関連付け解除が完了しました。',
            '====================================================================')) {
        $lines += New-PresentationLine -Stream Host -Message $message
    }

    return [pscustomobject]@{ Lines = $lines; ExitCode = 0; Notify = $true }
}

# 組み立てた表示内容を実際に書き出す。
function Write-HirakePresentation {
    param([Parameter(Mandatory)]$Presentation)

    foreach ($line in @($Presentation.Lines)) {
        switch ($line.Stream) {
            'Host'    { Write-Host $line.Message }
            'Warning' { Write-Warning $line.Message }
            'Error'   { Write-Error $line.Message }
        }
    }
}

# ---- シェル通知 -----------------------------------------------------

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
