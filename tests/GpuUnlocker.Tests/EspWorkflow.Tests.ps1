param([string]$Scripts)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
. (Join-Path $Scripts 'esp.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('gpu-unlocker-esp-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script:passes=0
function Assert($Condition,[string]$Message){if(-not $Condition){throw $Message};$script:passes++}
function Expect-Failure([scriptblock]$Action,[string]$Message){$failed=$false;try{& $Action | Out-Null}catch{$failed=$true};Assert $failed $Message}

# Mixed-generation NVIDIA adapters must not make a Turing verdict use another card.
[xml]$mixed='<nvidia_smi_log><gpu><pci><pci_device_id>1F0710DE</pci_device_id></pci><gsp_firmware_version>610.43</gsp_firmware_version></gpu><gpu><pci><pci_device_id>280510DE</pci_device_id></pci><gsp_firmware_version>N/A</gsp_firmware_version></gpu></nvidia_smi_log>'
Assert-TuringRuntime $mixed 1
Assert $true 'Mixed Turing runtime verdict'
$mixed.nvidia_smi_log.gpu[0].gsp_firmware_version='N/A'
Expect-Failure {Assert-TuringRuntime $mixed 1} 'Inactive Turing GSP accepted'

# Every external storage/firmware primitive is replaced; real ESP/BCD never touched.
function Test-IsAdmin { return $script:admin }
function Get-StorageInfo { return @{gpt=$script:gpt;espFound=$script:unique;partition=@{Guid='fixture-esp';DiskNumber=0;PartitionNumber=1}} }
function Open-Esp($Storage){if(-not $Storage.gpt -or -not $Storage.espFound){throw 'not unique/GPT'};return @{root=$script:esp;added=$false;partition=$Storage.partition}}
function Close-Esp($Context){$script:closed++}
function Get-FreeSpace($Context){return $script:free}
function Get-SecureBoot { return $script:secure }
function Copy-Item {
    param([string]$LiteralPath,[string]$Destination,[switch]$Force)
    if($script:failCopy -and $Destination.StartsWith($script:esp) -and $LiteralPath.EndsWith('gsp_boot_ad102.bin')){throw 'Injected copy failure'}
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
function Test-TuringPrerequisite {if(-not $script:gsp){throw 'GSP prerequisite failure'}}
function New-Backup([string]$Purpose){$backup=Join-Path $script:case ('backups\' + [guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $backup -Force | Out-Null;return $backup}
function Get-BootEntries { return @($script:entries) }
function Get-FirmwareOrder {if($script:failOrderRead){throw 'Injected order read failure'};return @($script:order)}
function Get-FirmwareBootSequence {return @($script:sequence)}
function Invoke-Bcd([string[]]$Arguments){
    $script:calls+=,($Arguments -join '|')
    switch($Arguments[0]){
        '/copy' {$script:entries+=@{id=$script:newId;path=('\'+$WindowsRelative);device='partition=fixture'};return "copied $script:newId"}
        '/set' {
            if($Arguments[2] -eq 'path'){
                if($script:failBcd){throw 'injected BCD failure'}
                @($script:entries | Where-Object {$_.id -eq $Arguments[1]})[0].path=$Arguments[3]
            }
            if($Arguments[2] -eq 'displayorder'){
                if($script:failOrderWrites -gt 0){$script:failOrderWrites--;throw 'Injected firmware write failure'}
                if($script:ignoreOrderWrites -gt 0){$script:ignoreOrderWrites--;return 'ignored'}
                $script:order=@($Arguments | Select-Object -Skip 3)
                if($script:sequenceOnWrite){$script:sequence=@($script:sequenceOnWrite);$script:sequenceOnWrite=$null}
            }
            if($Arguments[2] -eq 'bootsequence'){
                if($script:failSequenceWrites -gt 0){$script:failSequenceWrites--;throw 'Injected BootNext write failure'}
                $script:sequence=@($Arguments | Select-Object -Skip 3)
            }
            return 'set'
        }
        '/deletevalue' {if($Arguments[2] -ne 'bootsequence'){throw 'Unexpected deletevalue'};if($script:failSequenceWrites -gt 0){$script:failSequenceWrites--;throw 'Injected BootNext clear failure'};$script:sequence=@();return 'cleared'}
        '/delete' {
            if($script:failDelete){throw 'Injected firmware deletion failure'}
            Assert ($Arguments[1] -ne $WindowsBootId) 'Deleted Windows boot entry'
            $script:entries=@($script:entries | Where-Object {$_.id -ne $Arguments[1]})
            $script:order=@($script:order | Where-Object {$_ -ne $Arguments[1]})
            return 'deleted'
        }
        default {throw 'Unexpected BCD command'}
    }
}
function New-Fixture([string]$Name,[string]$Family='ad102'){
    $script:case=Join-Path $root $Name;$script:esp=Join-Path $script:case 'esp';$script:package=Join-Path $script:case 'package'
    New-Item -ItemType Directory -Path (Join-Path $script:esp 'EFI\Microsoft\Boot'),(Join-Path $script:package 'EFI\GPUUnlock') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $script:esp $WindowsRelative) -Value 'WINDOWS BOOT FILE'
    Set-Content -LiteralPath (Join-Path $script:esp 'unknown.txt') -Value 'PRESERVE'
    $script:newId='{11111111-2222-3333-4444-555555555555}'
    $script:entries=@(@{id=$WindowsBootId;path=('\'+$WindowsRelative);device='partition=fixture'})
    $script:order=@($WindowsBootId,'{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}')
    $script:originalOrder=@($script:order)
    $script:calls=@();$script:admin=$true;$script:gpt=$true;$script:unique=$true;$script:free=500MB;$script:secure=$false;$script:gsp=$true;$script:failBcd=$false;$script:failCopy=$false;$script:closed=0
    $script:sequence=@();$script:failOrderWrites=0;$script:ignoreOrderWrites=0;$script:failDelete=$false
    $script:failSequenceWrites=0;$script:failOrderRead=$false;$script:sequenceOnWrite=$null
    $files=foreach($path in (Get-AllowedPaths $Family)){
        $full=Join-Path $script:package $path
        if($path -eq 'chain-target.txt'){Set-Content -LiteralPath $full -Value ('\'+$WindowsRelative) -Encoding ASCII}
        else {Set-Content -LiteralPath $full -Value ('fixture '+$path) -Encoding ASCII}
        @{path=$path;bytes=(Get-Item -LiteralPath $full).Length;sha256=(Get-Hash $full)}
    }
    $manifest=@{schema=2;mode='gpu-unlocker';family=$Family;release='v2.1.5';files=@($files)}
    Write-Json (Join-Path $script:package 'package.json') $manifest
    return $manifest
}
function Assert-Preserved{
    Assert ((Get-Content -LiteralPath (Join-Path $script:esp $WindowsRelative)).Trim() -eq 'WINDOWS BOOT FILE') 'Windows EFI changed'
    Assert (Test-Path -LiteralPath (Join-Path $script:esp 'unknown.txt')) 'Unknown file removed'
    Assert (@($script:entries | Where-Object {$_.id -eq $WindowsBootId}).Count -eq 1) 'Windows boot option removed'
}

foreach($family in @('ad102','ga102','tu10x')){
    $manifest=New-Fixture ('install-'+$family) $family
    $result=Install-Unlock $script:package
    Assert ($result.filesValid -and $result.controlsValid -and $result.bootEntryValid) 'Installation verification failed'
    Assert (($script:order -join '|') -eq (($script:originalOrder+@($script:newId)) -join '|')) 'Changed original boot priority'
    Expect-Failure {Install-Unlock $script:package} 'Duplicate installation accepted'
    Assert-Preserved
    # Owned fixture EFI is authenticated by its valid installed manifest.
    Set-Content -LiteralPath (Join-Path $script:esp 'loader.log') -Value 'runtime evidence'
    $removed=Remove-UnlockInstallation
    Assert $removed.removed 'Uninstall failure'
    Assert (@(Get-ExistingPaths $script:esp).Count -eq 0) 'Owned residue left'
    Assert (Test-Path -LiteralPath (Join-Path $script:esp 'loader.log')) 'Runtime log removed'
    Assert-Preserved
    Assert ($script:closed -gt 0) 'ESP context not released'
}

$manifest=New-Fixture 'partial'
Copy-Item -LiteralPath (Join-Path $script:package 'fwimage_ad102.bin') -Destination (Join-Path $script:esp 'fwimage_ad102.bin')
$report=Get-EnvironmentReport
Assert $report.hasUnlockFiles 'Partial install not detected'
Expect-Failure {Install-Unlock $script:package} 'Partial install did not block write'
Assert (Remove-UnlockInstallation).removed 'Partial cleanup failed'
Assert-Preserved

$manifest=New-Fixture 'legacy'
Install-Unlock $script:package | Out-Null
$state=Read-State $script:esp
$state.schema=1;$state.mode='system-esp-ad102'
Write-Json (Join-Path $script:esp $StateRelative) $state
Assert ((Get-EnvironmentReport).installationKind -eq '旧版脚本安装') 'Legacy detection failed'
Assert (Remove-UnlockInstallation).removed 'Legacy removal failed'

$manifest=New-Fixture 'bcd-failure'
$script:failBcd=$true
Expect-Failure {Install-Unlock $script:package} 'BCD failure ignored'
Assert (@(Get-ExistingPaths $script:esp).Count -eq 0) 'Rollback left files'
Assert (($script:order -join '|') -eq ($script:originalOrder -join '|')) 'Rollback changed boot order'
Assert-Preserved

$manifest=New-Fixture 'space'
$script:free=10MB
Expect-Failure {Install-Unlock $script:package} '10 MiB does not include package requirement'
Assert (@(Get-ExistingPaths $script:esp).Count -eq 0) 'Low-space wrote ESP files'
$script:free=10MB + [long](($manifest.files | ForEach-Object {[long]$_.bytes} | Measure-Object -Sum).Sum) + [Text.Encoding]::UTF8.GetByteCount((@{schema=2;mode='gpu-unlocker';family='ad102';release='v2.1.5';files=@($manifest.files);partitionGuid='fixture-esp';phase='copying';bcdId=$null;installed='2000-01-01T00:00:00.0000000+00:00'}|ConvertTo-Json -Depth 12)) + 4096 + 128
Assert (Install-Unlock $script:package).filesValid 'Sufficient 10 MiB reserve rejected'

$manifest=New-Fixture 'copy-failure'
$script:failCopy=$true
Expect-Failure {Install-Unlock $script:package} 'Copy failure ignored'
Assert (@(Get-ExistingPaths $script:esp).Count -eq 0) 'Copy failure left owned files'
Assert-Preserved

$manifest=New-Fixture 'secure'
$script:secure=$true
Expect-Failure {Install-Unlock $script:package} 'Secure Boot did not block write'
$script:secure=$null
Expect-Failure {Install-Unlock $script:package} 'Unknown Secure Boot did not block write'

$manifest=New-Fixture 'turing'
$manifest=New-Fixture 'turing-fail' 'tu10x'
$script:gsp=$false
Expect-Failure {Install-Unlock $script:package} 'Turing prerequisite bypass'

$manifest=New-Fixture 'manifest-escape'
$manifest.files[0].path='..\unknown.txt'
Write-Json (Join-Path $script:package 'package.json') $manifest
Expect-Failure {Install-Unlock $script:package} 'Manifest traversal accepted'
Assert-Preserved

$manifest=New-Fixture 'tampered'
Add-Content -LiteralPath (Join-Path $script:package 'fwimage_ad102.bin') -Value 'tamper'
Expect-Failure {Install-Unlock $script:package} 'Tampered package accepted'

$manifest=New-Fixture 'bad-chain'
Install-Unlock $script:package | Out-Null
Set-Content -LiteralPath (Join-Path $script:esp 'chain-target.txt') -Value '\wrong.efi'
Assert (-not (Test-UnlockInstallation).controlsValid) 'Incorrect chain target accepted'

$manifest=New-Fixture 'unknown-efi'
New-Item -ItemType Directory -Path (Join-Path $script:esp 'EFI\GPUUnlock') | Out-Null
Set-Content -LiteralPath (Join-Path $script:esp $EfiRelative) -Value 'unknown app'
Expect-Failure {Remove-UnlockInstallation} 'Unknown EFI removed'
Assert (Test-Path -LiteralPath (Join-Path $script:esp $EfiRelative)) 'Unknown EFI was not preserved'

$manifest=New-Fixture 'other-disk'
$otherId='{bbbbbbbb-1111-2222-3333-444444444444}'
$script:entries+=@{id=$otherId;path=('\'+$EfiRelative);device='partition=OTHERDISK'}
Assert (-not (Get-EnvironmentReport).hasUnlockFiles) 'Other disk startup entry treated as local installation'
Install-Unlock $script:package | Out-Null
Remove-UnlockInstallation | Out-Null
Assert (@($script:entries | Where-Object {$_.id -eq $otherId}).Count -eq 1) 'Removed another disk firmware entry'

$manifest=New-Fixture 'entry-only'
$script:entries+=@{id=$script:newId;path=('\'+$EfiRelative);device='partition=fixture'}
Assert (Get-EnvironmentReport).hasUnlockFiles 'Entry-only residue not detected'
Remove-UnlockInstallation | Out-Null
Assert (@($script:entries | Where-Object {$_.id -eq $script:newId}).Count -eq 0) 'Entry-only residue not removed'

$manifest=New-Fixture 'interrupted-entry'
Install-Unlock $script:package | Out-Null
$state=Read-State $script:esp;$state.phase='copying'
Write-Json (Join-Path $script:esp $StateRelative) $state
$script:entries[1].path='\'+$WindowsRelative;$script:entries[1].description='GPU Unlock'
Assert (-not (Test-UnlockInstallation).filesValid) 'Interrupted transaction reported complete'
Remove-UnlockInstallation | Out-Null
Assert (@($script:entries | Where-Object {$_.id -eq $script:newId}).Count -eq 0) 'Recorded interrupted copy entry not removed'
Assert-Preserved

# BootOrder and BootNext parsing must remain independent, including localized output.
$text="identifier $FirmwareId`r`ndisplayorder $WindowsBootId`r`n             {aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}`r`nbootsequence $script:newId`r`ntimeout 1"
$parsed=ConvertFrom-FirmwareText $text
Assert ($parsed.order.Count -eq 2 -and $parsed.order[0] -eq $WindowsBootId -and $script:newId -notin $parsed.order) 'BootNext leaked into BootOrder'
Assert ($parsed.sequence.Count -eq 1 -and $parsed.sequence[0] -eq $script:newId) 'BootNext parsing failed'
$localized=$text.Replace('identifier','标识符').Replace('displayorder','显示顺序').Replace('bootsequence','启动序列')
Assert ((ConvertFrom-FirmwareText $localized).order.Count -eq 2) 'Localized firmware order parsing failed'
Expect-Failure {ConvertFrom-FirmwareText "identifier $FirmwareId`ndisplayorder invalid"} 'Malformed order accepted'

$manifest=New-Fixture 'promote'
Install-Unlock $script:package | Out-Null
$before=Test-UnlockInstallation
Assert ($before.filesValid -and $before.bootEntryValid -and $before.bootOrderKnown -and -not $before.unlockFirst) 'Append-only installation misreported as first'
$script:sequence=@($WindowsBootId)
$promoted=Set-UnlockFirst
Assert ($promoted.unlockFirst -and $promoted.windowsInBootOrder) 'Priority update not verified'
Assert (($script:order -join '|') -eq ((@($script:newId)+$script:originalOrder) -join '|')) 'Other boot entries reordered by priority update'
Assert ($script:sequence.Count -eq 1 -and $script:sequence[0] -eq $WindowsBootId) 'Priority update changed BootNext'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $script:case 'backups') -Recurse -Filter 'boot-order.json').Count -eq 1) 'Priority snapshot missing'
Assert-Preserved

# External edits after promotion must be detected, and removal preserves the live relative order.
$other='{cccccccc-1111-2222-3333-444444444444}'
$script:entries+=@{id=$other;path='\EFI\other\boot.efi';device='partition=fixture'}
$script:order=@($other,$script:newId,$WindowsBootId,$script:originalOrder[1])
$changed=Test-UnlockInstallation
Assert ($changed.filesValid -and $changed.bootEntryValid -and -not $changed.unlockFirst) 'Changed priority reported as damaged installation or still first'
Assert (Get-EnvironmentReport).hasUnlockFiles 'Changed priority hid installed unlock files'
$script:sequence=@($script:newId,$other)
Remove-UnlockInstallation | Out-Null
Assert (($script:order -join '|') -eq ((@($other,$WindowsBootId,$script:originalOrder[1])) -join '|')) 'Removal restored a stale order over user changes'
Assert ($script:sequence.Count -eq 1 -and $script:sequence[0] -eq $other) 'Removal damaged unrelated BootNext'
Assert (@($script:entries | Where-Object {$_.id -eq $other}).Count -eq 1) 'Removal deleted unrelated firmware entry'
Assert-Preserved

$manifest=New-Fixture 'promoted-removal'
Install-Unlock $script:package | Out-Null
Set-UnlockFirst | Out-Null
$script:sequence=@($script:newId)
Remove-UnlockInstallation | Out-Null
Assert (($script:order -join '|') -eq ($script:originalOrder -join '|')) 'Removing first entry failed to restore remaining order'
Assert ($script:sequence.Count -eq 0) 'Dangling unlock BootNext left behind'
Assert-Preserved

$manifest=New-Fixture 'not-listed'
Install-Unlock $script:package | Out-Null
$script:order=@($script:originalOrder)
Assert (-not (Test-UnlockInstallation).unlockInBootOrder) 'Unlisted entry reported present in BootOrder'
Assert (Set-UnlockFirst).unlockFirst 'Unlisted owned entry could not be promoted'
$script:order=@($script:newId,$script:originalOrder[1])
Assert (-not (Test-UnlockInstallation).windowsInBootOrder) 'Missing Windows fallback not detected'
Assert (Set-UnlockFirst).windowsInBootOrder 'Missing Windows fallback was not added'

foreach($failure in @('ignored','write','delete')){
    $manifest=New-Fixture ('priority-failure-'+$failure)
    Install-Unlock $script:package | Out-Null
    if($failure -eq 'delete'){
        Set-UnlockFirst | Out-Null
        $script:failDelete=$true
        Expect-Failure {Remove-UnlockInstallation} 'Firmware deletion failure ignored'
    } else {
        if($failure -eq 'ignored'){$script:ignoreOrderWrites=1}else{$script:failOrderWrites=1}
        Expect-Failure {Set-UnlockFirst} 'Rejected priority write reported successful'
        Assert (($script:order -join '|') -eq (($script:originalOrder+@($script:newId)) -join '|')) 'Priority failure did not restore original order'
    }
    Assert (Test-Path -LiteralPath (Join-Path $script:esp $EfiRelative)) 'Firmware failure removed EFI files'
    Assert-Preserved
}

$manifest=New-Fixture 'priority-refused'
Install-Unlock $script:package | Out-Null
$script:admin=$false
Expect-Failure {Set-UnlockFirst} 'Non-admin priority write accepted'
$script:admin=$true;$script:secure=$true
Expect-Failure {Set-UnlockFirst} 'Secure Boot priority write accepted'
$script:secure=$false
Add-Content -LiteralPath (Join-Path $script:esp $EfiRelative) -Value 'tampered'
Expect-Failure {Set-UnlockFirst} 'Tampered installation allowed as primary boot target'
Assert-Preserved

$manifest=New-Fixture 'orphan-firmware-id'
Install-Unlock $script:package | Out-Null
$script:entries=@($script:entries | Where-Object {$_.id -ne $script:newId})
$script:sequence=@($script:newId)
Remove-UnlockInstallation | Out-Null
Assert ($script:newId -notin $script:order -and $script:sequence.Count -eq 0) 'Recorded dangling firmware references were not removed'
Assert-Preserved

$manifest=New-Fixture 'reused-recorded-id'
Install-Unlock $script:package | Out-Null
@($script:entries | Where-Object {$_.id -eq $script:newId})[0].path='\EFI\unrelated\boot.efi'
Remove-UnlockInstallation | Out-Null
Assert ($script:newId -in $script:order) 'Reused foreign ID removed from live order'
Assert (@($script:entries | Where-Object {$_.id -eq $script:newId}).Count -eq 1) 'Reused foreign firmware entry deleted'
Assert-Preserved

foreach($fault in @('order','sequence','read')){
    $manifest=New-Fixture ('safe-remove-'+$fault)
    Install-Unlock $script:package | Out-Null
    Set-UnlockFirst | Out-Null
    $script:sequence=@($script:newId)
    if($fault -eq 'order'){$script:ignoreOrderWrites=1}
    if($fault -eq 'sequence'){$script:failSequenceWrites=1}
    if($fault -eq 'read'){$script:failOrderRead=$true}
    Expect-Failure {Remove-UnlockInstallation} 'Removal ignored uncertain firmware configuration'
    Assert (Test-Path -LiteralPath (Join-Path $script:esp $EfiRelative)) 'Unsafe removal deleted EFI before firmware cleanup'
    $script:failOrderRead=$false
    Remove-UnlockInstallation | Out-Null
    Assert ($script:newId -notin $script:order -and $script:sequence.Count -eq 0) 'Retry failed to clean firmware references'
    Assert-Preserved
}

$manifest=New-Fixture 'concurrent-boot-next'
Install-Unlock $script:package | Out-Null
$script:sequenceOnWrite=$script:originalOrder[1]
Expect-Failure {Set-UnlockFirst} 'External BootNext modification went unnoticed'
Assert ($script:sequence.Count -eq 1 -and $script:sequence[0] -eq $script:originalOrder[1]) 'Rollback overwrote another program BootNext'
Assert-Preserved

$manifest=New-Fixture 'missing-windows-efi'
Install-Unlock $script:package | Out-Null
Remove-Item -LiteralPath (Join-Path $script:esp $WindowsRelative)
Assert (-not (Test-UnlockInstallation).bootEntryValid) 'Missing Windows EFI accepted as a valid fallback'
Expect-Failure {Set-UnlockFirst} 'Priority change accepted without Windows fallback file'
Expect-Failure {Remove-UnlockInstallation} 'Removal proceeded without Windows fallback file'
Assert (Test-Path -LiteralPath (Join-Path $script:esp $EfiRelative)) 'Missing fallback check deleted unlock EFI'

Write-Host "PASS ESP: $script:passes checks; fixtures $root"
