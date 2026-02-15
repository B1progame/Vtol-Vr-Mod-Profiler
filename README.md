# VTOL VR Workshop Profile Switcher

Desktop tool for managing VTOL VR workshop content as reusable profiles.

## What this code does

This app scans your VTOL VR workshop directory (`steamapps/workshop/content/3018410`) and treats each workshop item folder as enabled or disabled:

- Enabled: folder name is the workshop ID (example: `1234567890`)
- Disabled: folder name is prefixed with `_OFF_` (example: `_OFF_1234567890`)

You can toggle mods, save named profiles, and apply a profile later. Applying a profile renames folders to match the selected enabled set.

## Main features

- Auto-detects Steam library workshop paths from `libraryfolders.vdf`
- Supports manual workshop path override
- Lists workshop items with display name, thumbnail, and download count
- Fetches missing metadata from Steam Workshop API when local metadata is not enough
- Search/filter by mod name or workshop ID
- Enable all / disable all / apply current toggles
- Save, load, and delete named profiles
- Creates a snapshot before applying changes, with restore-last-state support
- File watcher auto-refreshes the UI when workshop folders change
- Delete single or multiple mods from disk
- Optional: open Steam workshop page after delete
- App logging and crash logging
- Includes Inno Setup installer build script

## Tech stack

- .NET 8
- Avalonia UI 11
- CommunityToolkit.Mvvm
- System.Text.Json

## Project structure

- `src/VTOLVRWorkshopProfileSwitcher` - Avalonia application source
- `scripts/build-installer.ps1` - publish + installer build automation
- `installer/VTOLVRWorkshopProfileSwitcher.iss` - Inno Setup script

## Local data locations

The app stores its data under:

- `%LOCALAPPDATA%\VTOLVR-WorkshopProfiles\profiles` - saved profiles (`*.json`)
- `%LOCALAPPDATA%\VTOLVR-WorkshopProfiles\backups` - snapshots before apply
- `%LOCALAPPDATA%\VTOLVR-WorkshopProfiles\logs\app.log` - app operation logs

Crash logs are written to:

- `%USERPROFILE%\Documents\VTOLVR-WorkshopProfiles\logs\crash.log`

Thumbnail cache is written to:

- `%LOCALAPPDATA%\VTOLVRWorkshopProfileSwitcher\thumbnail-cache`

## Build and run (dev)

From repository root:

```powershell
dotnet restore .\VTOLVRWorkshopProfileSwitcher.sln
dotnet build .\VTOLVRWorkshopProfileSwitcher.sln -c Release
dotnet run --project .\src\VTOLVRWorkshopProfileSwitcher\VTOLVRWorkshopProfileSwitcher.csproj
```

## Build installer

Prerequisites:

- .NET SDK 8
- Inno Setup 6 (`ISCC.exe` at `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`), or pass a custom path

From repository root:

```powershell
.\scripts\build-installer.ps1 -Configuration Release -Runtime win-x64 -Version 1.0.0
```

Installer output:

- `installer\output\VTOLVRWorkshopProfileSwitcher-Setup.exe`

If Inno Setup is not installed, the script still publishes the app and prints the publish folder path.

## Notes

- This tool changes workshop folder names to control active/inactive items.
- Closing VTOL VR before applying profile changes is recommended.
- Admin permissions may be requested by installer/uninstaller.
