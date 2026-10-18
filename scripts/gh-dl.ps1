<#
.SYNOPSIS
    GitHub 多连接下载器，加速器开着也能跑满速度。

.DESCRIPTION
    两件事分开处理，这是本机实测出来的结论：

    1) 速度靠「直连 + 多连接」
       单连接 ~0.15~0.3 MB/s 且抖动大；16 并发直连实测 10.6 MB/s。
       加速器的代理链路 16 并发封顶约 2.2 MB/s（节点限速），
       所以下载必须走直连，不能走代理。

    2) 连通性靠「代理只取地址」
       加速器开着时 github.com 主站 IP（20.205.243.166）TCP 443 不通，
       但 codeload / objects / raw 的 IP 都可直连。
       release 下载要先经 github.com 拿 302，于是：
       用本地代理发一个 HEAD 拿到最终地址 -> 之后全程直连多连接下载。
       代理只承担几百字节的头部，不承担正文字节。

    引擎：aria2c（-x16）优先，缺失时回退内置分段并发器，最后回退镜像。

.PARAMETER Url
    GitHub 链接。支持简写 owner/repo。

.PARAMETER OutDir
    保存目录，默认当前目录。

.PARAMETER Connections
    并发连接数，默认 16（GitHub 单服务器上限）。

.PARAMETER OutName
    指定保存文件名。

.PARAMETER ProxyUrl
    用于解析跳转的本地代理，默认自动从系统代理设置读取（如 127.0.0.1:10810）。

.PARAMETER NoProxy
    完全不使用代理，即使系统代理开着。

.PARAMETER NoMirror
    不走镜像回退。

.EXAMPLE
    .\gh-dl.ps1 https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.1/PortableGit-2.47.1-64-bit.7z.exe
    .\gh-dl.ps1 owner/repo -OutDir D:\tmp -NoProxy
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Url,
    [string]$OutDir = (Get-Location).Path,
    [int]$Connections = 16,
    [string]$OutName = '',
    [string]$ProxyUrl = '',
    [switch]$NoProxy,
    [switch]$NoMirror
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($Connections -lt 1) { $Connections = 1 }
if ($Connections -gt 16) { $Connections = 16 }   # GitHub 单服务器连接上限

$script:Mirrors = @(
    ''
    'https://gh-proxy.com/'
    'https://ghproxy.net/'
)

# ---------------------------------------------------------------- 工具
function Normalize-Url {
    param([string]$u)
    if ($u -match '^https?://') { return $u }
    if ($u -match '^[\w.\-]+/[\w.\-]+$') { return "https://github.com/$u" }
    return "https://github.com/$u"
}

function Get-FileName {
    param([string]$u)
    $name = [System.IO.Path]::GetFileName(([uri]$u).AbsolutePath)
    if ([string]::IsNullOrWhiteSpace($name)) { $name = 'download.bin' }
    return $name
}

function Test-Aria2 {
    $c = Get-Command aria2c -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @(
            "$env:LOCALAPPDATA\Microsoft\WinGet\Links\aria2c.exe",
            "$env:ProgramFiles\aria2\aria2c.exe",
            "$env:LOCALAPPDATA\Programs\aria2\aria2c.exe")) {
        if (Test-Path $p) { return $p }
    }
    $wg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path $wg) {
        $hit = Get-ChildItem $wg -Recurse -Filter 'aria2c.exe' -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

function Get-SystemProxy {
    <# 从系统代理设置里读出一个可用的本地代理，读不到返回空 #>
    if ($NoProxy) { return '' }
    if ($ProxyUrl) { return $ProxyUrl }
    try {
        $reg = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction Stop
        if ($reg.ProxyEnable -ne 1 -or -not $reg.ProxyServer) { return '' }
        $srv = [string]$reg.ProxyServer
        if ($srv -match '=') {
            # 形如 http=127.0.0.1:10810;https=127.0.0.1:10810
            foreach ($part in ($srv -split ';')) {
                if ($part -match '^(https?)=(.+)$' -and $Matches[1] -eq 'https') { $srv = $Matches[2]; break }
            }
        }
        $hostPart = ($srv -split ':')[0]
        $portPart = ($srv -split ':')[1]
        if (-not $portPart) { return '' }
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $client.Connect($hostPart, [int]$portPart)
            return "$hostPart`:$portPart"
        } finally { $client.Close() }
    } catch { return '' }
}

function Resolve-FinalUrl {
    <# 经代理逐跳解析 302，返回真正的下载地址。代理只承担头部请求。 #>
    param([string]$Uri, [string]$Proxy, [int]$MaxHop = 5)

    if (-not $Proxy) { return $Uri }
    $cur = $Uri
    for ($i = 0; $i -lt $MaxHop; $i++) {
        $curlArgs = @('-s', '-I', '--max-time', '25', '--proxy', "http://$Proxy", $cur)
        $raw = & curl.exe @curlArgs 2>$null
        if (-not $raw) { return $cur }
        $line = $raw | Where-Object { $_ -match '^(?i)location:\s*(.+)$' } | Select-Object -First 1
        if (-not $line) { return $cur }
        $next = ($line -replace '^(?i)location:\s*', '').Trim()
        if ([string]::IsNullOrWhiteSpace($next)) { return $cur }
        $cur = $next
        if ($cur -match '^(?i)https?://(release-assets|objects)\.githubusercontent\.com') { break }
    }
    return $cur
}

function Invoke-Aria2 {
    param([string]$Aria, [string]$Uri, [string]$Dir, [string]$OutName, [string]$AllProxy = '')
    $argList = @(
        "`"$Uri`""
        "--dir=`"$Dir`""
        "--out=$OutName"
        "--split=$Connections"
        "--max-connection-per-server=$Connections"
        "--min-split-size=1M"
        "--continue=true"
        "--auto-file-renaming=false"
        "--allow-overwrite=true"
        "--file-allocation=none"
        "--remote-time=true"
        "--disable-ipv6=true"
        "--connect-timeout=20"
        "--timeout=60"
        "--max-tries=5"
        "--retry-wait=2"
        "--quiet=true"
        "--summary-interval=0"
        "--show-console-readout=false"
        "--console-log-level=error"
    )
    if ($AllProxy) { $argList += "--all-proxy=http://$AllProxy" }
    $p = Start-Process -FilePath $Aria -ArgumentList $argList -NoNewWindow -Wait -PassThru
    return [int]$p.ExitCode
}

function Invoke-RangeDownload {
    param([string]$Uri, [string]$Dir, [string]$OutName)

    $target = Join-Path $Dir $OutName
    $head = Invoke-WebRequest -Uri $Uri -Method Head -MaximumRedirection 5 -TimeoutSec 30
    $len = [int64]$head.Headers['Content-Length']
    if (-not $len -or $len -le 0) { throw '服务器未返回 Content-Length，无法分段。' }

    $chunk = [math]::Ceiling($len / $Connections)
    $tmp = Join-Path $Dir ".$OutName.part"
    $fs = [System.IO.File]::Open($tmp, 'OpenOrCreate', 'Write', 'ReadWrite')
    $fs.SetLength($len)
    $fs.Close()

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $jobs = @()
    for ($i = 0; $i -lt $Connections; $i++) {
        $start = [int64]$i * $chunk
        if ($start -ge $len) { break }
        $end = [math]::Min($start + $chunk - 1, $len - 1)
        $jobs += Start-ThreadJob -ArgumentList $Uri, $start, $end, $tmp -ScriptBlock {
            param($u, $s, $e, $file)
            $h = Invoke-WebRequest -Uri $u -Headers @{ Range = "bytes=$s-$e" } -TimeoutSec 900 -MaximumRedirection 5
            $bytes = $h.Content
            if ($bytes -is [string]) { $bytes = [System.Text.Encoding]::Default.GetBytes($bytes) }
            $stream = [System.IO.File]::Open($file, 'Open', 'Write', 'ReadWrite')
            try {
                $stream.Seek($s, 'Begin') | Out-Null
                $stream.Write($bytes, 0, $bytes.Length)
            } finally { $stream.Close() }
        }
    }
    if (-not $jobs) { throw '分段任务创建失败。' }
    $jobs | Wait-Job | Out-Null
    $jobs | Receive-Job -ErrorAction SilentlyContinue | Out-Null
    $jobs | Remove-Job -Force
    $sw.Stop()

    Move-Item -Force $tmp $target
    $mb = [math]::Round($len / 1MB, 2)
    $sec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Write-Host ("完成: {0}  {1} MB / {2}s / {3} MB/s" -f $target, $mb, $sec, [math]::Round($mb / [math]::Max($sec, 0.1), 2)) -ForegroundColor Green
}

# ---------------------------------------------------------------- 主流程
$real = Normalize-Url $Url
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path
$outName = if ([string]::IsNullOrWhiteSpace($OutName)) { Get-FileName $real } else { $OutName }

$aria = Test-Aria2
$proxy = Get-SystemProxy

Write-Host ("目标: {0}" -f $real) -ForegroundColor Cyan
Write-Host ("目录: {0}" -f $OutDir) -ForegroundColor DarkGray
if ($aria) { Write-Host ("引擎: aria2c  并发 {0}（直连）" -f $Connections) -ForegroundColor DarkGray }
else       { Write-Host ("引擎: 内置分段下载器  并发 {0}" -f $Connections) -ForegroundColor DarkGray }
if ($proxy) { Write-Host ("代理: {0}（只用于解析跳转地址）" -f $proxy) -ForegroundColor DarkGray }
else        { Write-Host '代理: 未启用' -ForegroundColor DarkGray }
Write-Host ''

# 组装候选地址：先用代理解析出真正的下载地址，再直连多连接
$candidates = @()
if ($real -match '^https://github\.com/' -and $proxy) {
    $final = Resolve-FinalUrl -Uri $real -Proxy $proxy
    if ($final -ne $real) {
        Write-Host ("解析跳转 -> {0}" -f $final.Substring(0, [Math]::Min(80, $final.Length)) + '...') -ForegroundColor DarkGray
        $candidates += [pscustomobject]@{ Uri = $final; Label = '直连（地址经代理解析）' }
    }
}
$candidates += [pscustomobject]@{ Uri = $real; Label = '直连' }

foreach ($m in $script:Mirrors) {
    if ($m -eq '') { continue }
    if ($NoMirror) { continue }
    if ($real -notmatch '^https://github\.com/') { continue }
    $candidates += [pscustomobject]@{ Uri = ($m + $real); Label = "镜像 $m" }
}

if ($proxy) {
    $candidates += [pscustomobject]@{ Uri = $real; Label = "代理兜底（全部经 $proxy）"; Proxy = $proxy }
}

$attempt = 0
foreach ($c in $candidates) {
    $attempt++
    Write-Host ("--- [{0}/{1}] {2} ---" -f $attempt, $candidates.Count, $c.Label) -ForegroundColor Yellow
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = 1
    try {
        if ($aria) { $code = Invoke-Aria2 -Aria $aria -Uri $c.Uri -Dir $OutDir -OutName $outName -AllProxy $c.Proxy }
        else { Invoke-RangeDownload -Uri $c.Uri -Dir $OutDir -OutName $outName; $code = 0 }
    } catch {
        Write-Warning $_.Exception.Message
        $code = 1
    }
    $sw.Stop()

    $file = Join-Path $OutDir $outName
    $ok = ($code -eq 0)
    if (-not $ok -and (Test-Path $file)) {
        if ((Get-Item $file).Length -gt 0 -and -not (Test-Path "$file.aria2")) { $ok = $true }
    }

    if ($ok) {
        $sizeMb = [math]::Round((Get-Item $file).Length / 1MB, 2)
        $sec = [math]::Max($sw.Elapsed.TotalSeconds, 0.1)
        Write-Host ("成功: {0}" -f $file) -ForegroundColor Green
        Write-Host ("      {0} MB / {1:N1}s = {2} MB/s" -f $sizeMb, $sec, [math]::Round($sizeMb / $sec, 2)) -ForegroundColor Green
        return
    }
    Write-Warning ("失败（exit {0}）" -f $code)
}

throw '所有来源均失败。试试 -NoProxy，或先关闭加速器再跑。'
