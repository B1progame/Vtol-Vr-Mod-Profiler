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
AppName=VTOL VR Workshop Profile Switcher
AppVersion={#MyAppVersion}
AppPublisher={#PublisherName}
AppPublisherURL=https://github.com/
AppSupportURL=https://github.com/
AppUpdatesURL=https://github.com/
DefaultDirName={autopf}\VTOL VR Workshop Profile Switcher
DefaultGroupName=VTOL VR Workshop Profile Switcher
DisableProgramGroupPage=yes
OutputDir=installer\output
OutputBaseFilename=VTOLVRWorkshopProfileSwitcher-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\VTOLVRWorkshopProfileSwitcher.exe
SetupIconFile={#IconFile}
VersionInfoCompany={#PublisherName}
VersionInfoDescription=VTOL VR Workshop Profile Switcher Installer
VersionInfoProductName=VTOL VR Workshop Profile Switcher
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VTOL VR Workshop Profile Switcher"; Filename: "{app}\VTOLVRWorkshopProfileSwitcher.exe"
Name: "{autodesktop}\VTOL VR Workshop Profile Switcher"; Filename: "{app}\VTOLVRWorkshopProfileSwitcher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VTOLVRWorkshopProfileSwitcher.exe"; Description: "Launch VTOL VR Workshop Profile Switcher"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserData: Boolean;

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
