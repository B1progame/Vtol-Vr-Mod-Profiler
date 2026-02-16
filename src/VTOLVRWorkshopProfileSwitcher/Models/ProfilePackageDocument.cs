using System;
using System.Collections.Generic;

namespace VTOLVRWorkshopProfileSwitcher.Models;

public sealed class ProfilePackageDocument
{
    public int SchemaVersion { get; init; }
    public string PackageName { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public List<ProfilePackageProfile> Profiles { get; init; } = new();
}

public sealed class ProfilePackageProfile
{
    public string Name { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public List<string> EnabledWorkshopIds { get; init; } = new();
}
