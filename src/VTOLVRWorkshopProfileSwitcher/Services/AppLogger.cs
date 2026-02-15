using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class AppLogger
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AppLogger(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task LogAsync(string message, CancellationToken cancellationToken = default)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_paths.LogFile, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
