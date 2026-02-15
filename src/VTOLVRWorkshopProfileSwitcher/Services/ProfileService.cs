using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VTOLVRWorkshopProfileSwitcher.Models;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class ProfileService
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ProfileService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<ModProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(_paths.ProfilesDir, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var profiles = new List<ModProfile>(files.Count);

        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var profile = await JsonSerializer.DeserializeAsync<ModProfile>(stream, cancellationToken: cancellationToken);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SaveProfileAsync(ModProfile profile, CancellationToken cancellationToken = default)
    {
        var filePath = GetProfileFilePath(profile.Name);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, profile, _jsonOptions, cancellationToken);
    }

    public Task DeleteProfileAsync(string profileName)
    {
        var filePath = GetProfileFilePath(profileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    private string GetProfileFilePath(string profileName)
    {
        var safeName = string.Concat(profileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "Profile";
        }

        return Path.Combine(_paths.ProfilesDir, $"{safeName}.json");
    }
}
