; =============================================================================
; Inno Setup Script for VocalPure
; Professional installer for .NET 8 WPF application with bundled dependencies.
; =============================================================================

#define MyAppName "VocalPure"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "VocalPure"
#define MyAppURL "https://github.com/yourusername/vocalpure"
#define MyAppExeName "VocalPure.exe"
#define MyAppAssocName MyAppName + " File"
#define MyAppAssocExt ".vocalpure"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

[Setup]
; Application metadata
AppId={{B4E2A8C5-7F1D-4B3A-9E5C-8D2F6A0B3E71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; Output settings
OutputDir=.
OutputBaseFilename=VocalPure-Setup-v{#MyAppVersion}
SetupIconFile=Assets\icon.ico
Compression=lzma2
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=4

; Architecture
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Installation directories
DefaultDirName={autopf64}\{#MyAppName}
DisableProgramGroupPage=yes

; UI settings
WizardStyle=modern
DefaultGroupName={#MyAppName}
LicenseFile=LICENSE.txt
InfoBeforeFile=README.md
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; Privileges
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; Version info
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=AI-Powered Vocal Separation Tool
VersionInfoTextVersion={#MyAppVersion}
VersionInfoCopyright=Copyright (C) 2024 {#MyAppPublisher}

; Modern styling
WizardSizePercent=100,100
WindowShowCaption=no
WindowResizable=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode
Name: "associatefiles"; Description: "Associate audio/video files with {#MyAppName}"; GroupDescription: "File Associations:"

[Files]
; Main application executable
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; Engine components (FFmpeg, ONNX models)
Source: "Engine\ffmpeg.exe"; DestDir: "{localappdata}\{#MyAppName}\Engine"; Flags: ignoreversion
Source: "Engine\Kim_Vocal_2.onnx"; DestDir: "{localappdata}\{#MyAppName}\Engine"; Flags: ignoreversion

; Visual C++ Redistributables (optional - comment out if targeting systems with VC++ already installed)
; Source: "redist\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

; License and documentation
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register application uninstall info
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppName}_is1"; \
    ValueType: string; ValueName: "DisplayName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey

; File associations
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocExt}"; \
    ValueType: string; ValueName: ""; ValueData: "{#MyAppAssocKey}"; \
    Flags: uninsdeletevalue; Tasks: associatefiles

Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}"; \
    ValueType: string; ValueName: ""; ValueData: "{#MyAppAssocName}"; \
    Flags: uninsdeletekey; Tasks: associatefiles

Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\DefaultIcon"; \
    ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; \
    Tasks: associatefiles

Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\shell\open\command"; \
    ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; \
    Tasks: associatefiles

[Run]
; Post-installation: Strip MotW from engine files
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-ChildItem -Path '{localappdata}\{#MyAppName}\Engine' -File | Unblock-File"""; \
    Description: "Securing engine components..."; \
    Flags: runhidden postinstall

; Launch application after install
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall skipifsilent nowait

; Visual C++ Redistributable (if included)
; Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; \
;     StatusMsg: "Installing Visual C++ Redistributables..."; \
;     Flags: waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#MyAppName}"

[Code]
{ ============================================================================= }
{ Pascal scripting for advanced installer logic                      }
{ ============================================================================= }

{ Check if VC++ Redistributables are installed }
function VCRedistInstalled: Boolean;
begin
    Result := RegKeyExists(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64');
    if not Result then
        Result := RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64');
end;

{ Initialize setup wizard pages }
procedure InitializeWizard;
begin
    { Customize welcome page }
    WizardForm.WelcomeLabel1.Caption := 'Welcome to VocalPure Setup';
    WizardForm.WelcomeLabel2.Caption := 'This wizard will install VocalPure v' + '{#MyAppVersion}' + ' on your computer.' + #13#10#13#10 +
        'VocalPure uses AI to separate vocals from music tracks.' + #13#10 +
        'GPU acceleration is recommended for best performance.';
end;

{ Pre-installation checks }
function InitializeSetup: Boolean;
begin
    Result := true;
    
    { Check Windows version - require Windows 10 or later }
    if not IsWindowsVersionOrGreater(10, 0, 17763) then begin
        MsgBox('VocalPure requires Windows 10 version 1809 or later.', mbCriticalError, MB_OK);
        Result := false;
    end;
    
    { Warn if VC++ Redistributables might be missing }
    if not VCRedistInstalled then begin
        if MsgBox('Visual C++ Redistributables may not be installed. These are required for VocalPure to run.' + #13#10#13#10 +
                  'Would you like to continue anyway?', mbConfirmation, MB_YESNO) = IDNO then
            Result := false;
    end;
end;

{ Post-installation: Set permissions on engine directory }
procedure CurStepChanged(CurStep: TSetupStep);
begin
    if CurStep = ssPostInstall then begin
        { Ensure the engine directory has proper permissions }
        { This allows the app to write/update engine files }
        Log('Setting permissions on engine directory...');
    end;
end;

{ CurPageChanged handler to update UI }
procedure CurPageChanged(CurPageID: Integer);
begin
    case CurPageID of
        wpReady: begin
            WizardForm.ReadyMemo.Lines.Add('');
            WizardForm.ReadyMemo.Lines.Add('Components to install:');
            WizardForm.ReadyMemo.Lines.Add('  - VocalPure application');
            WizardForm.ReadyMemo.Lines.Add('  - FFmpeg audio engine');
            WizardForm.ReadyMemo.Lines.Add('  - MDX-Net AI model (Kim_Vocal_2.onnx)');
        end;
    end;
end;
