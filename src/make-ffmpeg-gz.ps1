# Compresses ..\ffmpeg.exe into ffmpeg.exe.gz for embedding into the standalone build.
$src = Join-Path $PSScriptRoot "..\ffmpeg.exe"
$dst = Join-Path $PSScriptRoot "ffmpeg.exe.gz"
if (-not (Test-Path $src)) {
    Write-Host "ERROR: ffmpeg.exe not found next to the project folder"
    exit 1
}
$in = [IO.File]::OpenRead($src)
$out = [IO.File]::Create($dst)
$gz = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
$in.CopyTo($gz)
$gz.Dispose(); $out.Dispose(); $in.Dispose()
Write-Host ("OK: ffmpeg.exe.gz " + [Math]::Round((Get-Item $dst).Length / 1MB, 1) + " MB")
