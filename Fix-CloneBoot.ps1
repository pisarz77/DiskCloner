param(
    [int]$TargetDiskNumber = 1,
    [switch]$KeepDriveLetters
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = [Security.Principal.WindowsPrincipal]$id
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script as Administrator."
    }
}

function Get-FreeLetter([char[]]$Preferred) {
    $used = Get-Volume | Where-Object DriveLetter | Select-Object -ExpandProperty DriveLetter
    foreach ($l in $Preferred) {
        if ($used -notcontains $l) { return $l }
    }
    foreach ($l in [char[]]([char]'Z'..[char]'D')) {
        if ($used -notcontains $l) { return $l }
    }
    throw "No free drive letter available."
}

Assert-Admin

$efiGuid   = "{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}" # EFI System Partition
$basicGuid = "{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}" # Basic data (Windows)

$parts = Get-Partition -DiskNumber $TargetDiskNumber
$efi   = $parts | Where-Object GptType -eq $efiGuid   | Select-Object -First 1
$win   = $parts | Where-Object GptType -eq $basicGuid | Sort-Object Size -Descending | Select-Object -First 1

if (-not $efi) { throw "EFI partition not found on disk $TargetDiskNumber." }
if (-not $win) { throw "Windows/basic partition not found on disk $TargetDiskNumber." }

$efiLetter = Get-FreeLetter @('S','P','O','N')
$winLetter = Get-FreeLetter @('W','V','T','R','Q')

$efiPath = "$efiLetter`:\"
$winPath = "$winLetter`:\"

Write-Host "Target disk: $TargetDiskNumber"
Write-Host "EFI partition: #$($efi.PartitionNumber) -> $efiPath"
Write-Host "Windows partition: #$($win.PartitionNumber) -> $winPath"

$dpFile = $null
try {
    Add-PartitionAccessPath -DiskNumber $TargetDiskNumber -PartitionNumber $efi.PartitionNumber -AccessPath $efiPath
    Add-PartitionAccessPath -DiskNumber $TargetDiskNumber -PartitionNumber $win.PartitionNumber -AccessPath $winPath

    if (-not (Test-Path "$winPath`Windows")) {
        throw "Mounted Windows partition does not contain \Windows at $winPath"
    }

    Write-Host "Running bcdboot..."
    & bcdboot.exe "$winPath`Windows" /s "$efiLetter`:" /f UEFI /c
    if ($LASTEXITCODE -ne 0) {
        throw "bcdboot failed with exit code $LASTEXITCODE"
    }

    Write-Host "Extending NTFS filesystem..."
    $dpFile = Join-Path $env:TEMP "diskpart_extend_fs_$TargetDiskNumber.txt"
@"
select volume $winLetter
extend filesystem
"@ | Set-Content -Path $dpFile -Encoding ASCII
    & diskpart.exe /s $dpFile

    Write-Host "Running CHKDSK fix..."
    & chkdsk.exe "$winLetter`:" /f /x
    if ($LASTEXITCODE -gt 3) {
        throw "chkdsk /f failed with exit code $LASTEXITCODE"
    }

    Write-Host "Checking dirty bit..."
    $dirtyOut = & fsutil.exe dirty query "$winLetter`:" 2>&1
    $dirtyText = ($dirtyOut | Out-String)
    Write-Host $dirtyText
    if ($dirtyText -match "is dirty") {
        throw "Target volume still reports DIRTY state after chkdsk."
    }

    if (-not (Test-Path "$efiPath`EFI\Microsoft\Boot\BCD")) {
        throw "BCD file missing on EFI: $efiPath`EFI\Microsoft\Boot\BCD"
    }

    Write-Host "Verification:"
    fsutil fsinfo volumeinfo "$winLetter`:"
    Get-Partition -DiskNumber $TargetDiskNumber | Select-Object PartitionNumber,Type,GptType,Size,Offset | Format-Table -AutoSize
    Get-Partition -DiskNumber $TargetDiskNumber | Get-Volume | Select-Object DriveLetter,FileSystem,Size,SizeRemaining | Format-Table -AutoSize

    Write-Host "Done."
}
finally {
    if ($dpFile -and (Test-Path $dpFile)) { Remove-Item $dpFile -Force -ErrorAction SilentlyContinue }

    if (-not $KeepDriveLetters) {
        try { Remove-PartitionAccessPath -DiskNumber $TargetDiskNumber -PartitionNumber $efi.PartitionNumber -AccessPath $efiPath } catch {}
        try { Remove-PartitionAccessPath -DiskNumber $TargetDiskNumber -PartitionNumber $win.PartitionNumber -AccessPath $winPath } catch {}
    }
}
