#requires -Version 5.1
<#
.SYNOPSIS
    #86 の回帰: ツールバーのボタンが期待どおりの並びで置かれている。

.DESCRIPTION
    #86 は「#84 で足した戻る・上へ の 2 ボタンに対して、ハンバーガーが右へ来ていた」
    という手戻り。publish を触るまで気づけなかったが、並びは UI Automation から見える。
    ここが #96 の出発点。

    判定は AutomationId で書く。WPF は AutomationProperties.AutomationId が未設定なら
    x:Name を AutomationId として公開するので、製品コードには手を入れていない。
    Name（= Content）は Segoe MDL2 のグリフ 1 文字なので判定に使わない。

    **「同じ StackPanel の子」は当てにできない。** WPF の Panel / Border は
    AutomationPeer を持たず UIA のツリーに現れないため、ツールバーの 13 ボタンと
    タブ一覧は Window の直下へ並んで繰り上がる（実測で確認済み）。そこで:

      1. **ツールバー行を矩形で切り出す。** アンカー（`SidebarToggleButton`）と縦に
         重なる兄弟だけを取る。TitleBar やサイドバー・本文は縦に重ならないので落ちる
      2. **実際の X 座標で左から並べて、期待列と完全一致を見る。** 見た目の配置その
         ものを見るので、`Grid.Column` を入れ替えて左右を交換した場合も検出できる
      3. **論理ツリーの出現順も別に見る。** Tab キーの移動順はこちらに従うため

    1 の「完全一致」は、**期待に無い要素が 1 つでも行に居れば落ちる**ことを意味する。
    ボタンを足したらこのケースは落ちる。それは意図した動作で、「並びを変えた」と
    「うっかり動いた」を区別できないため、期待値の更新はレビューを通す。直すのは
    下の 2 つの配列だけでよい。
#>

Set-StrictMode -Version Latest

# 移動の 3 つ（サイドバー・戻る・上へ）。Grid.Column=0 の StackPanel。
$script:ExpectedLeft = @(
    'SidebarToggleButton'
    'BackButton'
    'UpButton'
)

# 「別のビューを開く」群。Grid.Column=2 の StackPanel。
$script:ExpectedRight = @(
    'GraphButton'
    'CanvasButton'
    'StructureButton'
    'LinkCheckButton'
    'FingerprintButton'
    'StatsButton'
    'ExportPdfButton'
    'PrintButton'
    'WorkspaceButton'
    'ThemeButton'
)

function Get-ToolbarRowIds {
    <#
    .SYNOPSIS
        アンカーと縦に重なる兄弟を、画面上の X 順に並べて AutomationId の配列で返す。
    .DESCRIPTION
        AutomationId が空の要素は '(no-id)' として残す。**飛ばさない。**
        名無しの要素が行に混ざったこと自体を検出したいため。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Anchor)

    $anchorRect = Get-UiaRect -Element $Anchor
    if ($null -eq $anchorRect) { throw 'アンカーの矩形を取得できませんでした。' }

    $row = @()
    foreach ($sibling in (Get-UiaSiblings -Element $Anchor)) {
        $rect = Get-UiaRect -Element $sibling
        if ($null -eq $rect) { continue }
        # 縦に重ならないものはツールバー行ではない（TitleBar / サイドバー / 本文）。
        if ($rect.Bottom -le $anchorRect.Top -or $rect.Top -ge $anchorRect.Bottom) { continue }

        $id = [string](Get-UiaProperty -Element $sibling -Name 'AutomationId')
        $row += [pscustomobject]@{
            Id   = $(if ($id) { $id } else { '(no-id)' })
            Left = [double]$rect.Left
        }
    }

    return @($row | Sort-Object Left | ForEach-Object { $_.Id })
}

function Invoke-Case86 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)]$Context
    )

    $results = @()

    # 左 3 個 → タブ一覧 → 右 10 個。ツールバー行に居てよいのはこの 14 個だけ。
    $expected = @($script:ExpectedLeft) + @('TabList') + @($script:ExpectedRight)

    $anchor = Find-UiaById -Root $Window -AutomationId $script:ExpectedLeft[0] -TimeoutSec 15
    if (-not $anchor) {
        $results += Assert-Ok -Label 'ツールバーを掴める' -Condition $false `
            -Detail ("{0} が見つかりません" -f $script:ExpectedLeft[0])
        return $false
    }

    # 1) 見た目の並び。X 座標で左から。行に余計な要素が居ても落ちる。
    $row = @(Get-ToolbarRowIds -Anchor $anchor)
    $results += Assert-Ok -Label 'ツールバーは左から 左3個 -> タブ -> 右10個' `
        -Condition (($row -join ',') -eq ($expected -join ',')) `
        -Detail ("実際: {0}" -f ($row -join ','))

    # 2) 論理ツリーの出現順（Tab キーの移動順はこちらに従う）。
    $ordered = @(Get-UiaOrderedIds -Root $Window -Ids $expected)
    $results += Assert-Ok -Label 'ツリー上の出現順も同じ' `
        -Condition (($ordered -join ',') -eq ($expected -join ',')) `
        -Detail ("実際: {0}" -f ($ordered -join ','))

    return (-not ($results -contains $false))
}
