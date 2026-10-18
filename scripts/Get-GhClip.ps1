try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# 从剪贴板里取第一条像 GitHub 链接或 owner/repo 的内容
$c = Get-Clipboard -Raw
if (-not $c) { return }
$line = ($c -split "\r?\n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
$line = $line.TrimEnd('。', '，', ',', ';', '；')
if ($line -match '^https?://' -or $line -match '^[\w.\-]+/[\w.\-]+$') { $line }