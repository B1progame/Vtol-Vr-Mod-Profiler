#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef PublisherName
  #define PublisherName "VTOLVR Workshop Tools"
#endif

#ifndef SourceDir
  #define SourceDir "publish\\win-x64"
#endif

#ifndef IconFile
  #define IconFile "src\\VTOLVRWorkshopProfileSwitcher\\Assets\\AppIcon.ico"
#endif

[Setup]
AppId={{6AB2D1C3-8D31-45E8-8B3F-AC5C8C1A7E12}
AppName=VTOL VR Switcher
AppVersion={#MyAppVersion}
AppPublisher={#PublisherName}
AppPublisherURL=https://github.com/
AppSupportURL=https://github.com/
AppUpdatesURL=https://github.com/
DefaultDirName={autopf}\VTOL VR Switcher
DefaultGroupName=VTOL VR Switcher
DisableProgramGroupPage=yes
OutputDir=installer\output
OutputBaseFilename=VTOLVRSwitcher-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\VTOLVRWorkshopProfileSwitcher.exe
SetupIconFile={#IconFile}
VersionInfoCompany={#PublisherName}
VersionInfoDescription=VTOL VR Switcher Installer
VersionInfoProductName=VTOL VR Switcher
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VTOL VR Switcher"; Filename: "{app}\VTOLVRWorkshopProfileSwitcher.exe"
Name: "{autodesktop}\VTOL VR Switcher"; Filename: "{app}\VTOLVRWorkshopProfileSwitcher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VTOLVRWorkshopProfileSwitcher.exe"; Description: "Launch VTOL VR Switcher"; Flags: nowait postinstall skipifsilent

[Code]
var
  AutoUpdatePage: TInputOptionWizardPage;
  RemoveUserData: Boolean;

procedure InitializeWizard;
begin
  AutoUpdatePage :=
    CreateInputOptionPage(
      wpSelectTasks,
      'Update Preferences',
      'Automatic updates',
      'Choose whether VTOL VR Switcher should auto-install updates when available.',
      True,
      False);

  AutoUpdatePage.Add('Enable automatic updates');
  AutoUpdatePage.Values[0] := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: string;
  SettingsPath: string;
  AutoInstallUpdatesValue: string;
  SettingsJson: string;
begin
  if CurStep <> ssInstall then
  begin
    exit;
  end;

  DataDir := ExpandConstant('{localappdata}\VTOLVR-WorkshopProfiles');
  if not DirExists(DataDir) then
  begin
    ForceDirectories(DataDir);
  end;

  SettingsPath := AddBackslash(DataDir) + 'settings.json';

  if FileExists(SettingsPath) then
  begin
    exit;
  end;

  if AutoUpdatePage.Values[0] then
  begin
    AutoInstallUpdatesValue := 'true';
  end
  else
  begin
    AutoInstallUpdatesValue := 'false';
  end;

  SettingsJson :=
    '{'#13#10 +
    '  "selectedDesign": "TACTICAL RED",'#13#10 +
    '  "openSteamPageAfterDelete": true,'#13#10 +
    '  "autoInstallUpdates": ' + AutoInstallUpdatesValue + ','#13#10 +
    '  "lastAutoInstallAttemptedTag": ""'#13#10 +
    '}'#13#10;

  SaveStringToFile(SettingsPath, SettingsJson, False);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveUserData :=
      MsgBox(
        'Also remove user data (profiles, backups, logs)?'#13#10#13#10 +
        'Path: ' + ExpandConstant('{localappdata}\VTOLVR-WorkshopProfiles'),
        mbConfirmation, MB_YESNO) = IDYES;

    if RemoveUserData then
    begin
      DataDir := ExpandConstant('{localappdata}\VTOLVR-WorkshopProfiles');
      if DirExists(DataDir) then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
