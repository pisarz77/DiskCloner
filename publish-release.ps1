param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [int]$KeepLatest = 2,
    [string]$ProjectPath = "DiskCloner.UI/DiskCloner.UI.csproj",
    [string]$PublishRoot = "publish"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($KeepLatest -lt 1) {
    throw "KeepLatest must be >= 1."
}

if (!(Test-Path -LiteralPath $ProjectPath)) {
    throw "Project not found: $ProjectPath"
}

if (!(Test-Path -LiteralPath $PublishRoot)) {
    New-Item -ItemType Directory -Path $PublishRoot | Out-Null
}

$publishDirs = Get-ChildItem -Path $PublishRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^publish_\d{8}_fix\d+$' }

$maxFix = 0
foreach ($dir in $publishDirs) {
    if ($dir.Name -match '^publish_\d{8}_fix(?<n>\d+)$') {
        $num = [int]$Matches['n']
        if ($num -gt $maxFix) {
            $maxFix = $num
        }
    }
}

$nextFix = $maxFix + 1
$datePart = Get-Date -Format "yyyyMMdd"
$targetDir = Join-Path $PublishRoot "publish_${datePart}_fix$nextFix"

Write-Host "Publishing single-file build to: $targetDir"

$publishArgs = @(
    "publish",
    $ProjectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", $targetDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$allPublishDirs = Get-ChildItem -Path $PublishRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^publish_\d{8}_fix\d+$' } |
    Sort-Object LastWriteTime -Descending

$toRemove = $allPublishDirs | Select-Object -Skip $KeepLatest
foreach ($dir in $toRemove) {
    Write-Host "Removing old publish directory: $($dir.FullName)"
    Remove-Item -LiteralPath $dir.FullName -Recurse -Force
}

Write-Host "Publish complete."
Write-Host "Output: $targetDir"
