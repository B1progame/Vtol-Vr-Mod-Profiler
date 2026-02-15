namespace VTOLVRWorkshopProfileSwitcher.Models;

public sealed class WorkshopMod
{
    public required string WorkshopId { get; set; }
    public required string FolderName { get; set; }
    public required string FullPath { get; set; }
    public required bool IsEnabled { get; set; }
    public string DisplayName { get; set; } = "Unknown Mod";
    public string? ThumbnailPath { get; set; }
    public long? DownloadCount { get; set; }
}
