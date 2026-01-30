# Build Instructions

## Prerequisites

### Required Software

1. **.NET 8.0 SDK** or later
   - Download from: https://dotnet.microsoft.com/download/dotnet/8.0
   - Verify installation: `dotnet --version`

2. **Visual Studio 2022** (recommended) or **Visual Studio Code**
   - Visual Studio 2022 Community Edition is free
   - Or use VS Code with C# extension

### Optional Tools

- **Git** (for cloning the repository)
- **Windows SDK** (usually included with Visual Studio)

## Building from Source

### Method 1: Visual Studio

1. **Open the solution**
   - Launch Visual Studio 2022
   - File → Open → Project/Solution
   - Select `solution\WIM-ISO-Driver-Injector.sln` (or open the repo root and open the project)

2. **Restore packages**
   - Right-click solution → Restore NuGet Packages
   - Or: Build → Restore NuGet Packages

3. **Build**
   - Build → Build Solution (Ctrl+Shift+B)
   - Or: Right-click solution → Build

4. **Output location**
   - Debug: `bin\Debug\net8.0-windows10.0.19041.0\`
   - Release: `bin\Release\net8.0-windows10.0.19041.0\`
   - The built executable is **idiot.exe** (not WIMISODriverInjector).

### Method 2: Command Line (dotnet CLI)

1. **Navigate to repository root** (the folder containing `WIMISODriverInjector.csproj`)
   ```powershell
   cd path\to\idiot
   ```

2. **Restore dependencies**
   ```powershell
   dotnet restore
   ```

3. **Build**
   ```powershell
   dotnet build -c Release
   ```
   From root there is only one project, so `dotnet build` works without specifying a file. To build the solution: `dotnet build solution\WIM-ISO-Driver-Injector.sln -c Release`

4. **Output location**
   - `bin\Release\net8.0-windows10.0.19041.0\`
   - The built executable is **idiot.exe**.

## Release Builds: Portable ZIP and MSI Installer

To build both a portable ZIP and an MSI installer (executable name **idiot.exe** in both):

1. **Prerequisites**: .NET 8 SDK; WiX Toolset SDK (restored via NuGet when building the installer).
2. **Run the release script** from the repo root:
   ```powershell
   .\build-release.ps1
   ```
3. **Outputs** (version read from `WIMISODriverInjector.csproj`):
   - **Portable**: `release\idiot-portable-<version>.zip` — unzip and run `idiot.exe` (framework-dependent; requires .NET 8 runtime on the machine).
   - **MSI**: `release\idiot-<version>.msi` — installs to Program Files; run **idiot.exe** from the Start Menu or install folder.

To build only the MSI after a prior publish, ensure `publish\portable` exists (e.g. run the script once), then:
```powershell
dotnet build installer\IdiotInstaller.wixproj -c Release -p:AppPublishPath="$((Resolve-Path publish\portable).Path)"
```

## Creating Portable Executable

### Self-Contained Single File

To create a portable, self-contained executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true
```

**Output location**: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`

### Bundling oscdimg for ISO creation

Creating output ISO files requires **oscdimg.exe**. To ship the app without requiring a separate Windows ADK install:

1. Obtain **oscdimg.exe** from the Windows ADK (Assessment and Deployment Kit), e.g. from:
   - `Program Files\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe`
2. Place **oscdimg.exe** in `Tools\` (or next to the published .exe in a `Tools` subfolder).
3. Rebuild/publish; the exe will be copied to the output when present.

See `Tools\README.md` for details.

### Options Explained

- `-c Release`: Release configuration (optimized)
- `-r win-x64`: Target Windows x64 runtime
- `--self-contained true`: Include .NET runtime
- `-p:PublishSingleFile=true`: Create single executable
- `-p:IncludeNativeLibrariesForSelfExtract=true`: Include native libraries
- `-p:PublishReadyToRun=true`: Pre-compile for faster startup

### Other Runtime Identifiers

For different architectures:

```powershell
# Windows x86 (32-bit)
dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true

# Windows ARM64
dotnet publish -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true
```

## Build Configurations

### Debug Configuration

- Includes debug symbols
- Not optimized
- Larger file size
- Better for development and debugging

```powershell
dotnet build -c Debug
```

### Release Configuration

- Optimized code
- Smaller file size
- Better performance
- Recommended for distribution

```powershell
dotnet build -c Release
```

## Project Structure

```
idiot/   (or your repo root)
├── solution\
│   └── WIM-ISO-Driver-Injector.sln      # Solution file (for Visual Studio)
├── WIMISODriverInjector.csproj          # Project file (dotnet build from root)
├── App.xaml / App.xaml.cs                # Application definition
├── MainWindow.xaml / MainWindow.xaml.cs  # GUI window
├── app.manifest                          # Application manifest
├── Core/                                 # Core processing, logging, cleanup
├── CLI/                                  # CLI entry point
├── Themes/                               # Theme resources
├── Tools/                                # oscdimg, boot files for ISO
├── README.md
├── USAGE.md
└── BUILD.md
```

## Dependencies

### NuGet Packages

- **System.CommandLine** (v2.0.0-beta4.22272.1)
  - Used for CLI argument parsing
  - Automatically restored during build

### Framework Dependencies

- **.NET 8.0 Windows Runtime**
- **WPF** (Windows Presentation Foundation)
- **System.Windows.Forms** (for FolderBrowserDialog)

## Troubleshooting Build Issues

### Issue: "SDK not found"

**Solution**: Install .NET 8.0 SDK
```powershell
# Verify installation
dotnet --version

# Should show 8.0.x or higher
```

### Issue: "NuGet packages not restored"

**Solution**: Restore packages manually
```powershell
dotnet restore
```

### Issue: "Cannot find System.CommandLine"

**Solution**: The package should restore automatically. If not:
```powershell
dotnet add WIMISODriverInjector.csproj package System.CommandLine
```

### Issue: "WPF not available"

**Solution**: Ensure you're targeting a Windows framework (not just `net8.0`)
- Check `TargetFramework` in `.csproj` file
- Should be: `<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>` (or similar WinUI target)

### Issue: "Administrator manifest error"

**Solution**: The app requires administrator privileges for DISM operations
- `app.manifest` is configured correctly
- Ensure it's included in the project

## Verification

After building, verify the executable:

1. **Check file exists**
   ```powershell
   Test-Path "bin\Release\net8.0-windows10.0.19041.0\idiot.exe"
   ```

2. **Test CLI help**
   ```powershell
   .\bin\Release\net8.0-windows10.0.19041.0\idiot.exe --help
   ```

3. **Test GUI launch**
   ```powershell
   .\bin\Release\net8.0-windows10.0.19041.0\idiot.exe
   # Should open GUI window
   ```

## Distribution

### What to Include

When distributing the application:

1. **Single executable** (if published as single file)
   - `idiot.exe`
   - No other files needed

2. **Or application folder** (if not single file)
   - `idiot.exe`
   - All DLL dependencies
   - Runtime files

### Size Expectations

- **Debug build**: ~50-100 MB
- **Release build**: ~30-50 MB
- **Self-contained single file**: ~60-80 MB

### Testing Before Distribution

1. Test on clean Windows 10/11 system
2. Test in Windows PE environment
3. Verify both GUI and CLI modes work
4. Test with sample ISO/WIM files
5. Verify logging works correctly

## Continuous Integration

### GitHub Actions Example

```yaml
name: Build

on: [push, pull_request]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v3
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '8.0.x'
      - name: Restore dependencies
        run: dotnet restore
      - name: Build
        run: dotnet build -c Release
      - name: Publish
        run: dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Advanced Build Options

### Trimming (Not Recommended)

Trimming can reduce size but may break WPF:
```powershell
# NOT recommended - may cause issues
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true
```

### Native AOT (Future)

When .NET supports Native AOT for WPF:
```powershell
# Future option
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishAot=true
```

## Version Information

Update version in `WIMISODriverInjector.csproj` (used by the app and by `build-release.ps1` for ZIP/MSI names):

```xml
<Version>1.0.0</Version>
```

This version appears in:
- File properties
- Application information
- Help text
