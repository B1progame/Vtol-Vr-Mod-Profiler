param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0",
    [string]$InnoCompiler = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\src\VTOLVRWorkshopProfileSwitcher\VTOLVRWorkshopProfileSwitcher.csproj"
$publishRoot = Join-Path $PSScriptRoot "..\publish"
$publishDir = Join-Path $publishRoot ("win-x64-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$issPath = Join-Path $PSScriptRoot "..\installer\VTOLVRWorkshopProfileSwitcher.iss"
$iconPath = Join-Path $PSScriptRoot "..\src\VTOLVRWorkshopProfileSwitcher\Assets\AppIcon.ico"

Write-Host "Stopping running app if present..."
Get-Process VTOLVRWorkshopProfileSwitcher -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Restoring packages for runtime $Runtime ..."
dotnet restore $project -r $Runtime
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}

Write-Host "Publishing app to $publishDir ..."
dotnet publish $project -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $InnoCompiler)) {
    Write-Warning "Inno Setup compiler not found: $InnoCompiler"
    Write-Warning "Install Inno Setup 6, then run this script again."
    Write-Host "Published app is ready at: $publishDir"
    exit 0
}

Write-Host "Building installer..."
& $InnoCompiler "/DMyAppVersion=$Version" "/DSourceDir=$publishDir" "/DIconFile=$iconPath" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compile failed with exit code $LASTEXITCODE"
}

Write-Host "Done. Installer output: installer\\output"
