using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VTOLVRWorkshopProfileSwitcher.Models;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class ModRenameEngine
{
    private const string DisabledPrefix = "_OFF_";

    public async Task<int> ApplyEnabledSetAsync(
        string workshopPath,
        IReadOnlyCollection<WorkshopMod> currentMods,
        IReadOnlySet<string> enabledIds,
        Func<string, CancellationToken, Task>? logAsync = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workshopPath))
        {
            return 0;
        }

        var renameCount = 0;
        var modsByWorkshopId = currentMods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.WorkshopId))
            .GroupBy(mod => mod.WorkshopId, StringComparer.Ordinal);

        foreach (var modGroup in modsByWorkshopId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workshopId = modGroup.Key;
            var shouldEnable = enabledIds.Contains(workshopId);
            var targetName = shouldEnable ? workshopId : $"{DisabledPrefix}{workshopId}";
            var targetPath = Path.Combine(workshopPath, targetName);
            var groupEntries = modGroup.ToList();
            var targetEntry = groupEntries.FirstOrDefault(entry =>
                string.Equals(entry.FolderName, targetName, StringComparison.OrdinalIgnoreCase));

            if (targetEntry is null)
            {
                var sourceEntry = shouldEnable
                    ? groupEntries.FirstOrDefault(entry => !entry.IsEnabled)
                    : groupEntries.FirstOrDefault(entry => entry.IsEnabled);

                sourceEntry ??= groupEntries.FirstOrDefault();
                if (sourceEntry is not null)
                {
                    var sourcePath = Path.Combine(workshopPath, sourceEntry.FolderName);
                    var success = await TryRenameWithRetryAsync(sourcePath, targetPath, cancellationToken);
                    if (success)
                    {
                        renameCount++;
                        await (logAsync?.Invoke($"Renamed {sourceEntry.FolderName} -> {targetName}", cancellationToken)
                            ?? Task.CompletedTask);
                    }
                    else
                    {
                        await (logAsync?.Invoke($"Failed to rename {sourceEntry.FolderName} -> {targetName}", cancellationToken)
                            ?? Task.CompletedTask);
                    }
                }
            }

            var duplicateEntries = groupEntries.Where(entry =>
                !string.Equals(entry.FolderName, targetName, StringComparison.OrdinalIgnoreCase));

            foreach (var duplicate in duplicateEntries)
            {
                var duplicatePath = Path.Combine(workshopPath, duplicate.FolderName);
                if (!Directory.Exists(duplicatePath))
                {
                    continue;
                }

                var deleted = await TryDeleteWithRetryAsync(duplicatePath, cancellationToken);
                if (deleted)
                {
                    await (logAsync?.Invoke(
                            $"Removed duplicate folder for {workshopId}: {duplicate.FolderName}",
                            cancellationToken)
                        ?? Task.CompletedTask);
                }
                else
                {
                    await (logAsync?.Invoke(
                            $"Failed to remove duplicate folder for {workshopId}: {duplicate.FolderName}",
                            cancellationToken)
                        ?? Task.CompletedTask);
                }
            }
        }

        return renameCount;
    }

    public async Task<DuplicateCleanupResult> CleanupDuplicateFoldersAsync(
        string workshopPath,
        IReadOnlyCollection<WorkshopMod> currentMods,
        Func<string, CancellationToken, Task>? logAsync = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workshopPath))
        {
            return new DuplicateCleanupResult(0, 0, 0, 0);
        }

        var groupsWithDuplicates = 0;
        var removedFolders = 0;
        var renamedFolders = 0;
        var failedOperations = 0;

        var modsByWorkshopId = currentMods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.WorkshopId))
            .GroupBy(mod => mod.WorkshopId, StringComparer.Ordinal);

        foreach (var modGroup in modsByWorkshopId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = modGroup.ToList();
            if (entries.Count <= 1)
            {
                continue;
            }

            groupsWithDuplicates++;
            var workshopId = modGroup.Key;
            var canonicalName = workshopId;
            var canonicalPath = Path.Combine(workshopPath, canonicalName);

            var sourceToKeep = entries
                .OrderByDescending(entry => entry.IsEnabled)
                .ThenByDescending(entry => GetLastWriteTimeUtcSafe(Path.Combine(workshopPath, entry.FolderName)))
                .First();
            var sourcePath = Path.Combine(workshopPath, sourceToKeep.FolderName);

            if (!string.Equals(sourceToKeep.FolderName, canonicalName, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(canonicalPath))
                {
                    var deletedCanonical = await TryDeleteWithRetryAsync(canonicalPath, cancellationToken);
                    if (deletedCanonical)
                    {
                        removedFolders++;
                        await (logAsync?.Invoke(
                                $"Removed duplicate folder for {workshopId}: {canonicalName}",
                                cancellationToken)
                            ?? Task.CompletedTask);
                    }
                    else
                    {
                        failedOperations++;
                        await (logAsync?.Invoke(
                                $"Failed to remove duplicate folder for {workshopId}: {canonicalName}",
                                cancellationToken)
                            ?? Task.CompletedTask);
                        continue;
                    }
                }

                var renamed = await TryRenameWithRetryAsync(sourcePath, canonicalPath, cancellationToken);
                if (renamed)
                {
                    renamedFolders++;
                    await (logAsync?.Invoke(
                            $"Renamed {sourceToKeep.FolderName} -> {canonicalName} during duplicate cleanup",
                            cancellationToken)
                        ?? Task.CompletedTask);
                }
                else
                {
                    failedOperations++;
                    await (logAsync?.Invoke(
                            $"Failed to rename {sourceToKeep.FolderName} -> {canonicalName} during duplicate cleanup",
                            cancellationToken)
                        ?? Task.CompletedTask);
                    continue;
                }
            }

            foreach (var entry in entries)
            {
                if (string.Equals(entry.FolderName, canonicalName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = Path.Combine(workshopPath, entry.FolderName);
                if (!Directory.Exists(path))
                {
                    continue;
                }

                var deleted = await TryDeleteWithRetryAsync(path, cancellationToken);
                if (deleted)
                {
                    removedFolders++;
                    await (logAsync?.Invoke(
                            $"Removed duplicate folder for {workshopId}: {entry.FolderName}",
                            cancellationToken)
                        ?? Task.CompletedTask);
                }
                else
                {
                    failedOperations++;
                    await (logAsync?.Invoke(
                            $"Failed to remove duplicate folder for {workshopId}: {entry.FolderName}",
                            cancellationToken)
                        ?? Task.CompletedTask);
                }
            }
        }

        return new DuplicateCleanupResult(groupsWithDuplicates, removedFolders, renamedFolders, failedOperations);
    }

    private static async Task<bool> TryRenameWithRetryAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Directory.Move(sourcePath, targetPath);
                return true;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(120 * attempt, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(120 * attempt, cancellationToken);
            }
        }

        return false;
    }

    private static async Task<bool> TryDeleteWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return true;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(120 * attempt, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(120 * attempt, cancellationToken);
            }
        }

        return false;
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}

public sealed record DuplicateCleanupResult(
    int GroupsWithDuplicates,
    int RemovedFolders,
    int RenamedFolders,
    int FailedOperations);
