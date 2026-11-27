#ifndef AppVersion
  #error AppVersion must be supplied by build.ps1.
#endif
#ifndef AppFileVersion
  #error AppFileVersion must be supplied by build.ps1.
#endif
#if Ver != EncodeVer(6, 7, 3)
  #error Use Inno Setup 6.7.3 to match the release build compiler.
#endif
#define RepoRoot SourcePath + "..\"

[Setup]
AppId={{29D7D90B-4F22-4F2E-AF74-476F234D7E42}
AppName=Connected Display Input Selector
AppVersion={#AppVersion}
AppPublisher=Jeffrey Strasser
AppPublisherURL=https://github.com/jeffstrasser/odyssey-input-switcher
AppSupportURL=https://github.com/jeffstrasser/odyssey-input-switcher/issues
AppUpdatesURL=https://github.com/jeffstrasser/odyssey-input-switcher/releases
DefaultDirName={localappdata}\Programs\Odyssey Input Switcher
DefaultGroupName=Connected Display Input Selector
UsePreviousGroup=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0.17763
OutputDir={#RepoRoot}dist
OutputBaseFilename=OdysseyInputSwitcher-Setup-{#AppVersion}
SetupIconFile={#RepoRoot}assets\monitor.ico
UninstallDisplayIcon={app}\OdysseyInputSwitcher.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppFileVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#RepoRoot}dist\win-x64\OdysseyInputSwitcher.exe"; DestDir: "{app}"; Check: not IsArm64; Flags: ignoreversion
Source: "{#RepoRoot}dist\win-arm64\OdysseyInputSwitcher.exe"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion
Source: "{#RepoRoot}README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Connected Display Input Selector"; Filename: "{app}\OdysseyInputSwitcher.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Connected Display Input Selector"; Filename: "{app}\OdysseyInputSwitcher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{userprograms}\Odyssey Input Switcher\Odyssey Input Switcher.lnk"; Check: WasLegacyInstall
Type: files; Name: "{userdesktop}\Odyssey Input Switcher.lnk"; Check: WasLegacyInstall

[Run]
Filename: "{app}\OdysseyInputSwitcher.exe"; Description: "Launch Connected Display Input Selector"; Flags: nowait postinstall skipifsilent

[Code]
const
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupValue = 'OdysseyInputSwitcher';

var
  LegacyInstall: Boolean;

function WasLegacyInstall(): Boolean;
begin
  Result := LegacyInstall;
end;

function InitializeSetup(): Boolean;
var
  PreviousName: String;
begin
  LegacyInstall := RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{29D7D90B-4F22-4F2E-AF74-476F234D7E42}_is1',
    'DisplayName', PreviousName) and
    (CompareText(Copy(PreviousName, 1, Length('Odyssey Input Switcher')), 'Odyssey Input Switcher') = 0);
  if not WizardSilent then
    MsgBox('Close Connected Display Input Selector before installing or upgrading: use Quit in the tray menu, or Settings > Exit.' + #13#10 +
      'Installation preserves your saved settings and does not enable Windows startup.',
      mbInformation, MB_OK);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
  InstalledCommand: String;
begin
  if CurUninstallStep = usUninstall then begin
    InstalledCommand := '"' + ExpandConstant('{app}\OdysseyInputSwitcher.exe') + '" --tray';
    // Do not remove a startup entry that belongs to a portable copy elsewhere.
    if RegQueryStringValue(HKCU, StartupKey, StartupValue, Command) and
      (CompareText(Command, InstalledCommand) = 0) then begin
      if not RegDeleteValue(HKCU, StartupKey, StartupValue) then
        RaiseException('Could not remove the installed app''s Windows startup entry.');
    end;
  end;
end;
