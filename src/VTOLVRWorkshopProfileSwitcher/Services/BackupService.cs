using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VTOLVRWorkshopProfileSwitcher.Models;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class BackupService
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public BackupService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<string> CreateSnapshotAsync(string workshopPath, CancellationToken cancellationToken = default)
    {
        var folders = Directory.Exists(workshopPath)
            ? Directory.EnumerateDirectories(workshopPath).Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList()
            : new();

        var snapshot = new WorkshopSnapshot
        {
            CreatedAtUtc = DateTime.UtcNow,
            WorkshopPath = workshopPath,
            Folders = folders
        };

        var filePath = Path.Combine(_paths.BackupsDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.json");
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken);
        return filePath;
    }

    public async Task<WorkshopSnapshot?> LoadLastSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var file = Directory.EnumerateFiles(_paths.BackupsDir, "*.json")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (file is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<WorkshopSnapshot>(stream, cancellationToken: cancellationToken);
    }
}
