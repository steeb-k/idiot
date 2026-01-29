# Quick Start Guide

Get up and running with WIM/ISO Driver Injector in 5 minutes!

## Prerequisites Check

✅ **Windows 10/11** or Windows PE  
✅ **Administrator privileges** (right-click → Run as Administrator)  
✅ **.NET 8.0 SDK** (for building from source)

## Option 1: Use Pre-built Executable

1. Download `WIMISODriverInjector.exe`
2. Right-click → Run as Administrator
3. Follow the GUI instructions below

## Option 2: Build from Source

### Quick Build (5 minutes)

1. **Install .NET 8.0 SDK**
   - Download: https://dotnet.microsoft.com/download/dotnet/8.0
   - Verify: Open PowerShell and run `dotnet --version`

2. **Build the project**
   ```powershell
   cd WIM-ISO-Driver-Injector
   dotnet restore
   dotnet build -c Release
   ```

3. **Create portable executable**
   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
   ```

4. **Find your executable**
   - Location: `WIMISODriverInjector\bin\Release\net8.0-windows\win-x64\publish\WIMISODriverInjector.exe`

## First Use: GUI Mode

1. **Launch** (as Administrator)
   ```
   WIMISODriverInjector.exe
   ```

2. **Select input file**
   - Click "Browse..." → Select your Windows ISO or WIM file

3. **Add drivers**
   - Click "Add Directory..." → Select folder with driver files (.inf)
   - Repeat for multiple driver folders

4. **Start processing**
   - Click "Start Processing"
   - Wait for completion (may take 30+ minutes for large files)

5. **Check results**
   - Review log file (default: `injection-log.txt`)
   - Verify output file was created

## First Use: CLI Mode

### Basic Command

```powershell
# Run as Administrator
WIMISODriverInjector.exe -i "Windows.iso" -o "Windows_injected.iso" -d "C:\Drivers"
```

### What You Need

- **Input**: ISO or WIM file (Windows installer)
- **Output**: Path for the new file
- **Drivers**: Folder(s) containing `.inf` driver files

### Example

```powershell
WIMISODriverInjector.exe `
    --input "C:\ISOs\Windows11.iso" `
    --output "C:\ISOs\Windows11_with_drivers.iso" `
    --drivers "C:\Drivers\Network" "C:\Drivers\Storage"
```

## Common First-Time Issues

### "Access Denied"
**Fix**: Run as Administrator

### "DISM not found"
**Fix**: DISM is built into Windows 10/11. If missing, you may need Windows ADK.

### "No drivers found"
**Fix**: Ensure driver folders contain `.inf` files (not just `.sys` or `.dll`)

### "ISO creation failed" (Windows 10 older than 1803)
**Fix**: Install Windows ADK or upgrade to Windows 10 1803+

## Next Steps

- 📖 Read [README.md](README.md) for full documentation
- 📘 See [USAGE.md](USAGE.md) for detailed usage examples
- 🔧 Check [BUILD.md](BUILD.md) for advanced build options

## Getting Help

1. **Check the log file** - Most issues are explained there
2. **Review error messages** - They usually indicate the problem
3. **Verify prerequisites** - Administrator rights, DISM availability, etc.

## Tips for Success

✅ **Test first**: Try with a small WIM file or test ISO  
✅ **Backup originals**: Always keep copies of your original files  
✅ **Check drivers**: Verify drivers are compatible with your Windows version  
✅ **Be patient**: Large files (10GB+) can take 30-60 minutes  
✅ **Monitor disk space**: Ensure 2-3x file size is available

---

**Ready to go!** Start with a test file to get familiar with the tool.
