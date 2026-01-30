; Inno Setup Script for I.D.I.O.T. (Image Driver Integration & Optimization Tool)
; Requires Inno Setup 6.0 or later: https://jrsoftware.org/isinfo.php

#define MyAppName "I.D.I.O.T."
#define MyAppFullName "Image Driver Integration & Optimization Tool"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "KZNJK"
#define MyAppURL "https://github.com/steeb-k/idiot"
#define MyAppExeName "idiot.exe"
#define MyAppId "{{8F7A3C5D-9B2E-4F1A-A8D6-3E5C7B9A1F2D}"

[Setup]
; NOTE: The value of AppId uniquely identifies this application.
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputBaseFilename=idiot-v{#MyAppVersion}-win-x64-installer
SetupIconFile=idiotLogo.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
DisableProgramGroupPage=yes
; Minimum Windows 10 version 1809 (build 17763)
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Include all files from the published output
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
; Start Menu shortcuts
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
; Desktop shortcut (optional, based on user selection)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel2.Caption := 
    'This will install {#MyAppFullName} on your computer.' + #13#10 + #13#10 +
    'I.D.I.O.T. is a portable tool for injecting drivers into Windows ' +
    'installer ISOs and WIM files.' + #13#10 + #13#10 +
    'Click Next to continue, or Cancel to exit Setup.';
end;
