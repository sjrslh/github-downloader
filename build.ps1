param([switch]$Install, [switch]$Deploy)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src\Program.cs'
$icon = Join-Path $root 'assets\icon.ico'
$manifest = Join-Path $root 'src\app.manifest'
$outDir = Join-Path $root 'dist'
$exe = Join-Path $outDir 'GitHubDownloader.exe'
$iconPng = Join-Path $root 'assets\icon.png'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $icon)) { throw "missing icon: $icon" }
if (-not (Test-Path -LiteralPath $manifest)) { throw "missing manifest: $manifest" }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# 源文件必须是 UTF-8 with BOM，否则 csc 会把中文字面量按 ANSI 读进去。
$text = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
# 防护：读到的内容异常短就中止。丢掉源码比编译失败严重得多。
if ([string]::IsNullOrWhiteSpace($text) -or $text.Length -lt 20000) {
    throw "源码读取异常（只有 $($text.Length) 字符），已中止，避免把 Program.cs 写空"
}
[System.IO.File]::WriteAllText($src, $text, (New-Object System.Text.UTF8Encoding($true)))

& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    "/out:$exe" "/win32icon:$icon" "/win32manifest:$manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    "/resource:$iconPng,GitHubDownloader.icon.png" `
    $src

if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $exe)) { throw 'exe not produced' }

# 下载脚本跟着 exe 走，保证便携。
$scripts = Join-Path $root 'scripts'
$distScripts = Join-Path $outDir 'scripts'
if (Test-Path -LiteralPath $scripts) {
    if (-not (Test-Path -LiteralPath $distScripts)) { New-Item -ItemType Directory -Path $distScripts | Out-Null }
    Copy-Item (Join-Path $scripts '*') $distScripts -Force
}

if ($Install) {
    $desktop = Join-Path $env:USERPROFILE 'Desktop'
    Copy-Item -LiteralPath $exe -Destination (Join-Path $desktop 'GitHubDownloader.exe') -Force
    Write-Host "installed to $desktop"
}

# 为什么还要往 %LOCALAPPDATA%\Programs 装一份：
# 有些环境（受限沙箱 / 安全策略）只限制「从特定目录启动的 exe」的写权限。
# 从这种目录直接跑，下载目标目录会报「访问被拒绝」。装到用户程序目录即可绕开。
# 已经存在就自动跟着更新，不用每次手动加 -Deploy。
$deployBase = [Environment]::GetFolderPath('LocalApplicationData')
if (-not [string]::IsNullOrEmpty($deployBase)) {
    $deployDir = [System.IO.Path]::Combine($deployBase, 'Programs', 'GitHubDownloader')
    $deployExe = [System.IO.Path]::Combine($deployDir, 'GitHubDownloader.exe')
    $shouldDeploy = $Deploy -or (Test-Path -LiteralPath $deployExe)
    if ($shouldDeploy) {
        if (-not (Test-Path -LiteralPath $deployDir)) {
            New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $exe -Destination $deployExe -Force
        if (Test-Path -LiteralPath $scripts) {
            $deployScripts = [System.IO.Path]::Combine($deployDir, 'scripts')
            if (-not (Test-Path -LiteralPath $deployScripts)) {
                New-Item -ItemType Directory -Path $deployScripts -Force | Out-Null
            }
            Copy-Item (Join-Path $scripts '*') $deployScripts -Force
        }
        Write-Host "deployed to $deployDir"
    }
}

Get-Item -LiteralPath $exe | Select-Object FullName, Length, LastWriteTime | Format-List