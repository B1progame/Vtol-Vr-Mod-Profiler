using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class LoadOnStartSyncService
{
    private const string TargetAppId = "3018410";
    private const string LoadOnStartFileName = "Load on Start";

    public async Task<IReadOnlySet<string>?> TryReadEnabledWorkshopIdsAsync(CancellationToken cancellationToken = default)
    {
        var candidateFiles = FindCandidateFiles();
        if (candidateFiles.Count == 0)
        {
            return null;
        }

        foreach (var filePath in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var existing = await ReadExistingAsync(filePath, cancellationToken);
                return existing.WorkshopItems
                    .Where(pair => pair.Value && IsNumericId(pair.Key))
                    .Select(pair => pair.Key)
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Try the next candidate file if one Steam user file is transiently unavailable.
            }
        }

        return null;
    }

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
            var backupPath = BuildBackupPath(filePath);
            try
            {
                var backupDirectory = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrWhiteSpace(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                }

                File.Copy(filePath, backupPath, overwrite: true);
            }
            catch (IOException)
            {
                // Backup should be best-effort only.
            }
            catch (UnauthorizedAccessException)
            {
                // Backup should be best-effort only.
            }
        }

        var json = JsonSerializer.Serialize(payload);
        await WriteAtomicAsync(filePath, json, cancellationToken);
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

        try
        {
            var parsed = JsonSerializer.Deserialize<LoadOnStartPayload>(content);
            return parsed ?? new LoadOnStartPayload();
        }
        catch (JsonException)
        {
            // The file may be transiently incomplete while another process updates it.
            return new LoadOnStartPayload();
        }
    }

    private static async Task WriteAtomicAsync(string filePath, string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath) ?? ".";
        var tempPath = Path.Combine(directory, $".vtolwps_loadonstart_tmp_{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Ignore temp cleanup failures.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore temp cleanup failures.
                }
            }
        }
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

    private static string BuildBackupPath(string sourceFilePath)
    {
        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VTOLVR-WorkshopProfiles",
            "backups",
            "load-on-start");
        var steamUserId = GetSteamUserIdFromLoadOnStartPath(sourceFilePath) ?? "unknown";
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
        return Path.Combine(backupRoot, $"{steamUserId}_{timestamp}.json");
    }

    private static string? GetSteamUserIdFromLoadOnStartPath(string filePath)
    {
        // Expected path shape: ...\userdata\<steamUserId>\3018410\remote\Load on Start
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = filePath.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("userdata", StringComparison.OrdinalIgnoreCase))
            {
                var userIdIndex = i + 1;
                if (userIdIndex < segments.Length)
                {
                    var userId = segments[userIdIndex];
                    if (IsNumericId(userId))
                    {
                        return userId;
                    }
                }
            }
        }

        return null;
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
