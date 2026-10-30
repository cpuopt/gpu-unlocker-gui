param([string]$Dotnet = 'dotnet', [string]$VersionSuffix = '', [string]$OutputDirectory = '')
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$out=Join-Path $project 'artifacts\GPUUnlocker-win-x64'
$stage=Join-Path $project ('artifacts\single-file-stage-'+[guid]::NewGuid().ToString('N'))
Push-Location $project
try {
    $publishArgs=@('publish','src\GpuUnlocker.App\GpuUnlocker.App.csproj','-c','Release','-p:PublishProfile=SingleFile','-o',$stage)
    if($VersionSuffix){$publishArgs += "-p:VersionSuffix=$VersionSuffix"}
    & $Dotnet @publishArgs
    if($LASTEXITCODE -ne 0){throw 'Publish failed.'}
    $files=@(Get-ChildItem -LiteralPath $stage -Recurse -File)
    if($files.Count -ne 1 -or $files[0].Name -ne 'GpuUnlocker.exe'){throw 'Expected exactly one self-contained EXE.'}
    $version=$files[0].VersionInfo.ProductVersion
    if($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$'){throw 'Unexpected EXE product version.'}
    $fileName="GPU-Unlocker-GUI-v$version.exe"
    Rename-Item -LiteralPath $files[0].FullName -NewName $fileName
    if($OutputDirectory){$out=Join-Path $project $OutputDirectory}
    $out=[IO.Path]::GetFullPath($out)
    $artifactRoot=[IO.Path]::GetFullPath((Join-Path $project 'artifacts')).TrimEnd('\')+'\'
    if(-not $out.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase) -or $out -eq [IO.Path]::GetFullPath($stage)){throw 'Output directory must be below project artifacts and differ from staging.'}
    if(Test-Path -LiteralPath $out){Remove-Item -LiteralPath $out -Recurse -Force}
    Move-Item -LiteralPath $stage -Destination $out
    $exe=Join-Path $out $fileName
    if($env:GITHUB_OUTPUT){
        "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
        "exe_name=$fileName" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
    Get-FileHash -LiteralPath $exe -Algorithm SHA256
    Write-Host "Published single self-contained EXE: $exe"
} finally {Pop-Location}
