# VTOL VR Workshop Profile Switcher

VTOL VR Workshop Profile Switcher is a desktop utility for managing VTOL VR Steam Workshop mods with reusable profiles.

<table>
  <tr>
    <td align="center">
      <img src="src/VTOLVRWorkshopProfileSwitcher/Assets/AppIcon.png" alt="VTOL VR Workshop Profile Switcher app icon" width="220" />
    </td>
    <td align="center">
      <img src="assets/75bc077dc55b270ae842ada9b5fb911a.jpg" alt="Vtol-Vr" width="520" />
    </td>
  </tr>
</table>

It helps you switch between mod setups by treating workshop folders as either enabled or disabled, then applying a saved profile in one action.

## What It Does

- Scans your VTOL VR workshop folder (`steamapps/workshop/content/3018410`)
- Detects enabled mods when folder name is `<WorkshopId>`
- Detects disabled mods when folder name is `_OFF_<WorkshopId>`
- Shows workshop items with display name, thumbnail, and download count (when available)
- Lets you search, filter, select, and bulk-manage mods
- Saves and loads named mod profiles
- Applies profiles by renaming folders to match the selected enabled set
- Creates a snapshot before applying changes so you can restore the last state
- Supports deleting single or multiple mods from disk
- Can open the Steam Workshop page after deleting a mod

## Features

- Automatic Steam workshop path detection (with manual override)
- Live refresh when workshop folders change (file system watcher)
- Profile save, load, apply, and delete
- Enable all, disable all, and apply current toggles
- Restore last snapshot
- Mod card view with thumbnail and metadata enrichment from Steam API
- App operation logging and crash logging
- Optional installer build script (PowerShell + Inno Setup)

## Workflow (High Level)

1. The app detects your VTOL VR workshop path automatically from Steam library configuration (`libraryfolders.vdf`) or uses a manual path.
2. It scans workshop subfolders and classifies each mod as enabled or disabled from the folder name.
3. You choose which mods should be active, then save that state as a profile.
4. When you apply a profile, the app renames folders (`<id>` or `_OFF_<id>`) to match the profile.
5. Before renaming, it stores a snapshot backup of the current folder state for recovery.

## Purpose

The goal of this project is to make switching between different VTOL VR mod combinations fast and repeatable without manually renaming workshop folders every time.

## Data & File Locations

The app stores data under `%LOCALAPPDATA%\VTOLVR-WorkshopProfiles`:

- `profiles` - saved profile JSON files
- `backups` - snapshot JSON files created before apply
- `logs\app.log` - operation logs

Additional files:

- Crash log: `%USERPROFILE%\Documents\VTOLVR-WorkshopProfiles\logs\crash.log`
- Thumbnail cache: `%LOCALAPPDATA%\VTOLVRWorkshopProfileSwitcher\thumbnail-cache`

## Repository Structure

- `src/VTOLVRWorkshopProfileSwitcher` - Avalonia app source code
- `scripts/build-installer.ps1` - publish and installer automation
- `installer/VTOLVRWorkshopProfileSwitcher.iss` - Inno Setup installer definition

## Notes

- This tool controls mod activation by renaming workshop folders.
- Closing VTOL VR before applying changes is recommended.

## Installation

### Option 1: Use a Release Build / Installer

If you have a packaged release, install and run `VTOLVRWorkshopProfileSwitcher`.

### Option 2: Build and Run from Source

Requirements:

- .NET 8 SDK
- Windows with Steam + VTOL VR workshop content

Commands:

```powershell
dotnet restore .\VTOLVRWorkshopProfileSwitcher.sln
dotnet build .\VTOLVRWorkshopProfileSwitcher.sln -c Release
dotnet run --project .\src\VTOLVRWorkshopProfileSwitcher\VTOLVRWorkshopProfileSwitcher.csproj
```

Optional installer build (requires Inno Setup 6):

```powershell
.\scripts\build-installer.ps1 -Configuration Release -Runtime win-x64 -Version 1.0.0
```

If this project is useful to you, please consider giving it a star on GitHub. :star:
