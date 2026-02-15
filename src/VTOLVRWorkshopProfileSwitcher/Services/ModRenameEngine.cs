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

        foreach (var mod in currentMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shouldEnable = enabledIds.Contains(mod.WorkshopId);
            if (shouldEnable == mod.IsEnabled)
            {
                continue;
            }

            var targetName = shouldEnable ? mod.WorkshopId : $"{DisabledPrefix}{mod.WorkshopId}";
            var sourcePath = Path.Combine(workshopPath, mod.FolderName);
            var targetPath = Path.Combine(workshopPath, targetName);

            if (Directory.Exists(targetPath))
            {
                await (logAsync?.Invoke($"Skip rename for {mod.WorkshopId}; target already exists: {targetName}", cancellationToken)
                    ?? Task.CompletedTask);
                continue;
            }

            var success = await TryRenameWithRetryAsync(sourcePath, targetPath, cancellationToken);
            if (success)
            {
                renameCount++;
                await (logAsync?.Invoke($"Renamed {mod.FolderName} -> {targetName}", cancellationToken)
                    ?? Task.CompletedTask);
            }
            else
            {
                await (logAsync?.Invoke($"Failed to rename {mod.FolderName} -> {targetName}", cancellationToken)
                    ?? Task.CompletedTask);
            }
        }

        return renameCount;
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
}
