param([ValidateSet('Probe','Gpus','Turing','Install','SetFirst','Verify','Remove')][string]$Operation, [string]$PackagePath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
. (Join-Path $PSScriptRoot 'esp.ps1')
try {
    $data = switch ($Operation) {
        'Probe' { Get-EnvironmentReport }
        'Gpus' { ,@(Get-CimInstance Win32_VideoController | ForEach-Object {
            $pci=$null
            if($_.PNPDeviceID -match 'VEN_10DE'){
                try {
                    $bus=(Get-PnpDeviceProperty -InstanceId $_.PNPDeviceID -KeyName 'DEVPKEY_Device_BusNumber' -ErrorAction Stop).Data
                    $address=(Get-PnpDeviceProperty -InstanceId $_.PNPDeviceID -KeyName 'DEVPKEY_Device_Address' -ErrorAction Stop).Data
                    if($null -ne $bus -and $null -ne $address){$pci='0000:{0:x2}:{1:x2}.{2:x}' -f [int]$bus,([int]$address -shr 16),([int]$address -band 0xffff)}
                }catch{}
            }
            @{name=$_.Name;deviceId=$_.PNPDeviceID;family=$null;pciBusId=$pci}
        }) }
        'Turing' { Test-TuringPrerequisite; $true }
        'Install' { Install-Unlock $PackagePath }
        'SetFirst' { Set-UnlockFirst }
        'Verify' { Test-UnlockInstallation }
        'Remove' { Remove-UnlockInstallation }
    }
    $json = @{success=$true;data=$data;error=$null} | ConvertTo-Json -Depth 12 -Compress
    [Console]::WriteLine('GPUUNLOCKER_RESULT:' + $json)
} catch {
    $json = @{success=$false;data=$null;error=$_.Exception.Message} | ConvertTo-Json -Depth 4 -Compress
    [Console]::WriteLine('GPUUNLOCKER_RESULT:' + $json)
    exit 1
}
