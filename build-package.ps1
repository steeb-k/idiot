# Build and package script for WIM-ISO-Driver-Injector
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$projectName = "idiot"
$outputRoot = ".\publish"
$publishDir = "$outputRoot\$Runtime"
$portableDir = "$outputRoot\portable"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Building $projectName v$Version" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Cyan
Write-Host "Runtime: $Runtime" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Clean previous builds
if (Test-Path $outputRoot) {
    Write-Host "Cleaning previous build..." -ForegroundColor Yellow
    Remove-Item $outputRoot -Recurse -Force
}

# Restore dependencies
Write-Host "Restoring dependencies..." -ForegroundColor Green
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "Restore failed!"
    exit 1
}

# Publish self-contained
Write-Host "`nPublishing self-contained build..." -ForegroundColor Green
dotnet publish `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $publishDir `
    /p:PublishTrimmed=false `
    /p:PublishReadyToRun=true `
    /p:WindowsAppSDKSelfContained=true

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed!"
    exit 1
}

Write-Host "? Build successful!`n" -ForegroundColor Green

# Wait for file handles to be released
Write-Host "Waiting for file handles to be released..." -ForegroundColor Gray
Start-Sleep -Seconds 2

# Create portable package
Write-Host "Creating portable package..." -ForegroundColor Cyan
$zipFile = "$outputRoot\$projectName-v$Version-$Runtime-portable.zip"

if (Test-Path $zipFile) {
    Remove-Item $zipFile -Force
}

# Create a clean portable directory
New-Item -ItemType Directory -Path $portableDir -Force | Out-Null
Copy-Item "$publishDir\*" -Destination $portableDir -Recurse -Force

# Create user-friendly README for portable version
$readmeContent = @"
I.D.I.O.T. - Image Driver Integration & Optimization Tool
==========================================================

QUICK START
-----------
1. Run idiot.exe
2. No installation or .NET runtime required
3. All files must remain in the same folder

SYSTEM REQUIREMENTS
-------------------
- Windows 10 version 1809 (build 17763) or later
- 64-bit (x64) processor

DOCUMENTATION
-------------
GitHub: https://github.com/steeb-k/idiot

LICENSE
-------
MIT License - See LICENSE file for details
"@

$readmeContent | Out-File -FilePath "$portableDir\README.txt" -Encoding utf8

# Wait again before zipping
Start-Sleep -Seconds 1

# Create ZIP with retry logic
$maxRetries = 3
$retryCount = 0
$zipCreated = $false

while (-not $zipCreated -and $retryCount -lt $maxRetries) {
    try {
        Compress-Archive -Path "$portableDir\*" -DestinationPath $zipFile -CompressionLevel Optimal -Force -ErrorAction Stop
        $zipCreated = $true
    }
    catch {
        $retryCount++
        if ($retryCount -lt $maxRetries) {
            Write-Host "  ZIP creation failed, retrying ($retryCount/$maxRetries)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
        else {
            Write-Error "Failed to create ZIP after $maxRetries attempts: $_"
            exit 1
        }
    }
}

$zipSize = (Get-Item $zipFile).Length / 1MB
Write-Host "? Portable ZIP created: $zipFile" -ForegroundColor Green
Write-Host "  Size: $([math]::Round($zipSize, 2)) MB`n" -ForegroundColor Gray

# Build installer with Inno Setup (if available and not skipped)
$installerFile = "$outputRoot\idiot-v$Version-$Runtime-installer.exe"
$innoSetupPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"

if (-not $SkipInstaller) {
    if (Test-Path $innoSetupPath) {
        Write-Host "Creating installer with Inno Setup..." -ForegroundColor Cyan
        
        # Get absolute paths for files
        $rootDir = Get-Location
        $licensePath = Join-Path $rootDir "LICENSE"
        $iconPath = Join-Path $rootDir "idiotLogo.ico"
        $publishPath = Join-Path $rootDir $publishDir
        
        # Update version and paths in installer script
        $issContent = Get-Content "installer.iss" -Raw
        $issContent = $issContent -replace '#define MyAppVersion ".*"', "#define MyAppVersion `"$Version`""
        
        # Add LicenseFile path if LICENSE exists
        if (Test-Path $licensePath) {
            $issContent = $issContent -replace '(\[Setup\])', "`$1`nLicenseFile=$licensePath"
        }
        
        # Fix paths to use absolute paths
        $issContent = $issContent -replace 'SetupIconFile=idiotLogo\.ico', "SetupIconFile=$iconPath"
        $issContent = $issContent -replace 'Source: "publish\\win-x64\\\*"', "Source: `"$publishPath\*`""
        
        $tempIss = "$outputRoot\installer-temp.iss"
        $issContent | Set-Content $tempIss
        
        & $innoSetupPath $tempIss /O"$outputRoot" /Q
        
        if ($LASTEXITCODE -eq 0 -and (Test-Path $installerFile)) {
            $installerSize = (Get-Item $installerFile).Length / 1MB
            Write-Host "? Installer created: $installerFile" -ForegroundColor Green
            Write-Host "  Size: $([math]::Round($installerSize, 2)) MB`n" -ForegroundColor Gray
            Remove-Item $tempIss -Force -ErrorAction SilentlyContinue
        } else {
            Write-Host "? Installer build failed" -ForegroundColor Yellow
            Write-Host "  Check $tempIss for details`n" -ForegroundColor Gray
        }
    } else {
        Write-Host "? Inno Setup not found at: $innoSetupPath" -ForegroundColor Yellow
        Write-Host "  Download from: https://jrsoftware.org/isdl.php" -ForegroundColor Gray
        Write-Host "  Or run with -SkipInstaller to skip installer creation`n" -ForegroundColor Gray
    }
}

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Packaging Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Published files: $publishDir" -ForegroundColor White
Write-Host "Portable ZIP:    $zipFile" -ForegroundColor White
if (Test-Path $installerFile) {
    Write-Host "Installer:       $installerFile" -ForegroundColor White
}
Write-Host "`nTo test the portable version:" -ForegroundColor Yellow
Write-Host "  Extract the ZIP and run $projectName.exe`n" -ForegroundColor Yellow
if (Test-Path $installerFile) {
    Write-Host "To test the installer:" -ForegroundColor Yellow
    Write-Host "  Run $installerFile and follow the setup wizard`n" -ForegroundColor Yellow
}
