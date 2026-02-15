using System;
using System.Collections.Generic;

namespace VTOLVRWorkshopProfileSwitcher.Models;

public sealed class WorkshopSnapshot
{
    public required DateTime CreatedAtUtc { get; init; }
    public required string WorkshopPath { get; init; }
    public required List<string> Folders { get; init; }
}
