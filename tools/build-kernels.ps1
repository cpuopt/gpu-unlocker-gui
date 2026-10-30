param([string]$Ptxas)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
if(-not $Ptxas){$Ptxas=Join-Path $project '.tools\cuda-nvcc\ptxas.exe'}
if(-not(Test-Path -LiteralPath $Ptxas)){throw 'Specify -Ptxas pointing to CUDA 12.8.93 ptxas.exe. See README.'}
$out=Join-Path $project 'src\GpuUnlocker.Core\Kernels'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$lock=@{}
foreach($arch in @(75,80,86,89)){
    $code=New-Object Text.StringBuilder
    [void]$code.AppendLine("// GPU Unlocker original register-resident MMA throughput kernel. Not a GEMM benchmark.")
    [void]$code.AppendLine(".version 8.0`n.target sm_$arch`n.address_size 64")
    $modes=if($arch -eq 75){@('fp16')}else{@('fp16','bf16','tf32')}
    foreach($mode in $modes){
        $k=if($mode -eq 'tf32' -or $arch -eq 75){8}else{16}
        $type=if($mode -eq 'fp16'){'f16'}elseif($mode -eq 'bf16'){'bf16'}else{'tf32'}
        $one=if($mode -eq 'fp16'){'0x3c003c00'}elseif($mode -eq 'bf16'){'0x3f803f80'}else{'0x3f800000'}
        [void]$code.AppendLine(@"
.visible .entry bench_$mode(.param .u32 iterations, .param .u64 output) {
 .reg .pred %p;
 .reg .b32 %a<4>, %b<2>, %n, %i, %thread, %bid, %nt, %idx;
 .reg .b64 %ptr, %off;
 .reg .f32 %c<32>, %sum;
 ld.param.u32 %n, [iterations];
 ld.param.u64 %ptr, [output];
 mov.u32 %a0, $one; mov.u32 %a1, $one; mov.u32 %a2, $one; mov.u32 %a3, $one;
 mov.u32 %b0, $one; mov.u32 %b1, $one;
"@)
        foreach($i in 0..31){[void]$code.AppendLine(" mov.f32 %c$i, 0f00000000;")}
        [void]$code.AppendLine(" mov.u32 %i, 0;`nLOOP:")
        foreach($chain in 0..7){
            $c=($chain*4)..($chain*4+3) | ForEach-Object {"%c$_"}
            $a=if($arch -eq 75){'%a0, %a1'}else{'%a0, %a1, %a2, %a3'}
            $b=if($arch -eq 75){'%b0'}else{'%b0, %b1'}
            [void]$code.AppendLine(" mma.sync.aligned.m16n8k$k.row.col.f32.$type.$type.f32 {$($c -join ', ')}, {$a}, {$b}, {$($c -join ', ')};")
        }
        [void]$code.AppendLine(" add.u32 %i, %i, 1;`n setp.lt.u32 %p, %i, %n;`n @%p bra LOOP;`n mov.f32 %sum, 0f00000000;")
        foreach($i in 0..31){[void]$code.AppendLine(" add.f32 %sum, %sum, %c$i;")}
        [void]$code.AppendLine(@'
 mov.u32 %thread, %tid.x;
 mov.u32 %bid, %ctaid.x;
 mov.u32 %nt, %ntid.x;
 mad.lo.u32 %idx, %bid, %nt, %thread;
 mul.wide.u32 %off, %idx, 4;
 add.u64 %ptr, %ptr, %off;
 st.global.f32 [%ptr], %sum;
 ret;
}
'@)
    }
    $source=Join-Path $out "tensor_sm$arch.ptx"
    $binary=Join-Path $out "tensor_sm$arch.cubin"
    [IO.File]::WriteAllText($source,$code.ToString(),[Text.Encoding]::ASCII)
    & $Ptxas -arch="sm_$arch" -O3 -o $binary $source
    if($LASTEXITCODE -ne 0){throw "ptxas failed for sm_$arch"}
    $lock["sm$arch"]=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
}
@{compiler='NVIDIA ptxas 12.8.93';kernels=$lock} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'kernel-lock.json') -Encoding UTF8
Write-Host 'Built sm75/sm80/sm86/sm89 cubins. End users do not need CUDA Toolkit.'
