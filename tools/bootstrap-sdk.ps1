param([string]$InstallDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\dotnet'))
$ErrorActionPreference='Stop'
$script=Join-Path ([IO.Path]::GetTempPath()) ('gpu-unlocker-dotnet-' + [guid]::NewGuid().ToString('N') + '.ps1')
try {
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script
    & $script -Version 10.0.403 -InstallDir $InstallDirectory -NoPath
    if(-not $?){throw 'SDK installation failed.'}
    Write-Host (Join-Path $InstallDirectory 'dotnet.exe')
} finally {if(Test-Path -LiteralPath $script){Remove-Item -LiteralPath $script}}
