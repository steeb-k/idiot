# Usage Guide

## Quick Start

### GUI Mode

1. **Launch the application**
   - Double-click `WIMISODriverInjector.exe`
   - The application will start in GUI mode

2. **Select input file**
   - Click "Browse..." next to "Input File"
   - Select your Windows installer ISO or WIM file
   - The output filename will be auto-suggested

3. **Add driver directories**
   - Click "Add Directory..." under "Driver Directories"
   - Select folders containing your driver files (.inf)
   - You can add multiple directories
   - Remove directories by selecting them and clicking "Remove Selected"

4. **Configure options** (optional)
   - Check/uncheck "Optimize and shrink WIM file" (enabled by default)
   - Change log file location if needed

5. **Start processing**
   - Click "Start Processing"
   - Monitor progress in the "Log Output" area
   - Wait for completion (this may take 30+ minutes for large files)

6. **Review results**
   - Check the log file for detailed information
   - Verify the output file was created successfully

### CLI Mode

#### Basic Command

```powershell
WIMISODriverInjector.exe --input "input.iso" --output "output.iso" --drivers "C:\Drivers"
```

#### Advanced Example

```powershell
WIMISODriverInjector.exe `
    --input "C:\ISOs\Windows11.iso" `
    --output "C:\ISOs\Windows11_with_drivers.iso" `
    --drivers "C:\Drivers\Network" "C:\Drivers\Storage" "C:\Drivers\Chipset" `
    --log "C:\Logs\injection.log" `
    --optimize
```

## Detailed Workflow

### Step-by-Step: Processing an ISO

1. **Prepare your drivers**
   - Organize drivers into folders (one folder per driver type is recommended)
   - Ensure all drivers are Windows-compatible
   - Drivers should be in `.inf` format

2. **Run the application**
   - Launch `WIMISODriverInjector.exe`
   - Select your Windows installer ISO
   - Add all driver directories
   - Click "Start Processing"

3. **What happens during processing:**
   - ISO is extracted to a temporary location
   - WIM files are located (typically `sources\install.wim`)
   - Each WIM image index is processed:
     - Image is mounted
     - Drivers are injected one by one
     - Changes are committed
   - WIM files are optimized (if enabled)
   - New ISO is created

4. **After processing:**
   - Review the log file for any failed drivers
   - Test the new ISO in a virtual machine
   - Burn or use the ISO as needed

### Step-by-Step: Processing a WIM

1. **Prepare your drivers** (same as ISO)

2. **Run the application**
   - Select your WIM file as input
   - Specify output WIM path
   - Add driver directories
   - Start processing

3. **What happens:**
   - WIM file indexes are identified
   - Each index is mounted and processed
   - Drivers are injected
   - WIM is optimized and saved

## Command-Line Reference

### Required Arguments

- `--input` / `-i`: Input ISO or WIM file path
- `--output` / `-o`: Output file path
- `--drivers` / `-d`: One or more driver directory paths

### Optional Arguments

- `--log` / `-l`: Log file path (default: `injection-log.txt`)
- `--optimize` / `--shrink`: Enable WIM optimization (default: true)
- `--no-optimize`: Disable WIM optimization

### Examples

**Minimal command:**
```powershell
WIMISODriverInjector.exe -i install.wim -o install_new.wim -d C:\Drivers
```

**Multiple driver directories:**
```powershell
WIMISODriverInjector.exe -i Windows.iso -o Windows_new.iso -d C:\Drivers\Net C:\Drivers\Storage
```

**Custom log location:**
```powershell
WIMISODriverInjector.exe -i install.wim -o install_new.wim -d C:\Drivers -l "C:\Logs\my-log.txt"
```

**Disable optimization:**
```powershell
WIMISODriverInjector.exe -i install.wim -o install_new.wim -d C:\Drivers --no-optimize
```

## Understanding the Log File

### Log Entry Format

```
[2026-01-25 14:30:15] [INFO] Processing ISO: C:\Windows.iso
[2026-01-25 14:30:20] [INFO] Found 1 WIM file(s) in ISO
[2026-01-25 14:30:25] [INFO] Processing image index 1: Windows 11 Pro
[2026-01-25 14:30:30] [INFO] Injecting driver: network_driver.inf
[2026-01-25 14:30:35] [SUCCESS] Successfully injected: network_driver.inf
[2026-01-25 14:30:40] [DRIVER_FAILED] Driver: C:\Drivers\old_driver.inf - Reason: Driver not compatible
[2026-01-25 14:35:00] [SUCCESS] WIM processing completed successfully
```

### Log Levels

- **INFO**: General information about operations
- **SUCCESS**: Successful operations
- **WARNING**: Non-critical issues
- **ERROR**: Critical errors that may affect processing
- **DRIVER_FAILED**: Specific driver injection failures

### What to Look For

- **DRIVER_FAILED entries**: Review these to see which drivers couldn't be injected and why
- **ERROR entries**: These indicate serious problems that may have stopped processing
- **Processing times**: Large files take time - be patient

## Best Practices

### Driver Organization

- **One folder per driver type**: Organize drivers by category (Network, Storage, Chipset, etc.)
- **Use descriptive names**: Name folders clearly (e.g., "Intel_Network_Drivers_v1.2")
- **Keep drivers updated**: Use the latest compatible drivers
- **Test drivers first**: Verify drivers work before injecting

### File Management

- **Keep originals**: Always keep a backup of your original ISO/WIM files
- **Test before deployment**: Test modified images in a VM first
- **Verify checksums**: Consider verifying file integrity after processing
- **Sufficient space**: Ensure 2-3x the file size is available for temporary files

### Performance Optimization

- **SSD storage**: Use SSD for faster processing
- **Close other applications**: Free up system resources
- **Process during off-hours**: Large files can take 30+ minutes
- **Monitor disk space**: Ensure adequate free space

## Troubleshooting Common Scenarios

### Scenario 1: "Access Denied" Error

**Problem**: Cannot access files or mount WIM

**Solutions**:
- Run as Administrator
- Check file permissions
- Ensure files aren't open in other applications
- Verify antivirus isn't blocking operations

### Scenario 2: Drivers Not Injecting

**Problem**: Drivers show as failed in log

**Solutions**:
- Verify driver compatibility with Windows version
- Check that .inf files are valid
- Ensure drivers are for the correct architecture (x64/x86)
- Review log file for specific error messages

### Scenario 3: ISO Creation Fails

**Problem**: Cannot create output ISO

**Solutions**:
- Ensure Windows 10 1803+ or install Windows ADK
- Check disk space availability
- Verify output path is writable
- Try a different output location

### Scenario 4: Processing Takes Too Long

**Problem**: Processing seems stuck

**Solutions**:
- Large files (10GB+) can take 30-60 minutes
- Check Task Manager for DISM activity
- Monitor disk I/O - high activity is normal
- Review log file for progress updates

## Automation Examples

### PowerShell Script

```powershell
# Process multiple ISOs
$isos = Get-ChildItem "C:\ISOs\*.iso"
$driverPath = "C:\Drivers"

foreach ($iso in $isos) {
    $output = $iso.FullName -replace "\.iso$", "_injected.iso"
    $log = $iso.FullName -replace "\.iso$", "_log.txt"
    
    & ".\WIMISODriverInjector.exe" `
        -i $iso.FullName `
        -o $output `
        -d $driverPath `
        -l $log
    
    Write-Host "Processed: $($iso.Name)"
}
```

### Batch Script

```batch
@echo off
set INPUT=C:\Windows.iso
set OUTPUT=C:\Windows_injected.iso
set DRIVERS=C:\Drivers
set LOG=C:\Logs\injection.log

WIMISODriverInjector.exe -i "%INPUT%" -o "%OUTPUT%" -d "%DRIVERS%" -l "%LOG%"

if %ERRORLEVEL% EQU 0 (
    echo Processing completed successfully!
) else (
    echo Processing failed. Check log file.
)
```

## Advanced Usage

### Processing Specific Image Indexes

Currently, the tool processes all image indexes in a WIM file. To process only specific indexes, you would need to:
1. Extract the WIM file
2. Process only the desired index
3. Recreate the WIM with only that index

### Driver Injection Order

Drivers are injected in the order they're found in the directories. For dependencies:
- Ensure base drivers are in directories listed first
- Or organize drivers in dependency order within folders

### Integration with Deployment Tools

The tool can be integrated into deployment pipelines:
- Use CLI mode for automation
- Parse log files for success/failure reporting
- Integrate with MDT, SCCM, or other deployment solutions

## Support and Resources

- **Log Files**: Always check log files first for detailed error information
- **Windows DISM Documentation**: Understanding DISM can help troubleshoot issues
- **Driver Compatibility**: Verify drivers are compatible with your Windows version
- **Community**: Check GitHub issues for similar problems and solutions
