<#
.SYNOPSIS
    GitHub 下载统一入口：自动判断「下文件」还是「拉仓库」。

.DESCRIPTION
    按输入形态分流：
      .../releases/download/...  或  带文件扩展名   -> gh-dl.ps1（多连接下文件）
      .../releases/tag/...                         -> 解析附件后逐个下载
      github.com/owner/repo      或  owner/repo     -> gh-clone.ps1（多连接拉仓库）

    支持 GITHUB_TOKEN（环境变量或 settings.ini 里的 githubToken），
    带上后 API 限额从 60 次/小时提到 5000 次/小时。

.PARAMETER Target
    链接或 owner/repo。

.PARAMETER OutDir
    输出目录。

.PARAMETER Quiet
    只输出 ## 开头的机器可读行（GUI 用）。

.PARAMETER NonInteractive
    不向用户提问；release 有多个附件时全部下载。

.PARAMETER NoOpen
    完成后不弹资源管理器。

.EXAMPLE
    .\gh.ps1 -Target https://github.com/owner/repo/releases/download/v1/x.zip
    .\gh.ps1 -Target owner/repo
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Target,
    [string]$OutDir = '',
    [int]$Connections = 16,
    [string]$Sha256 = '',
    [string]$Name = '',
    [switch]$Quiet,
    [switch]$NonInteractive,
    [switch]$NoOpen
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$script:Quiet = [bool]$Quiet
# GUI 调用时 stdin 不可用，任何 Read-Host 都会把界面挂死
$script:Interactive = (-not $NonInteractive) -and (-not [Console]::IsInputRedirected)

function Write-Info {
    param([string]$Text, [string]$Color = 'Gray')
    if ($script:Quiet) { return }
    Write-Host $Text -ForegroundColor $Color
}

function Get-DownloadsFolder {
    <# 系统的「下载」文件夹。硬拼 %USERPROFILE%\Downloads 在重定向过的机器上是错的。 #>
    try {
        $k = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' -ErrorAction Stop
        $v = $k.'{374DE290-123F-4565-9164-39C4925E467B}'
        if ($v) {
            $p = [Environment]::ExpandEnvironmentVariables([string]$v)
            if ($p) { return $p }
        }
    } catch { }
    return (Join-Path $env:USERPROFILE 'Downloads')
}

function Get-GitHubToken {
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN.Trim() }
    if ($env:GH_TOKEN) { return $env:GH_TOKEN.Trim() }
    $ini = Join-Path $env:LOCALAPPDATA 'GitHub下载器\settings.ini'
    if (Test-Path $ini) {
        foreach ($line in (Get-Content -LiteralPath $ini -Encoding UTF8)) {
            if ($line -match '^\s*githubToken\s*=\s*(.+)$') { return $Matches[1].Trim() }
        }
    }
    return ''
}

$text = $Target.Trim().Trim('"').Trim("'")
if ([string]::IsNullOrWhiteSpace($text)) { throw '没有拿到链接。' }

# 去掉中文标点尾巴（从聊天记录里直接粘贴时很常见）
$text = $text.TrimEnd('。', '，', ',', ';', '；')

$dlScript    = Join-Path $PSScriptRoot 'gh-dl.ps1'
$cloneScript = Join-Path $PSScriptRoot 'gh-clone.ps1'
foreach ($s in @($dlScript, $cloneScript)) {
    if (-not (Test-Path $s)) { throw "缺少脚本: $s" }
}

if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Get-DownloadsFolder }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$fileExt = '\.(zip|tar\.gz|tgz|7z|rar|exe|msi|whl|apk|jar|dmg|deb|rpm|bin|iso|pdf|json|yml|yaml)$'
$isRelease   = ($text -match '/releases/download/')
$isTagPage   = ($text -match '^https?://github\.com/[\w.\-]+/[\w.\-]+/releases(?:/tag/[^/?#]+)?/?$')
$isBareRepo  = ($text -match '^[\w.\-]+/[\w.\-]+$')
$isRepoUrl   = ($text -match '^https?://github\.com/[\w.\-]+/[\w.\-]+/?$')
$hasFileExt  = ($text -match "(?i)$fileExt")

$dlArgs = @{ OutDir = $OutDir; Connections = $Connections; Quiet = $Quiet }
if ($Sha256) { $dlArgs.Sha256 = $Sha256 }
# GUI 勾选的是 release 附件，文件名要以附件名为准，而不是从 URL 尾巴里猜
if ($Name) { $dlArgs.OutName = $Name }

Write-Info ''

if ($isRelease -or ($hasFileExt -and -not $isBareRepo -and -not $isRepoUrl)) {
    Write-Info '模式: 下载文件' 'Cyan'
    & $dlScript $text @dlArgs
    return
}

if ($isTagPage -or ($text -match '^https?://github\.com/([\w.\-]+)/([\w.\-]+)/releases(?:/tag/([^/?#]+))?/?$')) {
    $null = $text -match '^https?://github\.com/([\w.\-]+)/([\w.\-]+)/releases(?:/tag/([^/?#]+))?/?$'
    $owner = $Matches[1]; $repo = $Matches[2]; $tag = $Matches[3]
    $label = if ($tag) { "@$tag" } else { 'latest' }
    Write-Info ("模式: 解析 release 附件（{0}/{1} {2}）" -f $owner, $repo, $label) 'Cyan'

    $apiUrl = if ($tag) { "https://api.github.com/repos/$owner/$repo/releases/tags/$tag" }
              else     { "https://api.github.com/repos/$owner/$repo/releases/latest" }

    $token = Get-GitHubToken
    $headers = @('-H', 'Accept: application/vnd.github+json', '-H', "User-Agent: GitHubDownloader/2.0")
    if ($token) { $headers += @('-H', "Authorization: Bearer $token") }

    $raw = & curl.exe -s --max-time 25 @headers $apiUrl 2>$null
    if (-not $raw) {
        $proxy = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue).ProxyServer
        if ($proxy) {
            $raw = & curl.exe -s --max-time 25 --proxy "http://$proxy" @headers $apiUrl 2>$null
        }
    }
    if (-not $raw) { throw '取不到 release 信息（网络不通）。' }

    $rel = $raw | ConvertFrom-Json
    if ($rel.message) {
        if ([string]$rel.message -match 'rate limit') {
            throw 'GitHub API 限流了（未登录每小时 60 次）。在设置里填一个 Personal Access Token 可以提到 5000 次。'
        }
        throw "GitHub API: $($rel.message)"
    }

    $assets = @($rel.assets)
    if ($assets.Count -eq 0) {
        Write-Info ("这个 release（{0}）没有任何附件。" -f $rel.tag_name) 'Yellow'
        Write-Info '如果它只在源码里，用 owner/repo 拉整个仓库。' 'Yellow'
        return
    }

    Write-Info ("发现 {0} 个附件：" -f $assets.Count) 'Green'
    foreach ($a in $assets) {
        Write-Info ("  - {0}  ({1} MB)" -f $a.name, [math]::Round($a.size/1MB,2))
    }
    Write-Info ''

    if ($assets.Count -gt 1 -and $script:Interactive) {
        Write-Info '输入关键词筛选（如 win / x64 / .zip），直接回车 = 全部下载：' 'Yellow'
        $kw = Read-Host
        if (-not [string]::IsNullOrWhiteSpace($kw)) {
            $filtered = @($assets | Where-Object { $_.name -match [regex]::Escape($kw.Trim()) })
            if ($filtered.Count -eq 0) {
                Write-Info ("没有名字含「{0}」的附件，取消。" -f $kw) 'Red'
                return
            }
            $assets = $filtered
            Write-Info ("筛选后 {0} 个：" -f $assets.Count) 'Green'
        }
    }
    Write-Info ''

    # 单个附件失败不能拖垮整批：逐个下，记失败的，最后一起报
    $failed = 0
    $total = @($assets).Count
    foreach ($a in $assets) {
        try {
            & $dlScript $a.browser_download_url @dlArgs -OutName $a.name
        } catch {
            $failed++
            [Console]::Out.WriteLine(('##FAIL {0} {1}' -f $a.name, $_.Exception.Message))
            if (-not $Quiet) { Write-Warning ("{0} 下载失败：{1}" -f $a.name, $_.Exception.Message) }
        }
    }
    if ($failed -gt 0) {
        if (-not $Quiet) { Write-Warning ("{0}/{1} 个附件失败" -f $failed, $total) }
        $script:LastBatchFailed = $failed
    }
    if (-not $NoOpen -and -not $Quiet) {
        try { Start-Process explorer.exe $OutDir } catch { }
    }
    return
}

if ($isBareRepo -or $isRepoUrl) {
    Write-Info '模式: 拉取仓库' 'Cyan'
    & $cloneScript $text -OutDir $OutDir -Connections $Connections
    return
}

Write-Info '模式: 按文件下载（未识别的链接形态）' 'Yellow'
& $dlScript $text @dlArgs
