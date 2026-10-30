# Application-owned storage backend. Never execute arbitrary paths from manifests.
$EfiRelative = 'EFI\GPUUnlock\BOOTX64.EFI'
$StateRelative = 'EFI\GPUUnlock\install-state.json'
$WindowsRelative = 'EFI\Microsoft\Boot\bootmgfw.efi'
$WindowsBootId = '{9dea862c-5cdd-4e70-acc1-f32b344d4795}'
$FirmwareId = '{a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba}'

function Get-FamilyAssets([string]$Family) {
    if ($Family -notin @('ad102','ga102','tu10x')) { throw '未知 GPU 家族。' }
    $scrub = if ($Family -eq 'tu10x') { 'sec2_bl_tu10x.bin' } else { 'scrubber_ad10x.bin' }
    return @("booter_load_$Family.bin","gsp_boot_desc_$Family.bin","gsp_boot_$Family.bin","fwimage_$Family.bin","fwsignature_$Family.bin",$scrub,"v67-$Family-four-plm.bin")
}
function Get-AllowedPaths([string]$Family) { return @($EfiRelative) + @(Get-FamilyAssets $Family) + @('i_know_what_i_am_doing.txt','chain-target.txt') }
function Get-AllPaths { return @(@('ad102','ga102','tu10x') | ForEach-Object { Get-AllowedPaths $_ }) | Select-Object -Unique }
function Get-Hash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function Write-Json([string]$Path,$Value) {
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temp -Encoding UTF8
        Move-Item -LiteralPath $temp -Destination $Path -Force
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
function Test-IsAdmin {
    return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Assert-Admin { if (-not (Test-IsAdmin)) { throw '需要管理员权限。' } }
function Get-StorageInfo {
    $os = Get-Partition -DriveLetter $env:SystemDrive.TrimEnd(':') -ErrorAction Stop
    $disk = Get-Disk -Number $os.DiskNumber -ErrorAction Stop
    $parts = @(Get-Partition -DiskNumber $disk.Number | Where-Object {
        $_.IsSystem -and ([string]$_.GptType).Trim('{}') -eq 'c12a7328-f81f-11d2-ba4b-00a0c93ec93b'
    })
    return @{gpt=($disk.PartitionStyle -eq 'GPT');espFound=($parts.Count -eq 1);partition= $(if($parts.Count -eq 1){$parts[0]}else{$null})}
}
function Open-Esp($Storage) {
    if (-not $Storage.gpt -or -not $Storage.espFound) { throw '当前系统磁盘必须为 GPT，且具有唯一、正在使用的 ESP。' }
    $p = $Storage.partition
    $v = $p | Get-Volume -ErrorAction Stop
    if ($v.FileSystem -ne 'FAT32') { throw '系统 ESP 不是 FAT32。' }
    if ($p.DriveLetter -and [int][char]$p.DriveLetter -gt 0) { return @{root="$($p.DriveLetter):\";added=$false;partition=$p} }
    $used = @(Get-PSDrive -PSProvider FileSystem | ForEach-Object Name)
    $used += @(Get-Volume | Where-Object DriveLetter | ForEach-Object { [string]$_.DriveLetter })
    $letter = 90..68 | ForEach-Object { [string][char]$_ } | Where-Object { $_ -notin $used } | Select-Object -First 1
    if (-not $letter) { throw '没有可用于临时挂载 ESP 的盘符。' }
    $root = "${letter}:\"
    Add-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath $root -ErrorAction Stop
    return @{root=$root;added=$true;partition=$p}
}
function Close-Esp($Context) {
    if ($Context -and $Context.added) {
        Remove-PartitionAccessPath -DiskNumber $Context.partition.DiskNumber -PartitionNumber $Context.partition.PartitionNumber -AccessPath $Context.root -ErrorAction Stop
    }
}
function Get-FreeSpace($Context) { return [long](($Context.partition | Get-Volume -ErrorAction Stop).SizeRemaining) }
function Get-SecureBoot { return [bool](Confirm-SecureBootUEFI -ErrorAction Stop) }
function Invoke-Bcd([string[]]$Arguments) {
    $text = & "$env:SystemRoot\System32\bcdedit.exe" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "bcdedit 失败 ($LASTEXITCODE)：$($text -join ' ')" }
    return ($text -join "`r`n")
}
function Get-BootEntries {
    $text = Invoke-Bcd @('/enum','firmware','/v')
    $entries = foreach ($block in ($text -split '(?:\r?\n){2,}')) {
        $id = [regex]::Match($block,'\{[0-9a-fA-F-]{36}\}').Value
        if (-not $id) { continue }
        $path = [regex]::Match($block,'(?im)^\s*(?:path|路径)\s+(.+)$').Groups[1].Value.Trim()
        $device = [regex]::Match($block,'(?im)^\s*(?:device|设备)\s+(.+)$').Groups[1].Value.Trim()
        $description = [regex]::Match($block,'(?im)^\s*(?:description|描述)\s+(.+)$').Groups[1].Value.Trim()
        @{id=$id;path=$path;device=$device;description=$description;text=$block}
    }
    return @($entries)
}
function ConvertFrom-FirmwareText([string]$Text) {
    $guid='\{[0-9a-fA-F-]{36}\}'
    if([regex]::Match($Text,$guid).Value -ne $FirmwareId){throw '无法确认固件启动管理器。'}
    function Read-List([string]$Names,[bool]$Required) {
        $match=[regex]::Match($Text,('(?im)^[ \t]*(?:'+$Names+')[ \t]+(?<ids>'+$guid+'(?:[ \t]*\r?\n[ \t]+'+$guid+')*)[ \t]*\r?$'))
        if(-not $match.Success){
            if($Required -or $Text -match ('(?im)^[ \t]*(?:'+$Names+')\b')){throw '无法解析固件启动顺序字段。'}
            return @()
        }
        $ids=@([regex]::Matches($match.Groups['ids'].Value,$guid) | ForEach-Object Value)
        if(@($ids | Select-Object -Unique).Count -ne $ids.Count -or $FirmwareId -in $ids){throw '固件启动顺序包含重复或无效 ID。'}
        return $ids
    }
    # Parse fields separately: BootNext/bootsequence GUIDs must not become part of BootOrder.
    return @{order=@(Read-List 'displayorder|显示顺序' $true);sequence=@(Read-List 'bootsequence|启动序列|启动顺序|引导序列' $false)}
}
function Get-FirmwareOrder { return @((ConvertFrom-FirmwareText (Invoke-Bcd @('/enum','{fwbootmgr}','/v'))).order) }
function Get-FirmwareBootSequence { return @((ConvertFrom-FirmwareText (Invoke-Bcd @('/enum','{fwbootmgr}','/v'))).sequence) }
function Set-FirmwareBootSequence([string[]]$Ids) {
    if($Ids.Count){Invoke-Bcd (@('/set','{fwbootmgr}','bootsequence') + $Ids) | Out-Null}
    else {Invoke-Bcd @('/deletevalue','{fwbootmgr}','bootsequence') | Out-Null}
    if((@(Get-FirmwareBootSequence) -join '|') -ne ($Ids -join '|')){throw '一次性启动序列写入验证失败。'}
}
function Get-UnlockEntries($State=$null) {
    $entries=@(Get-BootEntries)
    $windows=@($entries | Where-Object {$_.id -eq $WindowsBootId -and $_.path -ieq ('\'+$WindowsRelative)})
    if($windows.Count -ne 1 -or -not $windows[0].device){throw '无法确认 Windows 回退启动项对应的 ESP，停止处理启动项。'}
    $recorded=$null
    if($State){$recorded=Get-StateId $State}
    return @($entries | Where-Object {
        $_.id -ne $WindowsBootId -and $_.id -ne $FirmwareId -and $_.device -eq $windows[0].device -and
        ($_.path -ieq ('\' + $EfiRelative) -or ($recorded -and $_.id -eq $recorded -and $_.description -eq 'GPU Unlock' -and $_.path -ieq ('\'+$WindowsRelative)))
    })
}
function Get-ExistingPaths([string]$Root) {
    $found = @(Get-AllPaths | Where-Object { Test-Path -LiteralPath (Join-Path $Root $_) })
    if (Test-Path -LiteralPath (Join-Path $Root $StateRelative)) { $found += $StateRelative }
    # A dedicated application directory also blocks replacement if damaged/unknown.
    if ((Test-Path -LiteralPath (Join-Path $Root 'EFI\GPUUnlock')) -and $found.Count -eq 0) { $found += 'EFI\GPUUnlock' }
    return @($found)
}
function Read-State([string]$Root) {
    $file = Join-Path $Root $StateRelative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json) } catch { return $null }
}
function Assert-Manifest($Manifest) {
    if ($Manifest.schema -eq 1 -and $Manifest.mode -eq 'system-esp-ad102') { $family='ad102' }
    elseif ($Manifest.schema -eq 2 -and $Manifest.mode -eq 'gpu-unlocker') { $family=[string]$Manifest.family }
    else { throw '安装清单版本无效。' }
    $allowed = @(Get-AllowedPaths $family)
    $seen = @{}
    if (@($Manifest.files).Count -ne $allowed.Count) { throw '安装清单文件数量无效。' }
    foreach ($file in $Manifest.files) {
        $path = ([string]$file.path).Replace('/','\')
        if ($path -notin $allowed -or $seen.ContainsKey($path) -or [long]$file.bytes -le 0 -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw '安装清单包含非法文件路径、大小或哈希。' }
        $seen[$path]=$true
    }
    return $family
}
function Test-Files([string]$Root,$Manifest,[switch]$AllowMissing) {
    [void](Assert-Manifest $Manifest)
    foreach ($file in $Manifest.files) {
        $path = Join-Path $Root ([string]$file.path).Replace('/','\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { if($AllowMissing){continue}; throw "文件缺失：$($file.path)" }
        if ((Get-Item -LiteralPath $path).Length -ne [long]$file.bytes -or (Get-Hash $path) -ne $file.sha256) { throw "文件校验失败：$($file.path)" }
    }
}
function Get-EnvironmentReport {
    $admin=Test-IsAdmin; $storage=$null; $context=$null; $writable=$false; $secure=$null; $found=@(); $entries=@(); $detail=''; $kind='未检测到'
    try { $storage=Get-StorageInfo } catch { $detail=$_.Exception.Message }
    if ($admin) {
        try { $secure=Get-SecureBoot } catch { }
        if ($storage -and $storage.gpt -and $storage.espFound) {
            try {
                $context=Open-Esp $storage
                $probe=Join-Path $context.root ('.gpuunlocker-probe-' + [guid]::NewGuid().ToString('N'))
                try {
                    [IO.File]::WriteAllText($probe,'probe')
                    if ([IO.File]::ReadAllText($probe) -ne 'probe') { throw 'ESP 读取校验失败。' }
                    $writable=$true
                } finally { if(Test-Path -LiteralPath $probe){Remove-Item -LiteralPath $probe -Force} }
                $found=@(Get-ExistingPaths $context.root)
                $state=Read-State $context.root
                if ($state -and $state.schema -eq 1) { $kind='旧版脚本安装' } elseif($state){$kind='桌面程序安装'} elseif($found.Count){$kind='部分残留'}
                $entries=@(Get-UnlockEntries $state)
            } catch { $writable=$false; $detail=$_.Exception.Message }
            finally { Close-Esp $context }
        }
    }
    $has=($found.Count -gt 0 -or $entries.Count -gt 0)
    $gpt=($storage -and $storage.gpt); $esp=($storage -and $storage.espFound)
    $checks=@(
        @{key='admin';label='管理员权限';status=$(if($admin){'Pass'}else{'Fail'});detail=$(if($admin){'已以管理员权限运行'}else{'请以管理员权限重新打开'})},
        @{key='gpt';label='系统磁盘为 GPT';status=$(if($gpt){'Pass'}elseif($storage){'Fail'}else{'Unknown'});detail=$(if($gpt){'已确认当前 Windows 所在磁盘'}else{$detail})},
        @{key='esp';label='系统磁盘具有唯一 ESP';status=$(if($esp){'Pass'}elseif($storage){'Fail'}else{'Unknown'});detail=$(if($esp){'已确认当前系统 ESP'}else{'未找到唯一且正在使用的系统 ESP'})},
        @{key='access';label='ESP 读写权限';status=$(if($writable){'Pass'}elseif(-not $admin){'Unknown'}else{'Fail'});detail=$(if($writable){'临时读写探测通过，探测文件已清除'}elseif(-not $admin){'需管理员权限才能检查'}else{$detail})},
        @{key='clean';label='ESP 无 unlock 文件或残留';status=$(if(-not $writable){'Unknown'}elseif($has){'Fail'}else{'Pass'});detail=$(if(-not $writable){'暂不能确认'}elseif($has){"$kind；发现 $($found.Count) 个路径、$($entries.Count) 个启动项"}else{'未发现已知 unlock 文件和启动项'})},
        @{key='secure';label='Secure Boot 已关闭';status=$(if($secure -eq $false){'Pass'}elseif($secure -eq $true){'Fail'}else{'Unknown'});detail=$(if($secure -eq $false){'已关闭'}elseif($secure){'请在 BIOS 中关闭 Secure Boot'}else{'无法确认，需管理员权限及 UEFI 环境'})}
    )
    return @{isAdmin=[bool]$admin;gpt=[bool]$gpt;espFound=[bool]$esp;espWritable=$writable;secureBootEnabled=$secure;hasUnlockFiles=$has;unlockFiles=@($found);installationKind=$kind;checks=$checks}
}
function Test-TuringPrerequisite {
    $class='HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
    $adapters=@(Get-ChildItem $class | ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object {
        if ($_.MatchingDeviceId -match '(?i)ven_10de&dev_([0-9a-f]{4})') {
            $id=[Convert]::ToInt32($Matches[1],16)
            ($id -ge 0x1e02 -and $id -le 0x1fff) -or ($id -ge 0x2182 -and $id -le 0x21d1)
        }
    })
    if(-not $adapters.Count){throw '未检测到 Turing 显卡；不能跳过本机 GSP 前置检查。'}
    if(@($adapters | Where-Object {$_.EnableGpuFirmware -ne 1}).Count){throw '20 系需要先启用 GSP 固件模式并重新启动。请参考依赖目录 README 中的 Turing 前置步骤；本程序不会修改注册表。'}
    $smi=Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if(-not $smi){throw '无法找到 nvidia-smi，不能确认 20 系 GSP 运行状态。'}
    $output=& $smi.Source -q -x 2>&1
    if($LASTEXITCODE -ne 0){throw 'nvidia-smi 检查失败，不能确认 GSP 运行状态。'}
    $settings=New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing=[Xml.DtdProcessing]::Ignore
    $settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create((New-Object IO.StringReader(($output -join "`n"))),$settings)
    try {$document=New-Object Xml.XmlDocument;$document.Load($reader)}finally{$reader.Dispose()}
    Assert-TuringRuntime $document $adapters.Count
}
function Assert-TuringRuntime([xml]$Document,[int]$Expected) {
    $versions=@(foreach($gpu in $Document.SelectNodes('/nvidia_smi_log/gpu')){
        $node=$gpu.SelectSingleNode('pci/pci_device_id')
        if(-not $node){continue}
        $hex=$node.InnerText -replace '^0x',''
        if($hex -notmatch '^[a-fA-F0-9]{8}$'){continue}
        $id=[Convert]::ToUInt32($hex.Substring(0,4),16)
        if(($id -ge 0x1e02 -and $id -le 0x1fff) -or ($id -ge 0x2182 -and $id -le 0x21d1)){
            $version=$gpu.SelectSingleNode('gsp_firmware_version')
            if($version){$version.InnerText.Trim()}else{'Unknown'}
        }
    })
    if($versions.Count -ne $Expected -or @($versions | Where-Object {$_ -match '^(?i:N/?A|Unknown)?$'}).Count){throw '20 系 GSP 固件尚未生效或无法确认。请先按上游说明启用并重新启动，不能使用休眠恢复代替。'}
}

function New-Backup([string]$Purpose) {
    $root=Join-Path $env:ProgramData 'GPUUnlocker\Backups'
    $path=Join-Path $root ((Get-Date -Format yyyyMMdd-HHmmss) + '-' + $Purpose + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    Invoke-Bcd @('/enum','firmware','/v') | Set-Content -LiteralPath (Join-Path $path 'firmware.txt') -Encoding UTF8
    Invoke-Bcd @('/export',(Join-Path $path 'BCD')) | Out-Null
    return $path
}
function Copy-ToBackup([string]$Root,[string]$Backup,[string[]]$Paths) {
    foreach($relative in $Paths){
        $source=Join-Path $Root $relative
        if(Test-Path -LiteralPath $source -PathType Leaf){
            $target=Join-Path $Backup $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
            if((Get-Hash $source) -ne (Get-Hash $target)){throw "备份校验失败：$relative"}
        }
    }
}
function Get-StateId($State){
    if($State.bcdId){return [string]$State.bcdId}
    if($State.bcd_id){return [string]$State.bcd_id}
    return $null
}
function Get-Verification($Context) {
    $state=Read-State $Context.root; $files=$false; $controls=$false; $entry=$false; $messages=@()
    try {
        if(-not $state){throw '没有有效安装记录。'}
        [void](Assert-Manifest $state)
        if([string]$state.partitionGuid -ne [string]$Context.partition.Guid){throw '安装记录与当前 ESP GUID 不匹配。'}
        if($state.phase -ne 'installed'){throw '安装事务尚未完成，请先移除残留再重新安装。'}
        Test-Files $Context.root $state
        $files=$true
    } catch {$messages+=$_.Exception.Message}
    try {
        $auto=Join-Path $Context.root 'i_know_what_i_am_doing.txt'
        $chain=Join-Path $Context.root 'chain-target.txt'
        $lines=@(Get-Content -LiteralPath $chain | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') })
        $controls=(Test-Path -LiteralPath $auto -PathType Leaf) -and $lines.Count -gt 0 -and $lines[0].Trim() -ieq ('\' + $WindowsRelative)
        if(-not $controls){$messages+='自动运行或 Windows 链式启动配置无效。'}
    } catch {$messages+='控制文件缺失或无法读取。'}
    if($state){
        $id=Get-StateId $state
        $entries=@(Get-BootEntries)
        $windows=@($entries | Where-Object {$_.id -eq $WindowsBootId -and $_.path -ieq ('\' + $WindowsRelative)})
        $unlock=@($entries | Where-Object {$_.id -eq $id -and $_.path -ieq ('\' + $EfiRelative)})
        $entry=$windows.Count -eq 1 -and $unlock.Count -eq 1 -and $windows[0].device -eq $unlock[0].device -and (Test-Path -LiteralPath (Join-Path $Context.root $WindowsRelative) -PathType Leaf)
        if(-not $entry){$messages+='GPU Unlock 启动项、设备或 Windows 回退启动项不正确。'}
    }
    $order=@();$sequence=@();$orderKnown=$false;$first=$false;$listed=$false;$windowsListed=$false;$orderMessage=''
    try {
        $order=@(Get-FirmwareOrder);$sequence=@(Get-FirmwareBootSequence);$orderKnown=$true
        $windowsListed=$WindowsBootId -in $order
        if($entry){$listed=$id -in $order;$first=$order.Count -gt 0 -and $order[0] -eq $id}
        $orderMessage=if($first){'GPU Unlock 当前为第一启动项。'}elseif($listed){'GPU Unlock 当前不是第一启动项，可在步骤 4 设置。'}else{'GPU Unlock 未列入当前启动顺序。'}
        if(-not $windowsListed){$orderMessage+=' Windows Boot Manager 未列入当前启动顺序，设置首选时会补充回退入口。'}
        if($sequence.Count){$orderMessage+=' 检测到一次性启动序列，下次启动可能优先按该序列执行。'}
    } catch {$orderMessage='无法读取固件启动顺序：'+$_.Exception.Message}
    $complete=$files -and $controls -and $entry
    return @{filesValid=$files;controlsValid=[bool]$controls;bootEntryValid=[bool]$entry;bootOrderKnown=$orderKnown;unlockFirst=$first;unlockInBootOrder=$listed;windowsInBootOrder=$windowsListed;firmwareOrder=$order;bootSequence=$sequence;bootOrderMessage=$orderMessage;message=$(if($complete){'安装文件与启动项有效；'+$orderMessage}else{$messages -join '；'})}
}
function Test-UnlockInstallation {
    Assert-Admin
    $context=$null
    try {$context=Open-Esp (Get-StorageInfo);return Get-Verification $context}
    finally {Close-Esp $context}
}

function Set-UnlockFirst {
    Assert-Admin
    if((Get-SecureBoot) -ne $false){throw 'Secure Boot 尚未关闭或无法确认，不能设置解锁器为首选。'}
    $context=$null
    try {
        $context=Open-Esp (Get-StorageInfo)
        $verification=Get-Verification $context
        if(-not ($verification.filesValid -and $verification.controlsValid -and $verification.bootEntryValid -and $verification.bootOrderKnown)){
            throw ('安装或固件顺序验证未通过：'+$verification.message+' '+$verification.bootOrderMessage)
        }
        $id=Get-StateId (Read-State $context.root)
        $oldOrder=@($verification.firmwareOrder);$oldSequence=@($verification.bootSequence)
        $desired=@($id)+@($oldOrder | Where-Object {$_ -ne $id})
        if($WindowsBootId -notin $desired){$desired+=@($WindowsBootId)}
        if(($desired -join '|') -eq ($oldOrder -join '|')){return $verification}
        $backup=New-Backup 'boot-order'
        Write-Json (Join-Path $backup 'boot-order.json') @{before=$oldOrder;after=$desired;bootSequence=$oldSequence;entryId=$id;partitionGuid=[string]$context.partition.Guid}
        Write-Host "启动顺序备份：$backup"
        $attempted=$false
        try {
            if((@(Get-FirmwareOrder) -join '|') -ne ($oldOrder -join '|') -or (@(Get-FirmwareBootSequence) -join '|') -ne ($oldSequence -join '|')){throw '固件启动配置已被其他程序修改，请重新检查。'}
            $attempted=$true
            Invoke-Bcd (@('/set','{fwbootmgr}','displayorder') + $desired) | Out-Null
            if((@(Get-FirmwareOrder) -join '|') -ne ($desired -join '|')){throw '固件未接受首选启动顺序。'}
            if((@(Get-FirmwareBootSequence) -join '|') -ne ($oldSequence -join '|')){throw '一次性启动序列发生意外变化。'}
            $result=Get-Verification $context
            if(-not ($result.filesValid -and $result.controlsValid -and $result.bootEntryValid -and $result.unlockFirst -and $result.windowsInBootOrder)){throw '设置后的安装与启动顺序验证失败。'}
            Write-Host '已将 GPU Unlock 设为第一启动项；实际开机行为需重启验证。'
            return $result
        } catch {
            $failure=$_.Exception.Message
            if($attempted){
                try {
                    $live=@(Get-FirmwareOrder)
                    if((@(Get-FirmwareBootSequence) -join '|') -ne ($oldSequence -join '|') -or (($live -join '|') -ne ($oldOrder -join '|') -and ($live -join '|') -ne ($desired -join '|'))){throw '固件配置出现外部变化，停止自动覆盖，请重新检查。'}
                    Invoke-Bcd (@('/set','{fwbootmgr}','displayorder')+$oldOrder) | Out-Null
                    if((@(Get-FirmwareOrder) -join '|') -ne ($oldOrder -join '|')){throw '原顺序恢复验证失败。'}
                    Write-Host '设置未完成，已恢复操作前的启动配置。'
                } catch {throw "设置失败：$failure；恢复失败：$($_.Exception.Message)。备份：$backup"}
            }
            throw "设置失败：$failure；备份：$backup"
        }
    } finally {Close-Esp $context}
}

function Install-Unlock([string]$PackagePath) {
    Assert-Admin
    $report=Get-EnvironmentReport
    if(-not $report.gpt -or -not $report.espFound -or -not $report.espWritable -or $report.secureBootEnabled -ne $false){throw '环境检查未全部通过，不能写入。'}
    if($report.hasUnlockFiles){throw '检测到ESP分区中已有unlock文件，若要重复写入，请先移除'}
    $manifest=Get-Content -LiteralPath (Join-Path $PackagePath 'package.json') -Raw | ConvertFrom-Json
    if($manifest.schema -ne 2 -or $manifest.release -ne 'v2.1.5'){throw '不是受支持的 v2.1.5 桌面程序部署包。'}
    $family=Assert-Manifest $manifest
    Test-Files $PackagePath $manifest
    if($family -eq 'tu10x'){Test-TuringPrerequisite}
    $context=$null; $copied=@(); $newId=$null; $stateCreated=$false; $oldOrder=@(); $backup=$null
    try {
        $context=Open-Esp (Get-StorageInfo)
        if(@(Get-ExistingPaths $context.root).Count -or @(Get-UnlockEntries).Count){throw 'ESP 状态已变化，发现已有补丁。'}
        $windows=Join-Path $context.root $WindowsRelative
        if(-not (Test-Path -LiteralPath $windows -PathType Leaf)){throw '当前 ESP 缺少 Windows 引导文件。'}
        $windowsHash=Get-Hash $windows
        $oldOrder=@(Get-FirmwareOrder)
        $state=@{schema=2;mode='gpu-unlocker';family=$family;release='v2.1.5';files=@($manifest.files);partitionGuid=[string]$context.partition.Guid;phase='copying';bcdId=$null;installed=(Get-Date -Format o)}
        $recordBytes=[Text.Encoding]::UTF8.GetByteCount(($state | ConvertTo-Json -Depth 12)) + 4096
        $required=[long](($manifest.files | Measure-Object bytes -Sum).Sum) + $recordBytes
        if((Get-FreeSpace $context) -lt ($required + 10MB)){throw 'ESP 空间不足：全部文件和安装记录写入后需额外保留至少 10 MiB。'}
        $backup=New-Backup 'install'
        Copy-ToBackup $context.root $backup @($WindowsRelative,'loader.log')
        Write-Host "备份已保存：$backup"
        New-Item -ItemType Directory -Path (Join-Path $context.root 'EFI\GPUUnlock') | Out-Null
        Write-Json (Join-Path $context.root $StateRelative) $state
        $stateCreated=$true
        foreach($file in $manifest.files){
            $relative=([string]$file.path).Replace('/','\')
            $dest=Join-Path $context.root $relative
            $temp=$dest + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
            try {
                Copy-Item -LiteralPath (Join-Path $PackagePath $relative) -Destination $temp
                if((Get-Hash $temp) -ne $file.sha256){throw "复制校验失败：$relative"}
                Move-Item -LiteralPath $temp -Destination $dest
                $copied+=$relative
                Write-Host "已写入 $relative"
            } finally {if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}
        }
        Test-Files $context.root $state
        $output=Invoke-Bcd @('/copy','{bootmgr}','/d','GPU Unlock')
        $newId=[regex]::Match($output,'\{[0-9a-fA-F-]{36}\}').Value
        if(-not $newId -or $newId -in @($WindowsBootId,$FirmwareId)){throw '没有获得有效的新启动项 ID。'}
        $state.bcdId=$newId
        Write-Json (Join-Path $context.root $StateRelative) $state
        Invoke-Bcd @('/set',$newId,'device',('partition=' + $context.root.TrimEnd('\'))) | Out-Null
        Invoke-Bcd @('/set',$newId,'path',('\' + $EfiRelative)) | Out-Null
        # Keep all original entries in the same order and append our entry.
        Invoke-Bcd (@('/set','{fwbootmgr}','displayorder') + $oldOrder + @($newId)) | Out-Null
        $order=@(Get-FirmwareOrder)
        if(($order -join '|') -ne (($oldOrder + @($newId)) -join '|')){throw '固件启动顺序验证失败。'}
        if((Get-Hash $windows) -ne $windowsHash){throw 'Windows 引导文件发生意外变化。'}
        $state.phase='installed'
        Write-Json (Join-Path $context.root $StateRelative) $state
        $result=Get-Verification $context
        if(-not ($result.filesValid -and $result.controlsValid -and $result.bootEntryValid)){throw $result.message}
        Write-Json (Join-Path $backup 'installed.json') $state
        return $result
    } catch {
        $failure=$_.Exception.Message
        if($stateCreated){
            try {
                if($newId){Invoke-Bcd @('/delete',$newId,'/f') | Out-Null}
                if($oldOrder.Count){Invoke-Bcd (@('/set','{fwbootmgr}','displayorder') + $oldOrder) | Out-Null}
                foreach($relative in $copied){Remove-Item -LiteralPath (Join-Path $context.root $relative) -Force}
                Remove-Item -LiteralPath (Join-Path $context.root $StateRelative) -Force
                $dir=Join-Path $context.root 'EFI\GPUUnlock'
                if(@(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0){Remove-Item -LiteralPath $dir}
                Write-Host '本次安装已回滚。'
            } catch { $failure+='；回滚未完成，保留记录供下次移除：' + $_.Exception.Message }
        }
        throw $failure
    } finally {Close-Esp $context}
}

function Remove-UnlockInstallation {
    Assert-Admin
    $context=$null
    try {
        $context=Open-Esp (Get-StorageInfo)
        $found=@(Get-ExistingPaths $context.root)
        $state=Read-State $context.root
        $trusted=$false
        if($state){try{[void](Assert-Manifest $state);$trusted=([string]$state.partitionGuid -eq [string]$context.partition.Guid)}catch{}}
        $entries=@(Get-UnlockEntries $(if($trusted){$state}else{$null}))
        if(-not $found.Count -and -not $entries.Count){return @{removed=$true;message='未发现 unlock 补丁。'}}
        if(-not (Test-Path -LiteralPath (Join-Path $context.root $WindowsRelative) -PathType Leaf)){throw 'Windows 回退引导文件缺失，已保留 unlock 文件和启动项，请先恢复 Windows 引导。'}
        $backup=New-Backup 'remove'
        $paths=@($found | Where-Object { $_ -ne 'EFI\GPUUnlock' })
        $efi=Join-Path $context.root $EfiRelative
        if(Test-Path -LiteralPath $efi -PathType Leaf){
            $hash=Get-Hash $efi
            $known=$hash -in @('9C615A3F5F16DFBE4800A5C46251CDB20F2886BBB4BEC70FECAB3400A3C2F7C9','B5E96F4E0CA162B98693AF00452A6933EA47DD7E24F0D4B3C804B48A05EC9DEA')
            if($trusted){$record=@($state.files | Where-Object {([string]$_.path).Replace('/','\') -ieq $EfiRelative});$known=$known -or ($record.Count -eq 1 -and $record[0].sha256 -eq $hash)}
            if(-not $known){throw 'EFI/GPUUnlock/BOOTX64.EFI 归属无法确认，已保留；请人工核查后再移除。'}
        }
        foreach($relative in $paths){
            if(-not (Test-Path -LiteralPath (Join-Path $context.root $relative) -PathType Leaf)){throw "发现异常目录，不能自动移除：$relative"}
            Write-Host "将移除 $relative"
        }
        Copy-ToBackup $context.root $backup $paths
        Write-Host "移除前备份：$backup"
        # Use the live order, never restore an installation-time order over later user changes.
        $ids=@($entries | ForEach-Object {$_.id})
        # Clean a recorded dangling ID only when it has not been reused by an unrelated entry.
        if($trusted){
            $recordedId=Get-StateId $state
            if($recordedId -and $recordedId -notin @($WindowsBootId,$FirmwareId) -and $recordedId -notin $ids -and @((Get-BootEntries) | Where-Object {$_.id -eq $recordedId}).Count -eq 0){$ids+=@($recordedId)}
        }
        $before=@(Get-FirmwareOrder);$sequence=@(Get-FirmwareBootSequence)
        $remainingOrder=@($before | Where-Object {$_ -notin $ids})
        if($WindowsBootId -notin $remainingOrder){$remainingOrder+=@($WindowsBootId)}
        $remainingSequence=@($sequence | Where-Object {$_ -notin $ids})
        Write-Json (Join-Path $backup 'removal.json') @{paths=$paths;entryIds=$ids;partitionGuid=[string]$context.partition.Guid;firmwareOrder=$before;bootSequence=$sequence;remainingOrder=$remainingOrder;remainingSequence=$remainingSequence}
        if($ids.Count){
            if((@(Get-FirmwareOrder) -join '|') -ne ($before -join '|') -or (@(Get-FirmwareBootSequence) -join '|') -ne ($sequence -join '|')){throw '固件启动配置已变化，请重新检查；已保留 ESP 文件。'}
            if(($before -join '|') -ne ($remainingOrder -join '|')){Invoke-Bcd (@('/set','{fwbootmgr}','displayorder')+$remainingOrder) | Out-Null}
            if((@(Get-FirmwareOrder) -join '|') -ne ($remainingOrder -join '|')){throw '移除前的启动顺序验证失败，已保留 ESP 文件。'}
            if(($sequence -join '|') -ne ($remainingSequence -join '|')){Set-FirmwareBootSequence $remainingSequence}
            foreach($entry in $entries){Invoke-Bcd @('/delete',$entry.id,'/f') | Out-Null}
            if(@(Get-UnlockEntries $(if($trusted){$state}else{$null})).Count){throw '仍有 GPU Unlock 启动项，已保留 ESP 文件。'}
            if((@(Get-FirmwareOrder) -join '|') -ne ($remainingOrder -join '|') -or (@(Get-FirmwareBootSequence) -join '|') -ne ($remainingSequence -join '|')){throw '移除后的固件配置验证失败，已保留 ESP 文件。'}
        }
        foreach($relative in $paths){Remove-Item -LiteralPath (Join-Path $context.root $relative) -Force}
        $dir=Join-Path $context.root 'EFI\GPUUnlock'
        if((Test-Path -LiteralPath $dir) -and @(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0){Remove-Item -LiteralPath $dir}
        $remaining=@(Get-ExistingPaths $context.root)
        if($remaining.Count){throw '已移除已知文件，但专用目录仍有未知内容，已保留。请人工核查。'}
        return @{removed=$true;message='unlock 补丁和对应启动项已移除；Windows 引导与 loader.log 已保留。'}
    } finally {Close-Esp $context}
}
