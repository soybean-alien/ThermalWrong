; ThermalWrong by soybean_alien
; https://github.com/soybean-alien/ThermalWrong · MIT License
;
; Build with build-installer.ps1 (it publishes the app first), or:
;   ISCC.exe installer\ThermalWrong.iss   after publishing to artifacts\publish

#define AppName "ThermalWrong"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "ThermalWrong.exe"
#define AppUrl "https://github.com/soybean-alien/ThermalWrong"

[Setup]
AppId={{6E0B7C1A-3F2D-4C55-9A0E-7D1B2F8C4A91}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=soybean_alien
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppComments=Unofficial project. Not affiliated with Thermalright.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\assets\ThermalWrong.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=ThermalWrong-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
VersionInfoVersion={#AppVersion}
VersionInfoCompany=soybean_alien
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start ThermalWrong when I sign in (it takes the screen over from TRCC's own startup)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\samples\*"; DestDir: "{app}\samples"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The installer is elevated, so registering the elevated logon task needs no extra prompt.
Filename: "{app}\{#AppExe}"; Parameters: "--install-autostart"; Tasks: autostart; Flags: runhidden waituntilterminated; StatusMsg: "Setting up start with Windows..."
; Launched from the elevated installer, so it can take the screen from TRCC without another prompt.
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Removes our logon task and, if we had turned TRCC's startup off, turns it back on.
Filename: "{app}\{#AppExe}"; Parameters: "--remove-autostart"; RunOnceId: "RemoveAutostart"; Flags: runhidden waituntilterminated

[Code]
function TrccInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\ThermalRight-TRCC\TRCCCAP\TRCC.exe'));
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not TrccInstalled then
    Result := MsgBox('Thermalright''s TRCC does not seem to be installed.' + #13#10#13#10 +
      'ThermalWrong uses the USB driver, sensor helper and ffmpeg that come with TRCC, so install TRCC first.' + #13#10#13#10 +
      'Continue anyway?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure StopApp;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopApp;
  Result := True;
end;
