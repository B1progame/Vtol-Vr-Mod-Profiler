using System.Collections.Generic;

namespace VTOLVRWorkshopProfileSwitcher.Models;

public sealed class ProfilePackageImportResult
{
    public List<ModProfile> ImportedProfiles { get; } = new();
    public int ImportedCount { get; set; }
    public int RenamedCount { get; set; }
    public int OverwrittenCount { get; set; }
    public int SkippedCount { get; set; }
    public int InvalidProfileCount { get; set; }
    public int RemovedInvalidWorkshopIdsCount { get; set; }
}
