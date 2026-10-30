$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-ProjectFile([string]$path) { [IO.File]::ReadAllText((Join-Path $root $path)) }
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
[xml]$props = Read-ProjectFile 'Version.props'
$version = [string]$props.Project.PropertyGroup.VersionPrefix
$lock = Read-ProjectFile 'dependencies.lock.json' | ConvertFrom-Json
$sdk = Read-ProjectFile 'global.json' | ConvertFrom-Json
Require ($version -match '^\d+\.\d+\.\d+$') 'VersionPrefix must be MAJOR.MINOR.PATCH.'
Require ($lock.schema -eq 1 -and $lock.applicationVersion -eq $version) 'Application and dependency lock versions differ.'
Require ([string]$props.Project.PropertyGroup.Product -eq $lock.applicationName -and [string]$props.Project.PropertyGroup.Title -eq $lock.applicationName) 'Product names differ from the dependency lock.'
Require ([string]$props.Project.PropertyGroup.AssemblyTitle -eq $lock.applicationName) 'Windows file description differs from the dependency lock.'
Require ((Read-ProjectFile 'README.md').StartsWith('# ' + $lock.applicationName)) 'README product name differs from the dependency lock.'
Require ((Read-ProjectFile 'src/GpuUnlocker.Core/BuildInfo.cs').Contains('public const string ProductName = "' + $lock.applicationName + '";')) 'GUI product name differs from the dependency lock.'
Require ($sdk.sdk.version -eq $lock.dotnetSdk -and $sdk.sdk.rollForward -eq 'disable') 'SDK must match the exact lock version.'
Require ([string]$props.Project.PropertyGroup.RuntimeFrameworkVersion -eq $lock.dotnetRuntime) 'Runtime framework version differs from the lock.'
Require ((Read-ProjectFile 'README.md') -match ('(?m)^\*\*[^\r\n*]+v' + [regex]::Escape($version) + '\*\*')) 'README current version differs from Version.props.'
[xml]$manifest = Read-ProjectFile 'src/GpuUnlocker.App/app.manifest'
Require ($manifest.assembly.assemblyIdentity.version -eq "$version.0") 'Windows manifest version differs from Version.props.'
Require (-not (Read-ProjectFile 'src/GpuUnlocker.App/GpuUnlocker.App.csproj').Contains('<Version>')) 'Do not override the shared version in the application project.'
foreach ($asset in $lock.downloads) {
    $source = Read-ProjectFile $asset.sourceFile
    Require ($asset.sha256 -match '^[a-fA-F0-9]{64}$') "Invalid SHA-256 for $($asset.project)."
    Require ($source.Contains($asset.url)) "Download URL drift: $($asset.project)."
    Require ($source.ToLowerInvariant().Contains($asset.sha256.ToLowerInvariant())) "Download hash drift: $($asset.project)."
}
$gemm = Read-ProjectFile 'src/GpuUnlocker.Core/GemmRuntime.cs'
foreach ($dll in $lock.cublasDlls.PSObject.Properties) {
    Require ($gemm.Contains($dll.Name) -and $gemm.Contains([string]$dll.Value)) "cuBLAS DLL lock drift: $($dll.Name)."
}
Require ((Read-ProjectFile 'src/GpuUnlocker.Core/GemmBenchmark.cs').Contains('"' + $lock.benchmarkProtocol + '"')) 'GEMM protocol differs from lock.'
$kernels = Read-ProjectFile $lock.referenceOnly.historicalKernelHashes | ConvertFrom-Json
Require ($kernels.compiler -eq $lock.referenceOnly.historicalMmaAssembler) 'Historical assembler version differs from lock.'
Require ((Read-ProjectFile 'README.md').Contains($lock.referenceOnly.pytorch.url)) 'PyTorch reference version differs from lock.'
Require ((Read-ProjectFile 'src/GpuUnlocker.Core/Scripts/esp.ps1').Contains('$manifest.schema -ne ' + $lock.deploymentSchema)) 'Deployment schema differs from lock.'
Write-Host "PASS version locks: $($lock.applicationName) $version; SDK $($lock.dotnetSdk); runtime $($lock.dotnetRuntime); $($lock.downloads.Count) download assets."
