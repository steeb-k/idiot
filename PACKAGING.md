# Packaging Guide for I.D.I.O.T.

## Building Portable and Installer Packages

### Quick Start

To build both portable and installer packages:

```powershell
.\build-package.ps1
```

This will create:
- `publish\win-x64\` - Published application files
- `publish\idiot-v1.0.0-win-x64-portable.zip` - Portable ZIP package (~77 MB)
- `publish\idiot-v1.0.0-win-x64-installer.exe` - Windows installer (~35 MB)

**Note:** The installer requires [Inno Setup 6](https://jrsoftware.org/isdl.php) to be installed. If not available, the script will skip installer creation and only build the portable ZIP.

### Quick Start (Portable Only)

To build only the portable package without the installer:

```powershell
.\build-package.ps1 -SkipInstaller
```

### Custom Build Options

```powershell
# Build with custom version
.\build-package.ps1 -Version "1.0.1"

# Build for different configuration
.\build-package.ps1 -Configuration Debug

# Build without installer
.\build-package.ps1 -SkipInstaller

# Combine options
.\build-package.ps1 -Version "1.0.1" -Configuration Release
```

### Prerequisites for Installer

To build the installer, you need:
- **Inno Setup 6** or later: [Download here](https://jrsoftware.org/isdl.php)
- Default installation path: `C:\Program Files (x86)\Inno Setup 6\`

If Inno Setup is not installed, the build script will only create the portable ZIP package.

### What's Included

**Portable Package:**
- ? Self-contained .NET 8 runtime (no installation required)
- ? All WinUI 3 / Windows App SDK dependencies
- ? XAML resources (via `idiot.pri`)
- ? Application icon
- ? External tools (e.g., `oscdimg.exe`)
- ?? Extract anywhere and run directly

**Installer Package:**
- ? Same self-contained runtime as portable
- ? Installs to `C:\Program Files\I.D.I.O.T\`
- ? Start Menu shortcuts
- ? Optional Desktop shortcut
- ? Windows Uninstaller integration
- ? Dark mode support (automatically detects Windows theme)
- ? Requires Windows 10 version 1809 (build 17763) or later

### Key Configuration

The following settings in `WIMISODriverInjector.csproj` enable self-contained packaging:

```xml
<SelfContained>true</SelfContained>
<PublishTrimmed>false</PublishTrimmed>
<PublishReadyToRun>true</PublishReadyToRun>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<EnableMsixTooling>true</EnableMsixTooling>
<AppxPackage>false</AppxPackage>
```

**Critical for WinUI 3:**
- `WindowsAppSDKSelfContained=true` - Includes all Windows App SDK runtime components
- `EnableMsixTooling=true` - Generates the `.pri` resource file needed for XAML
- `PublishTrimmed=false` - Prevents XAML binding issues

### Distribution

**Portable ZIP:**
- Extract to any folder
- Run `idiot.exe` directly
- No installation or .NET runtime required
- Can run from USB drive or any location

**Installer EXE:**
- Double-click to run setup wizard
- Installs to Program Files
- Creates Start Menu shortcuts
- Optional Desktop shortcut
- Includes uninstaller in Windows Settings

### Creating an Installer (Advanced)

The installer is built using Inno Setup with the `installer.iss` script. Key features:

**Installation:**
- Default location: `C:\Program Files\I.D.I.O.T\`
- Requires administrator privileges (for installation only)
- Creates Start Menu program group
- Optional desktop icon
- Launches app in normal user context (not elevated)

**Shortcuts:**
- Start Menu: I.D.I.O.T.
- Start Menu: Uninstall I.D.I.O.T.
- Desktop (optional): I.D.I.O.T.

**Customization:**
Edit `installer.iss` to modify:
- Installation directory
- Shortcut names
- License file
- Setup wizard appearance
- Version information

To manually build the installer:
```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
```

### Troubleshooting

**XAML parsing errors:**
- Ensure `idiot.pri` file is present in the publish output
- Verify `EnableMsixTooling=true` in the `.csproj`

**Missing dependencies:**
- Check that `WindowsAppSDKSelfContained=true` is set
- Verify all `.dll` files are included in the output

**File not found errors:**
- Ensure tools like `oscdimg.exe` are copied to output
- Check `CopyToOutputDirectory` settings in `.csproj`

**"File is being used by another process" when creating ZIP:**
- This is a timing issue with Windows file handles
- The build script automatically retries up to 3 times
- If it persists, close antivirus/file explorers and try again
- Or run the build script again - it usually succeeds on retry

### Package Size

The portable package is approximately 77 MB, which includes:
- .NET 8 runtime
- WinUI 3 / Windows App SDK
- All application dependencies

This is normal for a self-contained WinUI 3 application.
