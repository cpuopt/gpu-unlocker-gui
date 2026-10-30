param([string]$ReleaseDirectory,[ValidateSet('ad102','ga102','tu10x')][string]$Family,[string]$OutputDirectory,[string]$InputDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
try {
    $arguments = @{Family=$Family;Out=$OutputDirectory;Work=$InputDirectory}
    if ($Family -eq 'tu10x') { $arguments.ProvenBlob = Join-Path $InputDirectory 'gsp_rm_boot_dbg.bin' }
    & (Join-Path $ReleaseDirectory 'make-usbroot.ps1') @arguments
    if (-not $?) { throw '上游生成脚本失败。' }
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
