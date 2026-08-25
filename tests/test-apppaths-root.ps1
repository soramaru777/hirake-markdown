#requires -Version 5.1
<#
.SYNOPSIS
    AppPaths.Root が HIRAKE_DATA_ROOT を正しく解釈するかの回帰テスト（ISSUE #94）。

.DESCRIPTION
    キャンバス検証ハーネスは HIRAKE_DATA_ROOT で実利用のデータ領域から隔離される。
    ここが効かないまま回すと、実利用の settings.json / canvas（ピン留めはワークスペースに
    属さずアプリ全体で共有される）/ WebView2 プロファイルが汚れる。混ざったあとの
    切り分けは手作業になるため、この 1 点だけは CLI で必ず自動検証する。

    テスト用に AppPaths を書き直したりはしない。%TEMP% に使い捨てのコンソール
    プロジェクトを作り、src/Hirake/AppPaths.cs を <Compile Include> でそのまま
    取り込んで Root を印字させる。検証対象は常に製品コードそのもの。

    .NET SDK が要る（CI では既に setup-dotnet 済み）。

.EXAMPLE
    pwsh -NoProfile -File tests\test-apppaths-root.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot '..\src\Hirake\AppPaths.cs'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    Write-Error "AppPaths.cs が見つかりません: $sourcePath"
    exit 1
}
$sourcePath = (Resolve-Path -LiteralPath $sourcePath).Path

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

$probeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("hirake-apppaths-" + [guid]::NewGuid().ToString('N'))

# 既定値の期待。プローブは同じ API（Environment.SpecialFolder.LocalApplicationData）
# を通るので、ここも同じ値から組み立てる。
$defaultRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Hirake'

# 元の値を退避する。テスト後に必ず戻す（このプロセスから漏らさない）。
$savedValue = $env:HIRAKE_DATA_ROOT
$savedIsSet = Test-Path Env:\HIRAKE_DATA_ROOT

function Set-DataRoot {
    param([AllowNull()][string]$Value, [switch]$Unset)

    if ($Unset) {
        if (Test-Path Env:\HIRAKE_DATA_ROOT) { Remove-Item Env:\HIRAKE_DATA_ROOT }
        return
    }
    $env:HIRAKE_DATA_ROOT = $Value
}

function Get-ProbeRoot {
    param([Parameter(Mandatory)][string]$Dll)

    $out = & dotnet $Dll
    if ($LASTEXITCODE -ne 0) {
        throw "プローブの実行に失敗しました。終了コード: $LASTEXITCODE"
    }
    # 1 行だけ出す作りだが、SDK の付随出力が混ざっても最後の非空行を採る。
    return (@($out) | Where-Object { $_ -and $_.Trim() } | Select-Object -Last 1).Trim()
}

Write-Host '===================================================================='
Write-Host 'AppPaths.Root / HIRAKE_DATA_ROOT'
Write-Host '===================================================================='
Write-Host ("  probe   : {0}" -f $probeDir)
Write-Host ("  default : {0}" -f $defaultRoot)
Write-Host ''

try {
    New-Item -ItemType Directory -Path $probeDir -Force | Out-Null

    # 製品コードを取り込むだけのプローブ。UI もパッケージも要らない。
    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>AppPathsProbe</RootNamespace>
    <AssemblyName>AppPathsProbe</AssemblyName>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="$sourcePath" />
  </ItemGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $probeDir 'AppPathsProbe.csproj') -Value $csproj -Encoding UTF8

    $program = @"
// AppPaths.Root を印字するだけ。判定は呼び出し側の PowerShell が行う。
System.Console.WriteLine(Hirake.AppPaths.Root);
"@
    Set-Content -LiteralPath (Join-Path $probeDir 'Program.cs') -Value $program -Encoding UTF8

    Write-Host '  プローブをビルドしています…'
    $build = & dotnet build (Join-Path $probeDir 'AppPathsProbe.csproj') -c Release --nologo -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        $build | ForEach-Object { Write-Host "    $_" }
        throw "プローブのビルドに失敗しました。終了コード: $LASTEXITCODE"
    }

    $dll = Join-Path $probeDir 'bin\Release\net10.0\AppPathsProbe.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "プローブの成果物が見つかりません: $dll"
    }
    Write-Host ''

    # 1) 未設定 → 既定（既存利用者に影響しないこと）
    Set-DataRoot -Unset
    Assert-Equal '未設定なら %LocalAppData%\Hirake' (Get-ProbeRoot $dll) $defaultRoot

    # 2) 絶対パス → その値
    $custom = Join-Path ([System.IO.Path]::GetTempPath()) 'hirake-root-test'
    Set-DataRoot $custom
    Assert-Equal '絶対パスなら差し替わる' (Get-ProbeRoot $dll) ([System.IO.Path]::GetFullPath($custom))

    # 3) 末尾セパレータつき絶対パス → 正規化される（派生プロパティの二重区切りを防ぐ）
    Set-DataRoot ($custom + '\')
    Assert-Equal '末尾セパレータは正規化される' (Get-ProbeRoot $dll) ([System.IO.Path]::GetFullPath($custom))

    # 4) 相対パス → 既定（カレント配下へ散らかさない fail-safe）
    Set-DataRoot '.\hirake-relative'
    Assert-Equal '相対パスは無視して既定へ倒す' (Get-ProbeRoot $dll) $defaultRoot

    # 5) 空白のみ → 既定
    #    ※ 空文字は PowerShell ではプロセス環境変数の削除と同義になるため、
    #      1) の「未設定」で覆われる。ここでは空白のみを見る。
    Set-DataRoot '   '
    Assert-Equal '空白のみは無視して既定へ倒す' (Get-ProbeRoot $dll) $defaultRoot
} finally {
    if ($savedIsSet) { $env:HIRAKE_DATA_ROOT = $savedValue }
    elseif (Test-Path Env:\HIRAKE_DATA_ROOT) { Remove-Item Env:\HIRAKE_DATA_ROOT }

    if (Test-Path -LiteralPath $probeDir) {
        Remove-Item -LiteralPath $probeDir -Recurse -Force -ErrorAction SilentlyContinue
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
