# Enhanced WIM/ISO Modification Tool

Building configuration profiles with essential registry bypass functionality and custom WIM location support for advanced deployment scenarios.

## Steps
1. **Create configuration profile system** - Add JSON-based profile management in new [Core/ProfileManager.cs](Core/ProfileManager.cs) for saving driver sets, registry modifications, and deployment templates
2. **Implement registry modification framework** - Build registry injection system using DISM `/Load-Registry` and `/Unload-Registry` in [Core/RegistryProcessor.cs](Core/RegistryProcessor.cs) with safe offline registry editing
3. **Add Microsoft bypass functionality** - Implement essential registry tweaks including BypassNRO (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1`) and similar Microsoft requirement bypasses
4. **Support custom WIM locations** - Extend [Core/ImageProcessor.cs](Core/ImageProcessor.cs) to handle non-standard WIM paths (boot.wim/install.wim in custom folders instead of x:\sources\) for multiboot scenarios
5. **Create deployment scenario templates** - Add preset profiles for common deployment patterns with configurable driver sets and registry modifications
6. **Add profile import/export** - Implement profile sharing capabilities with validation to allow standardized deployment configurations

## Further Considerations
1. **Custom WIM paths** - App should auto-detect WIM files in non-standard locations but allow user to override detected paths
2. **Windows 11 specific tweaks** - Registry modifications should only apply to install.wim files containing Windows 11 images, detected automatically
3. **Profile validation** - Profiles should validate Windows version compatibility and only offer applicable registry modifications

## Essential Registry Modifications

### Microsoft Requirement Bypasses (Windows 11 Only)
- **BypassNRO**: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1` - Bypass network requirement during OOBE (equivalent to bypassnro.cmd)
- **BypassTPMCheck**: `HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1` - Bypass TPM 2.0 requirement for Windows 11 installation
- **BypassSecureBootCheck**: `HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1` - Bypass Secure Boot requirement for Windows 11 installation

### Version Detection and Application
- **Windows 11 detection**: Automatically detect Windows 11 images in install.wim and only apply Windows 11 specific bypasses
- **Local account support**: Registry modifications to ensure local account creation remains available during OOBE
- **Selective application**: Only apply relevant registry modifications based on detected Windows version

### Custom Registry Framework
- **Template system**: Allow users to define custom registry modifications through profile templates
- **Version-aware tweaks**: Extensible system that respects Windows version compatibility
- **Safe defaults**: Focus on non-destructive modifications for local account access and requirement bypasses
