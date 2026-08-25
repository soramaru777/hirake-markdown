#requires -Version 5.1
<#
.SYNOPSIS
    タブの切り替えと「タブを閉じる」が効く（ISSUE #96）。

.DESCRIPTION
    タブは TabControl ではなく ListBox（TabList）で、1 タブ = ListBoxItem。
    中身は DataTemplate なので AutomationId が無く、表示名は子の TextBlock
    （= ControlType.Text）の Name から読む。

    2 つ目のタブは「2 番目のプロセスを起動してパスを転送させる」ことで作る。
    Ctrl+O はファイルダイアログが開いて止まるため使わない（→ 設計のスコープ外）。

    切り替えはキー入力の合成ではなく SelectionItemPattern。押下は InvokePattern。
    どちらもフォーカスの所在に結果が左右されない。
#>

Set-StrictMode -Version Latest

function Wait-TabSelection {
    <#
    .SYNOPSIS
        指定のタブが選択状態になるまで待つ（WPF の選択反映は同期ではない）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Element,
        [int]$TimeoutSec = 10
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $selected = Test-UiaSelected -Element $Element
        if ($selected -eq $true) { return $true }
        if ((Get-Date) -ge $deadline) { return $selected }
        Start-Sleep -Milliseconds 250
    }
}

function Get-TabCloseButton {
    <#
    .SYNOPSIS
        タブ（ListBoxItem）の中から「タブを閉じる」ボタンを取り出す。
    .DESCRIPTION
        DataTemplate 内なので AutomationId が無い。HelpText（= ToolTip）で裏を取り、
        一致するものが無ければ最初のボタンを返す（呼び出し側が HelpText を判定する）。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Tab)

    $buttons = @(Get-UiaDescendants -Root $Tab -ControlType ([System.Windows.Automation.ControlType]::Button))
    if ($buttons.Count -eq 0) { return $null }

    foreach ($button in $buttons) {
        if ((Get-UiaHelpText -Element $button) -eq 'タブを閉じる') { return $button }
    }
    return $buttons[0]
}

function Invoke-CaseTabs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)]$Context
    )

    $results = @()
    $itemType = [System.Windows.Automation.ControlType]::ListItem

    $tabList = Find-UiaById -Root $Window -AutomationId 'TabList' -TimeoutSec 10
    if (-not $tabList) {
        return (Assert-Ok -Label 'タブ一覧（TabList）を掴める' -Condition $false `
                -Detail 'AutomationId=TabList が見つかりません')
    }

    $count = Wait-UiaDescendantCount -Root $tabList -ControlType $itemType -Expected 1 -TimeoutSec 30
    $results += Assert-Ok -Label '起動直後のタブは 1 つ' -Condition ($count -eq 1) -Detail "実測 $count 件"

    # 2 つ目を開く。二重起動の転送経路をそのまま使う（別プロセス → パイプ → Dispatcher）。
    $second = Start-HirakeSecondInstance -HostInfo $Context.HostInfo -OpenFile $Context.SecondFile
    $results += Assert-Ok -Label '2 番目のプロセスは転送して終了する' -Condition $second.Exited `
        -Detail ("終了コード {0}" -f $second.ExitCode)

    $count = Wait-UiaDescendantCount -Root $tabList -ControlType $itemType -Expected 2 -TimeoutSec 30
    $results += Assert-Ok -Label '2 つ目を開くとタブが 2 になる' -Condition ($count -eq 2) -Detail "実測 $count 件"
    if ($count -ne 2) { return $false }

    $tabs = @(Get-UiaDescendants -Root $tabList -ControlType $itemType)
    $firstText = Get-UiaText -Element $tabs[0]
    $secondText = Get-UiaText -Element $tabs[1]
    $results += Assert-Ok -Label 'タブは開いた順に並ぶ' `
        -Condition (($firstText -like '*shell01*') -and ($secondText -like '*shell02*')) `
        -Detail ("実際: {0} / {1}" -f $firstText, $secondText)

    # 以降の操作は表示名で選ぶ。並び順の判定が落ちても「切替」「閉じる」が
    # 別々に判定されるようにする（1 件の失敗で残りが道連れにならない）。
    $tabShell01 = @($tabs | Where-Object { (Get-UiaText -Element $_) -like '*shell01*' })
    $tabShell02 = @($tabs | Where-Object { (Get-UiaText -Element $_) -like '*shell02*' })
    if ($tabShell01.Count -ne 1 -or $tabShell02.Count -ne 1) {
        $results += Assert-Ok -Label '2 つのタブを表示名で見分けられる' -Condition $false `
            -Detail ("実際: {0} / {1}" -f $firstText, $secondText)
        return $false
    }
    $tabA = $tabShell01[0]
    $tabB = $tabShell02[0]

    # 1 つ目へ切り替える（転送直後は 2 つ目が選択されている）。
    Select-UiaElement -Element $tabA
    $results += Assert-Ok -Label '1 つ目を選ぶと選択が移る' `
        -Condition ((Wait-TabSelection -Element $tabA) -eq $true)
    $results += Assert-Ok -Label '2 つ目の選択は外れる' `
        -Condition ((Test-UiaSelected -Element $tabB) -eq $false)

    # 2 つ目を閉じる。
    $closeButton = Get-TabCloseButton -Tab $tabB
    if (-not $closeButton) {
        $results += Assert-Ok -Label 'タブに閉じるボタンがある' -Condition $false
        return $false
    }
    $results += Assert-Ok -Label '閉じるボタンの ToolTip は「タブを閉じる」' `
        -Condition ((Get-UiaHelpText -Element $closeButton) -eq 'タブを閉じる') `
        -Detail ("実際: {0}" -f (Get-UiaHelpText -Element $closeButton))

    Invoke-UiaElement -Element $closeButton
    $count = Wait-UiaDescendantCount -Root $tabList -ControlType $itemType -Expected 1 -TimeoutSec 20
    $results += Assert-Ok -Label '閉じるボタンでタブが 1 に戻る' -Condition ($count -eq 1) -Detail "実測 $count 件"

    if ($count -eq 1) {
        # 閉じた側の要素は既に無効。掴み直してから読む。
        $remaining = @(Get-UiaDescendants -Root $tabList -ControlType $itemType)
        $remainingText = if ($remaining.Count -eq 1) { Get-UiaText -Element $remaining[0] } else { '' }
        $results += Assert-Ok -Label '残ったのは 1 つ目のタブ' -Condition ($remainingText -like '*shell01*') `
            -Detail ("実際: {0}" -f $remainingText)
    }

    return (-not ($results -contains $false))
}
