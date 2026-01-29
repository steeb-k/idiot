# Script to check current DISM mount status
# Run this in an elevated PowerShell window (Run as Administrator)

Write-Host "=== Checking DISM Mount Status ===" -ForegroundColor Cyan
Write-Host ""

# Check for mounted WIM images
Write-Host "Checking for mounted WIM images..." -ForegroundColor Yellow
$dismOutput = dism.exe /Get-MountedWimInfo 2>&1

if ($LASTEXITCODE -eq 0) {
    Write-Host $dismOutput
} else {
    Write-Host "Error getting mount info: $dismOutput" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Checking for running DISM processes ===" -ForegroundColor Cyan
$dismProcesses = Get-Process -Name dism -ErrorAction SilentlyContinue

if ($dismProcesses) {
    Write-Host "Found $($dismProcesses.Count) DISM process(es) running:" -ForegroundColor Yellow
    foreach ($proc in $dismProcesses) {
        Write-Host "  PID: $($proc.Id) | CPU: $($proc.CPU) | Memory: $([math]::Round($proc.WorkingSet64/1MB, 2)) MB | Start Time: $($proc.StartTime)" -ForegroundColor Green
    }
} else {
    Write-Host "No DISM processes are currently running." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Checking specific mount directory ===" -ForegroundColor Cyan
$mountPath = "E:\WIMDriverInjector\7869b156-9120-4e6c-be63-bed04f5e924e\mount_6"

if (Test-Path $mountPath) {
    Write-Host "Mount directory exists: $mountPath" -ForegroundColor Green
    
    # Check if directory is accessible
    try {
        $dirInfo = Get-Item $mountPath -ErrorAction Stop
        Write-Host "  Directory is accessible" -ForegroundColor Green
        Write-Host "  Last Write Time: $($dirInfo.LastWriteTime)" -ForegroundColor Gray
        
        # Try to check if it's actually mounted by DISM
        $mountCheck = dism.exe /Get-MountedWimInfo 2>&1 | Select-String -Pattern "mount_6"
        if ($mountCheck) {
            Write-Host "  Status: MOUNTED (found in DISM mount list)" -ForegroundColor Yellow
            Write-Host $mountCheck -ForegroundColor Gray
        } else {
            Write-Host "  Status: Directory exists but NOT in DISM mount list (may be orphaned)" -ForegroundColor Red
        }
    } catch {
        Write-Host "  Error accessing directory: $_" -ForegroundColor Red
    }
} else {
    Write-Host "Mount directory does not exist: $mountPath" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Recommendations ===" -ForegroundColor Cyan
Write-Host "If mount_6 is stuck and DISM process is not running, you can try:" -ForegroundColor Yellow
Write-Host "  1. Force unmount: dism.exe /Unmount-Wim /MountDir:`"$mountPath`" /Discard" -ForegroundColor White
Write-Host "  2. If that fails, you may need to restart the computer" -ForegroundColor White
Write-Host "  3. After restart, manually delete: Remove-Item -Path `"$mountPath`" -Force -Recurse" -ForegroundColor White
