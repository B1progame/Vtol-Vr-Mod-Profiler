using System;
using System.Collections.Generic;

namespace VTOLVRWorkshopProfileSwitcher.Models;

public sealed class ModProfile
{
    public required string Name { get; init; }
    public required List<string> EnabledMods { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string Notes { get; init; } = string.Empty;
}
