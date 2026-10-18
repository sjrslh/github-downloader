<#
.SYNOPSIS
    GitHub 下载统一入口：自动判断「下文件」还是「拉仓库」。

.DESCRIPTION
    按输入形态分流：
      .../releases/download/...  或  带文件扩展名   -> gh-dl.ps1（多连接下文件）
      github.com/owner/repo      或  owner/repo     -> gh-clone.ps1（多连接拉仓库）

.PARAMETER Target
    链接或 owner/repo。

.PARAMETER OutDir
    输出目录。

.EXAMPLE
    .\gh.ps1 -Target https://github.com/owner/repo/releases/download/v1/x.zip
    .\gh.ps1 -Target owner/repo
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Target,
    [string]$OutDir = (Join-Path $env:USERPROFILE 'Downloads\GitHub')
)

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$text = $Target.Trim().Trim('"').Trim("'")
if ([string]::IsNullOrWhiteSpace($text)) { throw '没有拿到链接。' }

# 去掉尾部的多余符号
$text = $text.TrimEnd('。', '，', ',', ';', '；')

$dlScript    = Join-Path $PSScriptRoot 'gh-dl.ps1'
$cloneScript = Join-Path $PSScriptRoot 'gh-clone.ps1'
foreach ($s in @($dlScript, $cloneScript)) {
    if (-not (Test-Path $s)) { throw "缺少脚本: $s" }
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$fileExt = '\.(zip|tar\.gz|tgz|7z|rar|exe|msi|whl|apk|jar|dmg|deb|rpm|bin|iso|pdf|json|yml|yaml)$'
$isRelease = ($text -match '/releases/download/')
$isBareRepo = ($text -match '^[\w.\-]+/[\w.\-]+$')
$isRepoUrl = ($text -match '^https?://github\.com/[\w.\-]+/[\w.\-]+/?$')
$hasFileExt = ($text -match "(?i)$fileExt")

Write-Host ''
if ($isRelease -or ($hasFileExt -and -not $isBareRepo -and -not $isRepoUrl)) {
    Write-Host '模式: 下载文件' -ForegroundColor Cyan
    & $dlScript $text -OutDir $OutDir
}
elseif ($text -match '^https?://github\.com/([\w.\-]+)/([\w.\-]+)/releases(?:/tag/([^/?#]+))?/?$') {
    # release 页面链接：自动解析出附件，逐个下载
    $owner = $Matches[1]; $repo = $Matches[2]; $tag = $Matches[3]
    Write-Host ("模式: 解析 release 附件（{0}/{1} {2}）" -f $owner, $repo, $(if($tag){"@$tag"}else{"latest"})) -ForegroundColor Cyan

    $apiUrl = if ($tag) { "https://api.github.com/repos/$owner/$repo/releases/tags/$tag" }
              else     { "https://api.github.com/repos/$owner/$repo/releases/latest" }

    $raw = & curl.exe -s --max-time 25 -H "Accept: application/vnd.github+json" $apiUrl 2>$null
    if (-not $raw) {
        $proxy = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue).ProxyServer
        if ($proxy) { $raw = & curl.exe -s --max-time 25 --proxy "http://$proxy" -H "Accept: application/vnd.github+json" $apiUrl 2>$null }
    }
    if (-not $raw) { throw '取不到 release 信息（网络不通）。' }

    $rel = $raw | ConvertFrom-Json
    if ($rel.message) { throw "GitHub API: $($rel.message)" }

    $assets = @($rel.assets)
    if ($assets.Count -eq 0) {
        Write-Host ''
        Write-Host ("这个 release（{0}）没有任何附件。" -f $rel.tag_name) -ForegroundColor Yellow
        Write-Host '页面上能看到的 zip 多半是第三方市场重打包的，不是官方产物。' -ForegroundColor Yellow
        Write-Host '官方渠道通常在 npm：按包名在 DSH 插件页安装即可。' -ForegroundColor Yellow
        return
    }

    Write-Host ("发现 {0} 个附件：" -f $assets.Count) -ForegroundColor Green
    foreach ($a in $assets) { Write-Host ("  - {0}  ({1} MB)" -f $a.name, [math]::Round($a.size/1MB,2)) }
    Write-Host ''

    if ($assets.Count -gt 1) {
        Write-Host '输入关键词筛选（如 win / x64 / .zip），直接回车 = 全部下载：' -ForegroundColor Yellow
        $kw = Read-Host
        if (-not [string]::IsNullOrWhiteSpace($kw)) {
            $filtered = @($assets | Where-Object { $_.name -match [regex]::Escape($kw.Trim()) })
            if ($filtered.Count -eq 0) {
                Write-Host ("没有名字含「{0}」的附件，取消。" -f $kw) -ForegroundColor Red
                return
            }
            $assets = $filtered
            Write-Host ("筛选后 {0} 个：" -f $assets.Count) -ForegroundColor Green
        }
    }
    Write-Host ''

    foreach ($a in $assets) { & $dlScript $a.browser_download_url -OutDir $OutDir -OutName $a.name }
}elseif ($isBareRepo -or $isRepoUrl) {
    Write-Host '模式: 拉取仓库' -ForegroundColor Cyan
    & $cloneScript $text -OutDir $OutDir
}
else {
    Write-Host '模式: 按文件下载（未识别的链接形态）' -ForegroundColor Yellow
    & $dlScript $text -OutDir $OutDir
}

$ok = $?
Write-Host ''
if ($ok) {
    Write-Host "完成，输出目录：" -ForegroundColor Green
    Write-Host "  $OutDir" -ForegroundColor White
    try { Start-Process explorer.exe $OutDir } catch { }
} else {
    Write-Host "下载失败，请看上面的错误信息。" -ForegroundColor Red
}
