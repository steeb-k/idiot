# Sync version from version.json to all project files
# Usage: .\sync-version.ps1
# or to set a new version: .\sync-version.ps1 -Version "0.4.0" -GitHubTag "0.4.0-beta"

param(
    [string]$Version = "",
    [string]$GitHubTag = ""
)

$ErrorActionPreference = "Stop"

# Read current version from version.json
$versionFile = "version.json"
$versionData = Get-Content $versionFile | ConvertFrom-Json

if ($Version) {
    $versionData.version = $Version
}
if ($GitHubTag) {
    $versionData.gitHubTag = $GitHubTag
}

$currentVersion = $versionData.version
$githubTag = $versionData.gitHubTag

Write-Host "Synchronizing version: $currentVersion" -ForegroundColor Cyan
if ($GitHubTag) {
    Write-Host "GitHub tag: $githubTag" -ForegroundColor Cyan
}
Write-Host ""

# Update version.json if parameters were provided
if ($Version -or $GitHubTag) {
    $versionData | ConvertTo-Json | Set-Content $versionFile
    Write-Host "[OK] Updated version.json" -ForegroundColor Green
}

# Update Version.props (MSBuild)
$propsContent = @"
<Project>
  <PropertyGroup>
    <AppVersion>$currentVersion</AppVersion>
  </PropertyGroup>
</Project>
"@
$propsContent | Set-Content "Version.props"
Write-Host "[OK] Updated Version.props" -ForegroundColor Green

# Update WIMISODriverInjector.csproj
$csprojPath = "WIMISODriverInjector.csproj"
$csprojContent = Get-Content $csprojPath -Raw
$csprojContent = $csprojContent -replace '<Version>[^<]*</Version>', "<Version>$currentVersion</Version>"
$csprojContent | Set-Content $csprojPath
Write-Host "[OK] Updated WIMISODriverInjector.csproj" -ForegroundColor Green

# Update app.manifest (convert semantic versioning to assembly format)
$assemblyVersion = "$currentVersion.0"
$manifestPath = "app.manifest"
$manifestContent = Get-Content $manifestPath -Raw
$manifestContent = $manifestContent -replace '(<assemblyIdentity[^>]*\sversion=")[^"]*(")', "`${1}$assemblyVersion`${2}"
$manifestContent | Set-Content $manifestPath
Write-Host "[OK] Updated app.manifest (version: $assemblyVersion)" -ForegroundColor Green

# Update installer.iss
$issPath = "installer.iss"
$issContent = Get-Content $issPath -Raw
$issContent = $issContent -replace '#define MyAppVersion "[^"]*"', "#define MyAppVersion `"$currentVersion`""
$issContent | Set-Content $issPath
Write-Host "[OK] Updated installer.iss" -ForegroundColor Green

# Update build-package.ps1 default parameter
$buildScriptPath = "build-package.ps1"
$buildScriptContent = Get-Content $buildScriptPath -Raw
$buildScriptContent = $buildScriptContent -replace '\[string\]\$Version = "[^"]*"', "[string]`$Version = `"$currentVersion`""
$buildScriptContent | Set-Content $buildScriptPath
Write-Host "[OK] Updated build-package.ps1" -ForegroundColor Green

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Version Sync Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Current version: $currentVersion" -ForegroundColor White
if ($GitHubTag) {
    Write-Host "GitHub tag: $githubTag" -ForegroundColor White
}
Write-Host ""
Write-Host "All project files have been updated." -ForegroundColor Yellow
Write-Host ""
