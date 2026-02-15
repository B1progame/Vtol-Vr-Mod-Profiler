namespace VTOLVRWorkshopProfileSwitcher.Models;

public sealed class AppSettings
{
    public string SelectedDesign { get; set; } = "TACTICAL RED";
    public bool OpenSteamPageAfterDelete { get; set; } = true;
    public bool AutoInstallUpdates { get; set; }
}
