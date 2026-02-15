using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class LoadOnStartSyncService
{
    private const string TargetAppId = "3018410";
    private const string LoadOnStartFileName = "Load on Start";

    public async Task<LoadOnStartSyncResult> SyncAsync(
        IReadOnlyCollection<string> enabledWorkshopIds,
        CancellationToken cancellationToken = default)
    {
        var candidateFiles = FindCandidateFiles();
        if (candidateFiles.Count == 0)
        {
            return LoadOnStartSyncResult.Failed("No Steam cloud Load on Start file was found.");
        }

        var normalizedEnabledIds = enabledWorkshopIds
            .Where(IsNumericId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var filesUpdated = 0;
        var errors = new List<string>();

        foreach (var filePath in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await UpdateFileAsync(filePath, normalizedEnabledIds, cancellationToken);
                filesUpdated++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"{filePath}: {ex.Message}");
            }
        }

        if (filesUpdated == 0)
        {
            var detail = errors.Count == 0
                ? "No Steam Load on Start file could be updated."
                : string.Join(" | ", errors);
            return LoadOnStartSyncResult.Failed(detail);
        }

        if (errors.Count == 0)
        {
            return LoadOnStartSyncResult.Succeeded(
                filesUpdated,
                $"Updated {filesUpdated} Steam Load on Start file(s).");
        }

        return LoadOnStartSyncResult.Succeeded(
            filesUpdated,
            $"Updated {filesUpdated} Steam Load on Start file(s). Some files failed: {string.Join(" | ", errors)}");
    }

    private static async Task UpdateFileAsync(
        string filePath,
        IReadOnlyCollection<string> enabledIds,
        CancellationToken cancellationToken)
    {
        var existing = await ReadExistingAsync(filePath, cancellationToken);

        var localItems = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pair in existing.LocalItems)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                localItems[pair.Key] = pair.Value;
            }
        }

        var workshopItems = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in enabledIds)
        {
            workshopItems[id] = true;
        }

        var payload = new LoadOnStartPayload
        {
            WorkshopItems = workshopItems.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            LocalItems = localItems.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(filePath))
        {
            var backupPath = $"{filePath}.bak_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            File.Copy(filePath, backupPath, overwrite: true);
        }

        var json = JsonSerializer.Serialize(payload);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private static async Task<LoadOnStartPayload> ReadExistingAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new LoadOnStartPayload();
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new LoadOnStartPayload();
        }

        var parsed = JsonSerializer.Deserialize<LoadOnStartPayload>(content);
        return parsed ?? new LoadOnStartPayload();
    }

    private static List<string> FindCandidateFiles()
    {
        var results = new List<string>();
        var steamRoots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists);

        foreach (var steamRoot in steamRoots)
        {
            var userdataPath = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdataPath))
            {
                continue;
            }

            foreach (var userDir in Directory.EnumerateDirectories(userdataPath))
            {
                var appDir = Path.Combine(userDir, TargetAppId);
                if (!Directory.Exists(appDir))
                {
                    continue;
                }

                results.Add(Path.Combine(appDir, "remote", LoadOnStartFileName));
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(GetLastWriteTimeUtcSafe)
            .ToList();
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private static bool IsNumericId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.All(char.IsDigit);
    }

    private sealed class LoadOnStartPayload
    {
        public Dictionary<string, bool> WorkshopItems { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> LocalItems { get; set; } = new(StringComparer.Ordinal);
    }
}

public sealed record LoadOnStartSyncResult(bool Success, int FilesUpdated, string Message)
{
    public static LoadOnStartSyncResult Succeeded(int filesUpdated, string message)
        => new(true, filesUpdated, message);

    public static LoadOnStartSyncResult Failed(string message)
        => new(false, 0, message);
}
