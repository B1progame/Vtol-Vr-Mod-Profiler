using System;
using System.IO;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class AppPaths
{
    public string BaseDir { get; }
    public string ProfilesDir { get; }
    public string BackupsDir { get; }
    public string LogsDir { get; }
    public string LogFile => Path.Combine(LogsDir, "app.log");

    public AppPaths()
    {
        BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VTOLVR-WorkshopProfiles");
        ProfilesDir = Path.Combine(BaseDir, "profiles");
        BackupsDir = Path.Combine(BaseDir, "backups");
        LogsDir = Path.Combine(BaseDir, "logs");

        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(LogsDir);
    }
}
