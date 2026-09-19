#requires -Version 5.1
<#
.SYNOPSIS
    UI Automation の薄い層（ISSUE #96）。

.DESCRIPTION
    ここは Hirake を知らない。「ProcessId からウィンドウを掴む」「AutomationId で子孫を
    探す」「押す」「選ぶ」だけを持つ。「SidebarToggleButton の隣は BackButton」のような
    製品知識は tests/shell/cases/*.ps1 に置く。

    追加の依存は入れず、生の UI Automation（UIAutomationClient）を直接叩く（#94 の
    CDP クライアントと同じ方針）。pwsh 7.6.5 でアセンブリが読めることは確認済み。

    この層で分かっていること:
    - **UI Automation は描画順（paint order）を公開しない。** 重なりの検証はできない（→ #94 の CDP 側）
    - WPF は AutomationProperties.AutomationId 未設定なら x:Name を AutomationId として公開する
    - Content が Segoe MDL2 のグリフだと UIA の Name はグリフ 1 文字になる。判定には使わない
      （日本語は ToolTip = UIA の HelpText にある）
    - 要素は掴んだ後に消えうる（タブを閉じた直後など）。プロパティ読みは必ず例外を握る
#>

Set-StrictMode -Version Latest

$script:UiaReady = $false

function Initialize-Uia {
    <#
    .SYNOPSIS
        UI Automation のアセンブリを読む。何度呼んでもよい。
    #>
    [CmdletBinding()]
    param()

    if ($script:UiaReady) { return }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $script:UiaReady = $true
}

function Get-UiaProperty {
    <#
    .SYNOPSIS
        Current のプロパティを 1 つ読む。要素が消えていれば $null を返す。
    .DESCRIPTION
        UIA は「掴んだ後に消えた要素」へ触ると ElementNotAvailableException を投げる。
        判定の途中で落とすと原因が読めなくなるため、読みは必ずここを通す。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowNull()]$Element,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -eq $Element) { return $null }
    try { return $Element.Current.$Name } catch { return $null }
}

function Wait-UiaWindow {
    <#
    .SYNOPSIS
        ProcessId からトップレベルウィンドウを掴めるまで待つ。
    .DESCRIPTION
        画面外（IsOffscreen）のウィンドウは数えない。起動直後は「まだ描かれていない
        ウィンドウ」が一瞬見えることがあり、それを掴むと以降の探索が空振りする。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [int]$TimeoutSec = 30
    )

    Initialize-Uia

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)))

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $found = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
            for ($i = 0; $i -lt $found.Count; $i++) {
                $candidate = $found[$i]
                if ((Get-UiaProperty -Element $candidate -Name 'IsOffscreen') -eq $false) {
                    return $candidate
                }
            }
        } catch {
            # 列挙の最中にウィンドウが増減した。次の周回で見直す。
        }
        Start-Sleep -Milliseconds 250
    }

    throw "メインウィンドウを掴めませんでした（ProcessId $ProcessId / $TimeoutSec s）。"
}

function Find-UiaById {
    <#
    .SYNOPSIS
        AutomationId で子孫を 1 つ探す。見つからなければ $null（例外にしない）。
    .DESCRIPTION
        「無いこと」もケース側の判定材料になるので、ここでは投げない。
        TimeoutSec を渡すと、見つかるまで 250ms 間隔で待つ。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string]$AutomationId,
        [int]$TimeoutSec = 0
    )

    Initialize-Uia

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        try {
            $found = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($found) { return $found }
        } catch {
            # 探索中にツリーが作り替えられた。時間が残っていれば見直す。
        }
        if ((Get-Date) -ge $deadline) { return $null }
        Start-Sleep -Milliseconds 250
    }
}

function Get-UiaChildren {
    <#
    .SYNOPSIS
        ControlView の直接の子を、並び順どおりに配列で返す。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Element)

    Initialize-Uia

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $children = @()
    try {
        $child = $walker.GetFirstChild($Element)
        while ($null -ne $child) {
            $children += $child
            $child = $walker.GetNextSibling($child)
        }
    } catch {
        # 歩いている最中に作り替えられた。取れた分だけ返す。
    }
    return $children
}

function Get-UiaSiblings {
    <#
    .SYNOPSIS
        ある要素と同じ親を持つ子を、並び順どおりに配列で返す（自分自身を含む）。
    .DESCRIPTION
        注意: **WPF の Panel（StackPanel / Grid / Border）は AutomationPeer を持たず、
        UIA のツリーに現れない。** 子はそのまま上位（Window 直下など）へ繰り上がるので、
        ここが返すのは「同じパネルの子」ではなく繰り上がった先の兄弟一式になる
        （実測で確認済み。→ docs/wiki/hirake-shell-harness.md）。

        そのため「どのグループに属するか」はツリーからは決められない。位置で切るには
        Get-UiaRect を併用する（→ cases/Case-86-ToolbarOrder.ps1）。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Element)

    Initialize-Uia

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $parent = $walker.GetParent($Element)
    if ($null -eq $parent) { throw '親要素を取得できませんでした（既に閉じられている可能性があります）。' }

    return (Get-UiaChildren -Element $parent)
}

function Get-UiaRect {
    <#
    .SYNOPSIS
        画面座標の矩形（BoundingRectangle）を返す。取れなければ $null。
    .DESCRIPTION
        **UIA から「実際にどこに描かれているか」を取れる唯一の手掛かり。**
        ツリーの順序は論理的な並びなので、Grid.Column を入れ替えて見た目だけ
        左右を交換された場合はツリーからは分からない。矩形なら分かる。
        描画順（z-order）は別で、これは UIA からは取れない。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Element)

    $rect = Get-UiaProperty -Element $Element -Name 'BoundingRectangle'
    if ($null -eq $rect) { return $null }
    if ($rect.IsEmpty) { return $null }
    return $rect
}

function Get-UiaOrderedIds {
    <#
    .SYNOPSIS
        指定した AutomationId 群が、ツリー上に現れる順で並んだ配列を返す。
    .DESCRIPTION
        兄弟の並びだけでは足りないため用意した。**WPF の Panel（StackPanel / Grid）は
        AutomationPeer を持たないため、UIA のツリーには現れないことがある。**
        その場合、パネルの子はそのまま上位（Window 直下など）へ繰り上がる。
        つまり「同じ StackPanel の子である」ことをツリーからは前提にできない。

        そこで ControlView を先行順（document order）で深さ優先に歩き、$Ids に含まれる
        AutomationId だけを出現順に拾う。パネルが現れる木でも、繰り上がった木でも
        同じ答えになる。

        **全部揃った時点で打ち切る。** ツールバーは最初の行なので、WebView2 が抱える
        巨大なツリーへ降りる前に終わる。保険として深さと訪問数にも上限を置く。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)][string[]]$Ids,
        [int]$MaxDepth = 12,
        [int]$MaxNodes = 5000
    )

    Initialize-Uia

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $wanted = @{}
    foreach ($id in $Ids) { $wanted[$id] = $true }

    $found = @()
    $visited = 0
    $stack = New-Object System.Collections.Stack
    $stack.Push([pscustomobject]@{ Element = $Root; Depth = 0 })

    while ($stack.Count -gt 0) {
        if ($found.Count -ge $Ids.Count) { break }
        if ($visited -ge $MaxNodes) { break }

        $node = $stack.Pop()
        $visited++

        $id = Get-UiaProperty -Element $node.Element -Name 'AutomationId'
        if ($id -and $wanted.ContainsKey([string]$id)) { $found += [string]$id }

        if ($node.Depth -ge $MaxDepth) { continue }

        # 先行順を保つため、子を集めてから逆順に積む。
        $children = @()
        try {
            $child = $walker.GetFirstChild($node.Element)
            while ($null -ne $child) {
                $children += $child
                $child = $walker.GetNextSibling($child)
            }
        } catch {
            # 歩いている最中に作り替えられた。この枝は諦めて次へ。
            continue
        }

        for ($i = $children.Count - 1; $i -ge 0; $i--) {
            $stack.Push([pscustomobject]@{ Element = $children[$i]; Depth = $node.Depth + 1 })
        }
    }

    return $found
}

function Get-UiaDescendants {
    <#
    .SYNOPSIS
        子孫を ControlType で列挙する（並び順は UIA が返すツリー順）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)]$ControlType
    )

    Initialize-Uia

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)

    $result = @()
    try {
        $found = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        for ($i = 0; $i -lt $found.Count; $i++) { $result += $found[$i] }
    } catch {
        # 列挙の最中に作り替えられた場合は「今は取れない」として空を返す。
        return @()
    }
    return $result
}

function Wait-UiaDescendantCount {
    <#
    .SYNOPSIS
        指定 ControlType の子孫が期待の件数になるまで待ち、最後に見えた件数を返す。
    .DESCRIPTION
        タブの増減は非同期（別プロセスからの転送 → Dispatcher）なので、1 回数える
        だけでは早すぎる。件数を返すのは、ケース側が assertion を書けるようにするため
        （ここでは投げない）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)]$ControlType,
        [Parameter(Mandatory)][int]$Expected,
        [int]$TimeoutSec = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $last = @(Get-UiaDescendants -Root $Root -ControlType $ControlType).Count
        if ($last -eq $Expected) { return $last }
        if ((Get-Date) -ge $deadline) { return $last }
        Start-Sleep -Milliseconds 250
    }
}

function Invoke-UiaElement {
    <#
    .SYNOPSIS
        InvokePattern で押す。対応していなければ、何が押せなかったかを添えて投げる。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Element)

    Initialize-Uia

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $id = Get-UiaProperty -Element $Element -Name 'AutomationId'
        throw "InvokePattern に対応していません（AutomationId='$id'）。"
    }
    $pattern.Invoke()
}

function Select-UiaElement {
    <#
    .SYNOPSIS
        SelectionItemPattern で選ぶ（タブの切り替え）。
    .DESCRIPTION
        キー入力の合成は使わない。フォーカスの所在で結果が変わるため（#94 でも
        同じ理由でキー合成を避けている）。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Element)

    Initialize-Uia

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $id = Get-UiaProperty -Element $Element -Name 'AutomationId'
        throw "SelectionItemPattern に対応していません（AutomationId='$id'）。"
    }
    $pattern.Select()
}

function Test-UiaSelected {
    <#
    .SYNOPSIS
        選択されているかを返す。対応していない・要素が消えている場合は $null。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Element)

    Initialize-Uia

    $pattern = $null
    try {
        if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            return $null
        }
        return [bool]$pattern.Current.IsSelected
    } catch {
        return $null
    }
}

function Get-UiaText {
    <#
    .SYNOPSIS
        子孫の ControlType.Text の Name を連結して返す（DataTemplate 内の表示名）。
    .DESCRIPTION
        タブ 1 つ = ListBoxItem で、中身は DataTemplate なので AutomationId が無い。
        表示名は子の TextBlock（= ControlType.Text）の Name にある。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Element)

    $parts = @()
    foreach ($text in (Get-UiaDescendants -Root $Element -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        $name = Get-UiaProperty -Element $text -Name 'Name'
        if ($name) { $parts += [string]$name }
    }
    return ($parts -join '')
}

function Get-UiaHelpText {
    <#
    .SYNOPSIS
        HelpText（WPF の ToolTip）を返す。グリフ 1 文字の Name の代わりに使う。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Element)

    $value = Get-UiaProperty -Element $Element -Name 'HelpText'
    if ($null -eq $value) { return '' }
    return [string]$value
}
