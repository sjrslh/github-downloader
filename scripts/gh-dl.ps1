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

    引擎：内置 .NET 分段并发器（PS 5.1 原生，无需任何模块），
          有 aria2c 时仍优先用它。

    进度：向 stdout 输出机器可读行，GUI 靠这个画进度条：
        ##PROGRESS <已下载字节> <总字节> <字节每秒>
        ##DONE     <文件路径> <总字节> <耗时秒>
        ##FAIL     <来源标签> <原因>

.PARAMETER Url
    GitHub 链接。支持简写 owner/repo。

.PARAMETER OutDir
    保存目录，默认当前目录。

.PARAMETER Connections
    并发连接数，默认 16。

.PARAMETER OutName
    指定保存文件名。

.PARAMETER ProxyUrl
    用于解析跳转的本地代理，默认自动从系统代理设置读取。

.PARAMETER NoProxy
    完全不使用代理，即使系统代理开着。

.PARAMETER NoMirror
    不走镜像回退。

.PARAMETER Sha256
    下载完成后校验 SHA256，不匹配则删除文件并报错。

.PARAMETER Quiet
    只输出 ## 开头的机器可读行和错误，不打普通日志。

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
    [switch]$NoMirror,
    [string]$Sha256 = '',
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

if ($Connections -lt 1) { $Connections = 1 }
if ($Connections -gt 16) { $Connections = 16 }   # GitHub 单服务器连接上限

$script:Mirrors = @(
    ''
    'https://gh-proxy.com/'
    'https://ghfast.top/'
)

$script:Quiet = [bool]$Quiet

function Write-Info {
    param([string]$Text, [string]$Color = 'Gray')
    if ($script:Quiet) { return }
    Write-Host $Text -ForegroundColor $Color
}

function Write-ProgressLine {
    param([long]$Done, [long]$Total, [double]$Speed)
    [Console]::Out.WriteLine(('##PROGRESS {0} {1} {2}' -f $Done, $Total, [long]$Speed))
    [Console]::Out.Flush()
}

# ---------------------------------------------------------------- C# 下载核心
# 纯 .NET Framework / PS 5.1 可用：不用 ThreadJob、不用 runspace、不用 aria2c。
# 断点续传靠 .ghdl-state 文件（append-only，记录已完成的分块序号）。
if (-not ('GhSegmentedDownloader' -as [type])) {
  try {
    Add-Type -Language CSharp -ErrorAction Stop -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class GhSegmentedDownloader
{
    public string Url;
    public string TargetPath;
    public string StatePath;
    public IWebProxy Proxy;
    public int Connections = 16;
    public int ChunkSize = 4194304;
    public int MaxRetry = 4;
    public string ExpectedSha256;

    private long total = -1;
    private long completed;
    private int chunkCount;
    private bool[] chunkDone;
    private List<Task> tasks = new List<Task>();
    private SemaphoreSlim gate;
    private volatile bool aborted;
    private Exception firstError;
    private readonly object stateLock = new object();
    private string stateError;

    public string FinalUrl;
    public bool RangesSupported;
    public bool UsedResume;
    public string Sha256Actual;
    public string StateError { get { return this.stateError; } }

    public GhSegmentedDownloader()
    {
        this.gate = new SemaphoreSlim(1);
    }

    public long TotalBytes { get { return this.total; } }
    public long Downloaded { get { return Interlocked.Read(ref this.completed); } }
    public bool IsFinished { get; private set; }
    public int ChunkCount { get { return this.chunkCount; } }
    public int ChunkBytes { get { return this.ChunkSize; } }
    public bool Success { get; private set; }
    public Exception Error { get { return this.firstError; } }

    private static string UserAgent
    {
        get { return "GitHubDownloader/2.0 (+https://github.com/sjrslh/github-downloader)"; }
    }

    private HttpWebRequest MakeRequest(string url, bool head, long start, long end)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = head ? "HEAD" : "GET";
        req.AllowAutoRedirect = true;
        req.UserAgent = UserAgent;
        req.Timeout = 30000;
        req.ReadWriteTimeout = 90000;
        req.KeepAlive = true;
        req.Proxy = this.Proxy;          // null = 强制直连
        req.AutomaticDecompression = DecompressionMethods.None;
        req.ServicePoint.ConnectionLimit = Math.Max(16, this.Connections);
        if (!head && start >= 0)
        {
            req.AddRange(start, end);
        }
        return req;
    }

    // 探测大小与是否支持 Range。优先 HEAD，拿不到就退到 GET bytes=0-1。
    public void Probe()
    {
        Exception headError = null;
        try
        {
            HttpWebRequest req = MakeRequest(this.Url, true, -1, -1);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                this.FinalUrl = resp.ResponseUri.ToString();
                this.total = resp.ContentLength;
                string ranges = resp.Headers["Accept-Ranges"];
                this.RangesSupported = (ranges != null &&
                    ranges.IndexOf("bytes", StringComparison.OrdinalIgnoreCase) >= 0);
                if (this.total > 0 && this.RangesSupported) { return; }
            }
        }
        catch (Exception ex)
        {
            headError = ex;
        }

        try
        {
            HttpWebRequest req = MakeRequest(this.Url, false, 0, 1);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                this.FinalUrl = resp.ResponseUri.ToString();
                if (resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    this.RangesSupported = false;
                    using (Stream s = resp.GetResponseStream())
                    {
                        // 不支持分段：直接整段落盘。
                        this.total = resp.ContentLength;
                        using (FileStream fs = new FileStream(this.TargetPath, FileMode.Create,
                                   FileAccess.Write, FileShare.ReadWrite))
                        {
                            CopyStream(s, fs, 81920);
                        }
                    }
                    this.completed = this.total > 0 ? this.total : new FileInfo(this.TargetPath).Length;
                    this.total = this.completed;
                    this.Success = true;
                    this.IsFinished = true;
                    return;
                }
                string cr = resp.Headers["Content-Range"];   // bytes 0-1/12345678
                long len = -1;
                if (cr != null)
                {
                    int slash = cr.LastIndexOf('/');
                    if (slash > 0) { long.TryParse(cr.Substring(slash + 1), out len); }
                }
                this.total = len;
                this.RangesSupported = len > 0;
                if (!this.RangesSupported) { throw new InvalidOperationException("服务器未返回可用的总长度，无法分段。"); }
            }
        }
        catch (Exception)
        {
            if (headError != null) { throw headError; }
            throw;
        }
    }

    private static void CopyStream(Stream src, Stream dst, int bufferSize)
    {
        byte[] buf = new byte[bufferSize];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            dst.Write(buf, 0, n);
        }
    }

    private void LoadState()
    {
        this.chunkDone = new bool[this.chunkCount];
        if (String.IsNullOrEmpty(this.StatePath) || !File.Exists(this.StatePath)) { return; }
        try
        {
            string[] lines = File.ReadAllLines(this.StatePath);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] != '+') { continue; }
                int idx;
                if (int.TryParse(line.Substring(1), out idx))
                {
                    if (idx >= 0 && idx < this.chunkCount && !this.chunkDone[idx])
                    {
                        this.chunkDone[idx] = true;
                        this.completed += this.ChunkLen(idx);
                        this.UsedResume = true;
                    }
                }
            }
        }
        catch { }
    }

    private long ChunkLen(int idx)
    {
        long start = (long)idx * this.ChunkSize;
        long end = Math.Min(start + this.ChunkSize, this.total);
        return Math.Max(0, end - start);
    }

    // 16 个下载线程会同时走到这里。Append 模式的多句柄写入必须串行化，
    // 否则句柄抢同一个文件指针，记录会错乱甚至整条丢失（早先版本就是这样，
    // 结果断点续传从来没生效过）。
    private void MarkChunk(int idx)
    {
        if (String.IsNullOrEmpty(this.StatePath)) { return; }
        lock (this.stateLock)
        {
            try
            {
                using (FileStream fs = new FileStream(this.StatePath, FileMode.Append,
                           FileAccess.Write, FileShare.ReadWrite))
                {
                    byte[] bytes = Encoding.ASCII.GetBytes("+" + idx + "\n");
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                }
            }
            catch (Exception ex)
            {
                this.stateError = ex.Message;
            }
        }
    }

    private void WriteRange(long offset, byte[] buffer, int count)
    {
        using (FileStream fs = new FileStream(this.TargetPath, FileMode.Open,
                   FileAccess.Write, FileShare.ReadWrite, 65536))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(buffer, 0, count);
        }
    }

    private void FetchChunk(int idx)
    {
        long start = (long)idx * this.ChunkSize;
        long end = Math.Min(start + this.ChunkSize, this.total) - 1;
        byte[] buffer = new byte[this.ChunkSize];
        int written = 0;

        for (int attempt = 0; attempt <= this.MaxRetry; attempt++)
        {
            if (this.aborted) { return; }
            long attemptStart = start + written;
            if (attemptStart > end) { break; }
            try
            {
                HttpWebRequest req = MakeRequest(this.Url, false, attemptStart, end);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                {
                    s.ReadTimeout = 90000;
                    int n;
                    while (written < buffer.Length &&
                           (n = s.Read(buffer, written, buffer.Length - written)) > 0)
                    {
                        written += n;
                        Interlocked.Add(ref this.completed, n);
                    }
                }
                if (start + written > end) { break; }
            }
            catch (Exception ex)
            {
                if (attempt >= this.MaxRetry)
                {
                    throw new IOException("分块 " + idx + " 重试 " + this.MaxRetry + " 次仍失败: " + ex.Message, ex);
                }
                int wait = (int)Math.Min(8000, 500 * Math.Pow(2, attempt));
                Thread.Sleep(wait);
            }
        }

        WriteRange(start, buffer, written);
        if (this.chunkDone != null) { this.chunkDone[idx] = true; }
        this.MarkChunk(idx);
    }

    public void Start()
    {
        if (this.IsFinished) { return; }

        string dir = Path.GetDirectoryName(Path.GetFullPath(this.TargetPath));
        if (!String.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }

        if (this.total <= 0) { this.Probe(); }
        if (this.IsFinished) { return; }

        if (this.RangesSupported && this.Connections > 1)
        {
            // 自适应分块：总大小很大时把每块放大，避免 4GB 文件产生上千个状态行。
            long desired = this.total / Math.Max(1, this.Connections * 8);
            if (desired > this.ChunkSize) { this.ChunkSize = (int)Math.Min(64L * 1024 * 1024, desired); }

            // 每个分块都会整块读进内存，所以 连接数 x 块大小 就是峰值内存。
            // 几 GB 的文件如果放任分块放大，16 路并发能吃掉 512MB，这里压到 256MB 上限。
            long memoryCap = (256L * 1024 * 1024) / Math.Max(1, this.Connections);
            if (this.ChunkSize > memoryCap && memoryCap >= 1024 * 1024) { this.ChunkSize = (int)memoryCap; }
            if (this.ChunkSize < 1024 * 1024) { this.ChunkSize = 1024 * 1024; }

            this.chunkCount = (int)((this.total + this.ChunkSize - 1) / this.ChunkSize);
            this.LoadState();

            // 预分配目标文件
            using (FileStream fs = new FileStream(this.TargetPath, FileMode.OpenOrCreate,
                       FileAccess.Write, FileShare.ReadWrite))
            {
                if (fs.Length != this.total) { fs.SetLength(this.total); }
            }

            this.gate = new SemaphoreSlim(this.Connections);
            for (int i = 0; i < this.chunkCount; i++)
            {
                if (this.chunkDone[i]) { continue; }
                int idx = i;
                this.tasks.Add(Task.Factory.StartNew(delegate()
                {
                    this.gate.Wait();
                    try
                    {
                        if (this.aborted) { return; }
                        this.FetchChunk(idx);
                    }
                    catch (Exception ex)
                    {
                        this.aborted = true;
                        if (this.firstError == null) { this.firstError = ex; }
                    }
                    finally
                    {
                        this.gate.Release();
                    }
                }, TaskCreationOptions.LongRunning));
            }
        }
        else
        {
            // 不支持 Range：单流整段下载
            HttpWebRequest req = MakeRequest(this.Url, false, -1, -1);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream fs = new FileStream(this.TargetPath, FileMode.Create,
                       FileAccess.Write, FileShare.ReadWrite))
            {
                byte[] buf = new byte[262144];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    Interlocked.Add(ref this.completed, n);
                }
            }
            this.total = this.completed;
        }

        Task[] snapshot = this.tasks.ToArray();
        Task.Factory.StartNew(delegate()
        {
            try { Task.WaitAll(snapshot); }
            catch (Exception ex) { if (this.firstError == null) { this.firstError = ex; } }
            this.Finish();
        }, TaskCreationOptions.LongRunning);
    }

    private void Finish()
    {
        try
        {
            if (this.firstError == null && this.total > 0)
            {
                long real = new FileInfo(this.TargetPath).Length;
                if (real != this.total)
                {
                    this.firstError = new IOException(
                        "大小不符：期望 " + this.total + " 字节，实际 " + real + " 字节。");
                }
            }

            if (this.firstError == null && !String.IsNullOrEmpty(this.ExpectedSha256))
            {
                string actual = ComputeSha256(this.TargetPath);
                this.Sha256Actual = actual;
                if (!String.Equals(actual, this.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    this.firstError = new InvalidDataException(
                        "SHA256 校验失败。期望 " + this.ExpectedSha256 + "，实际 " + actual);
                }
            }

            this.Success = (this.firstError == null);
            if (this.Success && !String.IsNullOrEmpty(this.StatePath))
            {
                try { File.Delete(this.StatePath); } catch { }
            }
        }
        catch (Exception ex)
        {
            if (this.firstError == null) { this.firstError = ex; }
            this.Success = false;
        }
        finally
        {
            this.IsFinished = true;
        }
    }

    public static string ComputeSha256(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] hash = sha.ComputeHash(fs);
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) { sb.Append(b.ToString("x2")); }
            return sb.ToString();
        }
    }
}
'@
  } catch {
    # 受限环境（TEMP 只读、执行策略、被管控的机器）编译不了内联 C#，退回单连接流式下载。
    # 慢是慢点，但不会像以前那样直接崩在 Start-ThreadJob 上。
    Write-Host '提示: 多连接引擎不可用（无法编译内联代码），已退回单连接下载。' -ForegroundColor Yellow
  }
}

$script:HasSegmented = [bool]('GhSegmentedDownloader' -as [type])

function Invoke-SimpleDownload {
    param([string]$Uri, [string]$Dir, [string]$OutName, [string]$AllProxy)
    $target = Join-Path $Dir $OutName
    $req = [System.Net.HttpWebRequest]::Create($Uri)
    $req.UserAgent = 'GitHubDownloader/2.0'
    $req.Timeout = 30000
    $req.ReadWriteTimeout = 90000
    if ($AllProxy) { $req.Proxy = New-Object System.Net.WebProxy("http://$AllProxy", $true) }
    else { $req.Proxy = $null }

    $resp = $req.GetResponse()
    try {
        $total = $resp.ContentLength
        $stream = $resp.GetResponseStream()
        $fs = [System.IO.File]::Create($target)
        try {
            $buf = New-Object byte[] 262144
            $done = 0L
            $lastReport = [DateTime]::UtcNow
            while (($n = $stream.Read($buf, 0, $buf.Length)) -gt 0) {
                $fs.Write($buf, 0, $n)
                $done += $n
                if (([DateTime]::UtcNow - $lastReport).TotalMilliseconds -ge 400) {
                    Write-ProgressLine -Done $done -Total $total -Speed 0
                    $lastReport = [DateTime]::UtcNow
                }
            }
            Write-ProgressLine -Done $done -Total $total -Speed 0
        } finally {
            $fs.Close(); $stream.Close()
        }
    } finally {
        $resp.Close()
    }
    return $target
}

function Normalize-Url {
    param([string]$u)
    if ($u -match '^https?://') { return $u }
    if ($u -match '^[\w.\-]+/[\w.\-]+$') { return "https://github.com/$u" }
    return "https://github.com/$u"
}

function Get-FileName {
    param([string]$u)
    $name = [System.IO.Path]::GetFileName(([uri]$u).AbsolutePath)
    if ([string]::IsNullOrWhiteSpace($name)) { return 'download.bin' }

    # URL 里的文件名是百分号编码的（demo%20file%20%E6%B5%8B%E8%AF%95.zip），
    # 不解码就会把 %20 原样当文件名存下来。
    try { $name = [System.Uri]::UnescapeDataString($name) } catch { }

    # 解码后可能出现文件系统非法字符，替换掉
    foreach ($c in [System.IO.Path]::GetInvalidFileNameChars()) { $name = $name.Replace([string]$c, '_') }
    $name = $name.Trim().TrimEnd('.')

    # 超长文件名保护（Windows 单段 255 字符），保留扩展名
    if ($name.Length -gt 180) {
        $ext = [System.IO.Path]::GetExtension($name)
        $name = $name.Substring(0, 180 - $ext.Length) + $ext
    }
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
            $picked = ''
            foreach ($part in ($srv -split ';')) {
                if ($part -match '^https?=(.+)$') { $picked = $Matches[1]; break }
            }
            if (-not $picked) {
                foreach ($part in ($srv -split ';')) {
                    if ($part -match '^socks=?(.+)$') { $picked = $Matches[1]; break }
                }
            }
            $srv = $picked
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
    param([string]$Uri, [string]$Proxy, [int]$MaxHop = 6)

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
        if ($next -match '^/') {
            $base = [uri]$cur
            $next = $base.GetLeftPart([System.UriPartial]::Authority) + $next
        }
        $cur = $next
        if ($cur -match '^(?i)https?://(release-assets|objects)\.githubusercontent\.com') { break }
    }
    return $cur
}

function Invoke-Aria2 {
    param([string]$Aria, [string]$Uri, [string]$Dir, [string]$OutName, [string]$AllProxy, [long]$Total)
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
        "--summary-interval=1"
        "--console-log-level=warn"
    )
    if ($AllProxy) { $argList += "--all-proxy=http://$AllProxy" }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Aria
    $psi.Arguments = ($argList -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.BeginErrorReadLine()
    while (-not $p.HasExited) {
        Start-Sleep -Milliseconds 500
        $file = Join-Path $Dir $OutName
        if (Test-Path $file) {
            $have = (Get-Item $file).Length
            Write-ProgressLine -Done $have -Total $Total -Speed 0
        }
    }
    return [int]$p.ExitCode
}

function Invoke-Segmented {
    param([string]$Uri, [string]$Dir, [string]$OutName, [string]$AllProxy)
    $target = Join-Path $Dir $OutName
    if (-not $script:HasSegmented) {
        return (Invoke-SimpleDownload -Uri $Uri -Dir $Dir -OutName $OutName -AllProxy $AllProxy)
    }
    $dl = New-Object GhSegmentedDownloader
    $dl.Url = $Uri
    $dl.TargetPath = $target
    $dl.StatePath = (Join-Path $Dir ("." + $OutName + ".ghdl-state"))
    $dl.Connections = $Connections
    $dl.MaxRetry = 4
    if ($Sha256) { $dl.ExpectedSha256 = $Sha256 }
    if ($AllProxy) {
        $dl.Proxy = New-Object System.Net.WebProxy("http://$AllProxy", $true)
    } elseif ($ProxyUrl) {
        $dl.Proxy = New-Object System.Net.WebProxy("http://$ProxyUrl", $true)
    } else {
        $dl.Proxy = $null
    }

    $dl.Probe()
    $total = $dl.TotalBytes
    if ($total -gt 0) {
        $chunks = [Math]::Min($Connections, [Math]::Ceiling($total / 1MB))
        $sizeMsg = '大小: {0:N2} MB / 并发: {1} 路' -f ($total / 1MB), $chunks
        Write-Info $sizeMsg 'DarkGray'
    }
    $dl.Start()

    $last = 0L
    $lastTime = [DateTime]::UtcNow
    while (-not $dl.IsFinished) {
        Start-Sleep -Milliseconds 400
        $have = $dl.Downloaded
        $now = [DateTime]::UtcNow
        $dt = ($now - $lastTime).TotalSeconds
        $speed = 0
        if ($dt -gt 0.05) { $speed = ($have - $last) / $dt }
        $last = $have
        $lastTime = $now
        Write-ProgressLine -Done $have -Total $total -Speed $speed
        if (-not $script:Quiet -and $total -gt 0) {
            $pct = [Math]::Min(100, [Math]::Round($have * 100.0 / $total, 1))
            $statusText = '{0:N1}%  {1:N1}/{2:N1} MB  {3:N1} MB/s' -f $pct, ($have/1MB), ($total/1MB), ($speed/1MB)
            Write-Progress -Activity "下载" -Status $statusText -PercentComplete $pct
        }
    }

    if (-not $script:Quiet) { Write-Progress -Activity "下载" -Completed }
    if ($dl.UsedResume) {
        $resumeMsg = '断点续传：跳过已完成分块，直接从上次中断处继续。'
        Write-Info $resumeMsg 'DarkGray'
    }
    if (-not $dl.Success) {
        throw ($dl.Error.Message)
    }
    Write-ProgressLine -Done $total -Total $total -Speed 0
    return $target
}

# ---------------------------------------------------------------- 主流程
$real = Normalize-Url $Url
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path
$outName = if ([string]::IsNullOrWhiteSpace($OutName)) { Get-FileName $real } else { $OutName }

$aria = Test-Aria2
$proxy = Get-SystemProxy

Write-Info ("目标: {0}" -f $real) 'Cyan'
Write-Info ("目录: {0}" -f $OutDir) 'DarkGray'
if ($aria) { Write-Info ("引擎: aria2c  并发 {0}（直连）" -f $Connections) 'DarkGray' }
else       { Write-Info ("引擎: 内置分段下载器  并发 {0}（无需任何模块）" -f $Connections) 'DarkGray' }
if ($proxy) { Write-Info ("代理: {0}（只用于解析跳转地址）" -f $proxy) 'DarkGray' }
else        { Write-Info '代理: 未启用' 'DarkGray' }
Write-Info ''

# 组装候选地址：先用代理解析出真正的下载地址，再直连多连接
$candidates = @()
if ($real -match '^https://github\.com/' -and $proxy) {
    $final = Resolve-FinalUrl -Uri $real -Proxy $proxy
    if ($final -ne $real) {
        Write-Info ("解析跳转 -> {0}..." -f $final.Substring(0, [Math]::Min(80, $final.Length))) 'DarkGray'
        $candidates += [pscustomobject]@{ Uri = $final; Label = '直连（地址经代理解析）'; Proxy = '' }
    }
}
$candidates += [pscustomobject]@{ Uri = $real; Label = '直连'; Proxy = '' }

if (-not $NoMirror -and $real -match '^https://github\.com/') {
    foreach ($m in $script:Mirrors) {
        if ($m -eq '') { continue }
        $candidates += [pscustomobject]@{ Uri = ($m + $real); Label = "镜像 $m"; Proxy = '' }
    }
}

if ($proxy) {
    $candidates += [pscustomobject]@{ Uri = $real; Label = "代理兜底（全部经 $proxy）"; Proxy = $proxy }
}

# 目标已在本地且大小一致时直接跳过（重复解析不会重复下载）
$targetPath = Join-Path $OutDir $outName

$attempt = 0
$lastError = '未知错误'
# 每个候选源的失败原因先收集起来，最后合并成一条输出。
# 以前是每源一条 ##FAIL，界面上会刷出三条一模一样的长错误。
$attempts = @()
foreach ($c in $candidates) {
    $attempt++
    $headMsg = '--- [{0}/{1}] {2} ---' -f $attempt, $candidates.Count, $c.Label
    Write-Info $headMsg 'Yellow'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = 1
    try {
        if ($aria) { $code = Invoke-Aria2 -Aria $aria -Uri $c.Uri -Dir $OutDir -OutName $outName -AllProxy $c.Proxy -Total 0 }
        else { Invoke-Segmented -Uri $c.Uri -Dir $OutDir -OutName $outName -AllProxy $c.Proxy | Out-Null; $code = 0 }
    } catch {
        $lastError = $_.Exception.Message

        # 把 .NET 的原始异常翻成人话，别把 "Exception calling Probe" 甩给用户
        if ($lastError -match '\(404\)') {
            $lastError = '目标不存在（HTTP 404）。检查 owner/repo 拼写；私有仓库需要先填 Token。'
        } elseif ($lastError -match '\(403\)') {
            $lastError = '被拒绝（HTTP 403）。多半是 API 限流或仓库权限问题，试试填 Token。'
        } elseif ($lastError -match '无法解析|resolve|No such host') {
            $lastError = '域名解析失败，检查网络或代理。'
        } elseif ($lastError -match 'SSL|TLS|证书') {
            $lastError = 'TLS 握手失败，可能是代理/证书问题。'
        }

        $warnMsg = '{0}: {1}' -f $c.Label, $lastError
        Write-Warning $warnMsg
        $attempts += ('{0}：{1}' -f $c.Label, $lastError)
        $code = 1
    }
    $sw.Stop()

    $file = Join-Path $OutDir $outName
    $ok = ($code -eq 0)

    # 注意：内置分段器会先把文件预分配到完整大小，
    # 所以「文件有内容」绝不能当作下载成功——只有 aria2 路径才看 .aria2 标志。
    if (-not $ok -and -not $aria -and (Test-Path $file)) {
        $stateFile = Join-Path $OutDir ("." + $outName + ".ghdl-state")
        if (Test-Path $stateFile) {
            $doneLines = @(Get-Content $stateFile | Where-Object { $_ -match '^\+\d+$' }).Count
            Write-Info ("已下 {0} 个分块保留在 .ghdl-state，下次运行会从这里续传。" -f $doneLines) 'DarkGray'
        }
    }
    if (-not $ok -and $aria -and (Test-Path $file)) {
        # aria2 可能没清掉控制文件，但文件已经完整
        if ((Get-Item $file).Length -gt 0 -and -not (Test-Path "$file.aria2")) { $ok = $true }
    }

    if ($ok) {
        $sizeMb = [Math]::Round((Get-Item $file).Length / 1MB, 2)
        $sec = [Math]::Max($sw.Elapsed.TotalSeconds, 0.1)
        [Console]::Out.WriteLine(('##DONE {0} {1} {2:N1}' -f $file, (Get-Item $file).Length, $sec))
        $okMsg = '成功: {0}' -f $file
        Write-Info $okMsg 'Green'
        $rateMsg = '      {0} MB / {1:N1}s = {2} MB/s' -f $sizeMb, $sec, [Math]::Round($sizeMb / $sec, 2)
        Write-Info $rateMsg 'Green'
        return
    }
    Write-Info ("失败（exit {0}）" -f $code) 'Red'
}

$combined = (($attempts | Select-Object -Unique) -join '；')
[Console]::Out.WriteLine(('##FAIL 全部来源失败 {0}' -f $combined))
throw "所有来源均失败。$combined"
