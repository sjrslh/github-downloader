param([switch]$Install)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src\Program.cs'
$icon = Join-Path $root 'assets\icon.ico'
$manifest = Join-Path $root 'src\app.manifest'
$outDir = Join-Path $root 'dist'
$exe = Join-Path $outDir 'GitHubDownloader.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $icon)) { throw "missing icon: $icon" }
if (-not (Test-Path $manifest)) { throw "missing manifest: $manifest" }
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# Source must be UTF-8 with BOM, otherwise csc reads the Chinese literals as ANSI.
$text = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText($src, $text, (New-Object System.Text.UTF8Encoding($true)))

& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    "/out:$exe" "/win32icon:$icon" "/win32manifest:$manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    $src

if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw 'exe not produced' }

# Ship the downloader scripts next to the exe so the app is portable.
$scripts = Join-Path $root 'scripts'
$distScripts = Join-Path $outDir 'scripts'
if (Test-Path $scripts) {
    if (-not (Test-Path $distScripts)) { New-Item -ItemType Directory -Path $distScripts | Out-Null }
    Copy-Item (Join-Path $scripts '*') $distScripts -Force
}

if ($Install) {
    $desktop = Join-Path $env:USERPROFILE 'Desktop'
    Copy-Item $exe (Join-Path $desktop 'GitHubDownloader.exe') -Force
    Write-Host "installed to $desktop"
}

Get-Item $exe | Select-Object FullName, Length, LastWriteTime | Format-List
