using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VTOLVRWorkshopProfileSwitcher.Services;

public sealed class WorkshopWatcherService : IDisposable
{
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;

    public event Func<Task>? WorkshopChanged;

    public void Start(string workshopPath)
    {
        Stop();

        if (!Directory.Exists(workshopPath))
        {
            return;
        }

        _watcher = new FileSystemWatcher(workshopPath)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        DebounceRaise();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        DebounceRaise();
    }

    private void DebounceRaise()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        _ = RaiseDebouncedAsync(_debounceCts.Token);
    }

    private async Task RaiseDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            if (WorkshopChanged is not null)
            {
                await WorkshopChanged.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        Stop();
    }
}
