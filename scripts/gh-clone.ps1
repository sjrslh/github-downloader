<#
.SYNOPSIS
    GitHub 仓库多连接拉取 + 解包，产出可直接给 DSH 安装的本地目录。

.DESCRIPTION
    DSH 的插件安装走 pnpm add，spec 可以是 registry 包名、git 地址、tarball，
    也可以是「绝对路径」（读该目录的 package.json）。
    所以最快的一条路是：用多连接把仓库 zip/tar 拉下来解包成目录，
    再用本地绝对路径安装 —— 全程避开 git clone 的单流限速。

.PARAMETER Repo
    owner/repo 或完整 GitHub 仓库地址。

.PARAMETER Ref
    分支 / tag / commit，默认 HEAD（由 codeload 解析默认分支）。

.PARAMETER OutDir
    解包目标根目录，默认当前目录。最终目录为 <OutDir>\<repo>-<ref>。

.PARAMETER KeepArchive
    保留下载的压缩包。

.EXAMPLE
    .\gh-clone.ps1 owner/repo
    .\gh-clone.ps1 https://github.com/owner/repo -Ref v1.2.3 -OutDir D:\plugins
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Repo,
    [string]$Ref = '',
    [string]$OutDir = (Get-Location).Path,
    [int]$Connections = 16,
    [switch]$KeepArchive
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Parse-Repo {
    param([string]$r)
    $r = $r -replace '^https?://github\.com/', '' -replace '\.git$', '' -replace '/+$', ''
    $parts = $r -split '/'
    if ($parts.Count -lt 2) { throw "仓库格式应为 owner/repo，收到: $r" }
    return [pscustomobject]@{ Owner = $parts[0]; Name = $parts[1] }
}

$info  = Parse-Repo $Repo
$slug  = "$($info.Owner)/$($info.Name)"
$refSeg = if ([string]::IsNullOrWhiteSpace($Ref)) { 'HEAD' } else { $Ref }
$zipUrl = "https://codeload.github.com/$slug/zip/$refSeg"

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path

Write-Host "仓库 : $slug" -ForegroundColor Cyan
Write-Host "引用 : $refSeg" -ForegroundColor Cyan
Write-Host "来源 : $zipUrl" -ForegroundColor DarkGray

$dlScript = Join-Path $PSScriptRoot 'gh-dl.ps1'
if (-not (Test-Path $dlScript)) { throw "缺少 gh-dl.ps1，应与本脚本同目录。" }

$zipName = "$($info.Name)-$($refSeg).zip"
& $dlScript $zipUrl -OutDir $OutDir -Connections $Connections -NoMirror -OutName $zipName
$zipPath = Join-Path $OutDir ("$($info.Name)-$($refSeg).zip")
if (-not (Test-Path $zipPath)) {
    $zipPath = Get-ChildItem $OutDir -Filter '*.zip' | Sort-Object LastWriteTime -Descending |
               Select-Object -First 1 -ExpandProperty FullName
}
if (-not $zipPath) { throw '没有找到下载下来的 zip。' }

$stage = Join-Path $OutDir (".stage-" + $info.Name)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $stage -Force

# codeload 的 zip 顶层会多一层 <repo>-<sha> 目录，剥掉后落到干净的 <OutDir>\<repo>
$final = Join-Path $OutDir $info.Name
if (Test-Path $final) { Remove-Item $final -Recurse -Force }
$inner = Get-ChildItem $stage -Directory | Select-Object -First 1
if ($inner -and ((Get-ChildItem $stage | Measure-Object).Count -eq 1)) {
    Move-Item -LiteralPath $inner.FullName -Destination $final
} else {
    Move-Item -LiteralPath $stage -Destination $final
}
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
if (-not $KeepArchive) { Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue }

$pkg = Join-Path $final 'package.json'
Write-Host ''
Write-Host "本地目录 : $final" -ForegroundColor Green
if (Test-Path $pkg) {
    $pj = Get-Content $pkg -Raw | ConvertFrom-Json
    Write-Host ("package  : {0} @ {1}" -f $pj.name, $pj.version) -ForegroundColor Green
    Write-Host ''
    Write-Host '在 DSH 里用本地路径安装（spec 填下面这行）:' -ForegroundColor Cyan
    Write-Host ("  {0}" -f $final) -ForegroundColor White
} else {
    Write-Host '警告：该仓库根目录没有 package.json，可能不是 DSH 插件包。' -ForegroundColor Yellow
}
