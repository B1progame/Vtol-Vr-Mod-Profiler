using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VTOLVRWorkshopProfileSwitcher.Models;
using VTOLVRWorkshopProfileSwitcher.Services;
using VTOLVRWorkshopProfileSwitcher.Views;

namespace VTOLVRWorkshopProfileSwitcher.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const string GitHubOwner = "B1progame";
    private const string GitHubRepoName = "Vtol-Vr-Mod";
    private const string ReleasesPageUrl = "https://github.com/B1progame/Vtol-Vr-Mod/releases";
    private const string DefaultInstallerAssetName = "VTOLVRSwitcher-Setup.exe";
    private const int DowngradeReleasePageSize = 50;
    private static readonly string[] CwbPackFolderToggleExcludeNameTokens =
    {
        "cwb addon",
        "custom weapons base addon"
    };
    private static readonly Version CurrentAppVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
    private static readonly string CurrentVersionId = GetCurrentVersionId();
    private static readonly HttpClient GitHubHttpClient = new();
    private readonly AppPaths _appPaths = new();
    private readonly SteamLibraryDetector _detector = new();
    private readonly WorkshopScanner _scanner = new();
    private readonly WorkshopDependencyResolverService _dependencyResolver = new();
    private readonly CwbLoadItemsService _cwbLoadItemsService = new();
    private readonly ModRenameEngine _renameEngine = new();
    private readonly LoadOnStartSyncService _loadOnStartSyncService = new();
    private readonly WorkshopWatcherService _watcher = new();
    private readonly ProfileService _profileService;
    private readonly ProfilePackageService _profilePackageService;
    private readonly BackupService _backupService;
    private readonly AppSettingsService _settingsService;
    private readonly AppLogger _logger;
    private readonly DirectorySizeCacheService _directorySizeCache = new();
    private const string VrRuntimeSteamVr = "SteamVR";
    private const string VrRuntimeOculus = "Oculus";
    private const string VrRuntimeOpenXr = "OpenXR";
    public const string LaunchHoverTargetSidebar = "sidebar";
    public const string LaunchHoverTargetModded = "modded";
    public const string LaunchHoverTargetVanilla = "vanilla";

    private readonly ObservableCollection<ModItemViewModel> _allMods = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _liveDependencyLock = new(1, 1);
    private readonly SemaphoreSlim _autoCleanupLock = new(1, 1);
    private bool _suppressSettingsSave;
    private bool _isProjectingDependencyStates;
    private bool _isLoadingProfileSelectionIntoToggles;
    private CancellationTokenSource? _addModeProfileSaveCts;
    private CancellationTokenSource? _dependencyPreviewCts;
    private CancellationTokenSource? _vtolRunningMonitorCts;
    private HashSet<string>? _lastRequestedEnabledSet;
    private List<string> _lastRequiredOrderedIds = new();
    private string _lastApplyContext = string.Empty;
    private DateTime _lastAutoCleanupUtc = DateTime.MinValue;
    private bool _startupInitializationComplete;
    private bool _startupUpdateCheckQueued;

    [ObservableProperty]
    private ObservableCollection<ModItemViewModel> filteredMods = new();

    [ObservableProperty]
    private ObservableCollection<ProfileItemViewModel> profiles = new();

    [ObservableProperty]
    private ObservableCollection<ProfileItemViewModel> filteredProfiles = new();

    [ObservableProperty]
    private ProfileItemViewModel? selectedProfile;

    [ObservableProperty]
    private string profileNameInput = string.Empty;

    [ObservableProperty]
    private string profileNotesInput = string.Empty;

    [ObservableProperty]
    private string profileSearchQuery = string.Empty;

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private string manualWorkshopPath = string.Empty;

    [ObservableProperty]
    private string activeWorkshopPath = "Not detected";

    [ObservableProperty]
    private string steamStatusPath = "Detecting Steam libraries...";

    [ObservableProperty]
    private string statusMessage = "Ready";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isLaunchingGame;

    [ObservableProperty]
    private bool isVtolRunning;

    [ObservableProperty]
    private string launchButtonHoverTarget = string.Empty;

    [ObservableProperty]
    private bool openSteamPageAfterDelete = true;

    [ObservableProperty]
    private bool isSettingsOpen;

    [ObservableProperty]
    private string selectedDesign = "TACTICAL RED";

    [ObservableProperty]
    private bool hasUpdateAvailable;

    [ObservableProperty]
    private string latestReleaseVersion = "Not checked";

    [ObservableProperty]
    private string latestReleaseUrl = ReleasesPageUrl;

    [ObservableProperty]
    private string updateStatusText = "Checking for updates...";

    [ObservableProperty]
    private bool isCheckingForUpdates;

    [ObservableProperty]
    private bool isDownloadingUpdate;

    [ObservableProperty]
    private bool canAutoInstallUpdate;

    [ObservableProperty]
    private bool autoInstallUpdates;

    [ObservableProperty]
    private string selectedVrRuntime = VrRuntimeSteamVr;

    [ObservableProperty]
    private string latestInstallerUrl = string.Empty;

    [ObservableProperty]
    private string latestInstallerFileName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ReleaseInstallOption> downgradeReleaseOptions = new();

    [ObservableProperty]
    private ReleaseInstallOption? selectedDowngradeRelease;

    [ObservableProperty]
    private bool isLoadingDowngradeReleases;

    [ObservableProperty]
    private string selectedImportConflictPolicy = "Rename";

    [ObservableProperty]
    private ObservableCollection<string> missingWorkshopIds = new();

    [ObservableProperty]
    private string missingModsContext = string.Empty;

    [ObservableProperty]
    private int nextMissingModIndex;

    public IReadOnlyList<string> DesignOptions { get; } = new[] { "TACTICAL RED", "STEEL BLUE" };
    public IReadOnlyList<string> ImportConflictPolicyOptions { get; } = new[] { "Rename", "Overwrite", "Skip" };
    public IReadOnlyList<string> VrRuntimeOptions { get; } = new[] { VrRuntimeSteamVr, VrRuntimeOculus, VrRuntimeOpenXr };
    public bool IsSteamVrRuntime => string.Equals(SelectedVrRuntime, VrRuntimeSteamVr, StringComparison.OrdinalIgnoreCase);
    public bool IsOculusRuntime => string.Equals(SelectedVrRuntime, VrRuntimeOculus, StringComparison.OrdinalIgnoreCase);
    public bool IsOpenXrRuntime => string.Equals(SelectedVrRuntime, VrRuntimeOpenXr, StringComparison.OrdinalIgnoreCase);
    public int MissingModsCount => MissingWorkshopIds.Count;
    public bool HasMissingMods => MissingModsCount > 0;
    public bool CanApplyAgain => _lastRequestedEnabledSet is not null && _lastRequestedEnabledSet.Count > 0;
    public string AppAuthor => "B1progame";
    public string AppCreatedOn => "2026-02-15";
    public string CurrentVersionText => $"v{CurrentAppVersion.Major}.{CurrentAppVersion.Minor}.{CurrentAppVersion.Build}";
    public string CurrentVersionIdText => CurrentVersionId;
    public bool CanInstallSelectedDowngrade => SelectedDowngradeRelease is not null && !IsLoadingDowngradeReleases;

    public MainWindowViewModel()
    {
        _profileService = new ProfileService(_appPaths);
        _profilePackageService = new ProfilePackageService();
        _backupService = new BackupService(_appPaths);
        _settingsService = new AppSettingsService(_appPaths);
        _logger = new AppLogger(_appPaths);
        _ = _logger.InfoAsync($"Session started. Log file: {Path.GetFileName(_appPaths.LogFile)}");

        _allMods.CollectionChanged += OnModsCollectionChanged;
        _watcher.WorkshopChanged += OnWorkshopChangedAsync;
        EnsureGitHubHttpDefaults();
        InitializeShell();
        StartVtolRunningMonitor();

        _ = InitializeAsync();
    }

    public override void Dispose()
    {
        // Do not force-disable Doorstop on launcher exit.
        // Users may close the launcher immediately after pressing Play; disabling
        // here can race with VTOL startup and prevent mods from loading.
        TryRestoreDisabledModFoldersOnLauncherExit();
        _launchCts?.Cancel();
        _launchCts?.Dispose();
        _vtolRunningMonitorCts?.Cancel();
        _vtolRunningMonitorCts?.Dispose();
        _vtolExitCleanupCts?.Cancel();
        _vtolExitCleanupCts?.Dispose();
        _addModeProfileSaveCts?.Cancel();
        _addModeProfileSaveCts?.Dispose();
        _dependencyPreviewCts?.Cancel();
        _dependencyPreviewCts?.Dispose();
        _profileDependencyToggleCts?.Cancel();
        _profileDependencyToggleCts?.Dispose();
        _watcher.Dispose();
        _autoCleanupLock.Dispose();
        _liveDependencyLock.Dispose();
        _refreshLock.Dispose();
    }

    private void TryRestoreDisabledModFoldersOnLauncherExit()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) ||
                string.Equals(ActiveWorkshopPath, "Not detected", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(ActiveWorkshopPath))
            {
                return;
            }

            var folderNames = Directory.EnumerateDirectories(ActiveWorkshopPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            var parsedMods = new List<WorkshopMod>(folderNames.Count);
            foreach (var folderName in folderNames)
            {
                if (folderName is null)
                {
                    continue;
                }

                if (!WorkshopScanner.TryGetWorkshopId(folderName, out var workshopId, out var isEnabled))
                {
                    continue;
                }

                parsedMods.Add(new WorkshopMod
                {
                    WorkshopId = workshopId,
                    FolderName = folderName,
                    FullPath = Path.Combine(ActiveWorkshopPath, folderName),
                    IsEnabled = isEnabled,
                    DisplayName = $"Mod {workshopId}"
                });
            }

            if (parsedMods.Count == 0)
            {
                return;
            }

            var allIds = parsedMods
                .Select(mod => mod.WorkshopId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);

            if (allIds.Count == 0)
            {
                return;
            }

            _renameEngine.ApplyEnabledSetAsync(
                    ActiveWorkshopPath,
                    parsedMods,
                    allIds,
                    disableUnselectedMods: false,
                    logAsync: (line, token) => _logger.LogAsync(line, token),
                    cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Shutdown cleanup is best-effort only.
        }
    }

    private CancellationTokenSource? _launchCts;
    private CancellationTokenSource? _vtolExitCleanupCts;

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        ProfileNameInput = value.Name;
        ProfileNotesInput = value.Notes;
    }

    partial void OnProfileSearchQueryChanged(string value)
    {
        ApplyProfileFilter();
    }

    partial void OnSelectedDesignChanged(string value)
    {
        SaveSettingsIfNeeded();
    }

    partial void OnOpenSteamPageAfterDeleteChanged(bool value)
    {
        SaveSettingsIfNeeded();
    }

    partial void OnAutoInstallUpdatesChanged(bool value)
    {
        SaveSettingsIfNeeded();
    }

    partial void OnSelectedDowngradeReleaseChanged(ReleaseInstallOption? value)
    {
        OnPropertyChanged(nameof(CanInstallSelectedDowngrade));
    }

    partial void OnIsLoadingDowngradeReleasesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallSelectedDowngrade));
    }

    partial void OnSelectedVrRuntimeChanged(string value)
    {
        if (!VrRuntimeOptions.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            SelectedVrRuntime = VrRuntimeSteamVr;
            return;
        }

        OnPropertyChanged(nameof(IsSteamVrRuntime));
        OnPropertyChanged(nameof(IsOculusRuntime));
        OnPropertyChanged(nameof(IsOpenXrRuntime));
        SaveSettingsIfNeeded();
    }

    [RelayCommand]
    private void SetVrRuntime(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime))
        {
            return;
        }

        if (!VrRuntimeOptions.Contains(runtime, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(SelectedVrRuntime, runtime, StringComparison.OrdinalIgnoreCase))
        {
            SelectedVrRuntime = runtime;
        }
    }

    partial void OnMissingWorkshopIdsChanged(ObservableCollection<string> value)
    {
        OnPropertyChanged(nameof(MissingModsCount));
        OnPropertyChanged(nameof(HasMissingMods));
        NotifyModStatsChanged();
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        NavigateSettings();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        try
        {
            var latest = await TryGetLatestReleaseAsync();
            if (latest is null)
            {
                HasUpdateAvailable = false;
                CanAutoInstallUpdate = false;
                LatestReleaseVersion = "None";
                LatestReleaseUrl = ReleasesPageUrl;
                LatestInstallerUrl = string.Empty;
                LatestInstallerFileName = string.Empty;
                UpdateStatusText = "No GitHub releases published yet.";
                return;
            }

            var latestTag = latest.Value.TagName;
            LatestReleaseVersion = string.IsNullOrWhiteSpace(latestTag) ? "Unknown" : latestTag;
            LatestReleaseUrl = string.IsNullOrWhiteSpace(latest.Value.HtmlUrl) ? ReleasesPageUrl : latest.Value.HtmlUrl;
            LatestInstallerUrl = string.IsNullOrWhiteSpace(latest.Value.InstallerUrl)
                ? $"https://github.com/{GitHubOwner}/{GitHubRepoName}/releases/latest/download/{DefaultInstallerAssetName}"
                : latest.Value.InstallerUrl;
            LatestInstallerFileName = string.IsNullOrWhiteSpace(latest.Value.InstallerName)
                ? DefaultInstallerAssetName
                : latest.Value.InstallerName;
            HasUpdateAvailable = IsUpdateAvailable(latestTag, CurrentAppVersion, CurrentVersionId);
            CanAutoInstallUpdate = HasUpdateAvailable;

            if (!HasUpdateAvailable)
            {
                UpdateStatusText = $"You are up to date ({CurrentVersionText}, id {CurrentVersionIdText})";
            }
            else if (!CanAutoInstallUpdate)
            {
                UpdateStatusText = $"Update found ({LatestReleaseVersion}), but no installer asset (.exe) is attached.";
            }
            else
            {
                UpdateStatusText = $"New version available: {LatestReleaseVersion} (current {CurrentVersionText})";
            }

            if (AutoInstallUpdates && CanAutoInstallUpdate)
            {
                if (!_startupInitializationComplete)
                {
                    UpdateStatusText = $"New version available: {LatestReleaseVersion} (auto install will run after startup)";
                    return;
                }

                await DownloadAndInstallUpdateAsync();
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            HasUpdateAvailable = false;
            CanAutoInstallUpdate = false;
            UpdateStatusText = $"Update check failed ({(int)ex.StatusCode.Value})";
        }
        catch
        {
            HasUpdateAvailable = false;
            CanAutoInstallUpdate = false;
            UpdateStatusText = "Update check failed";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (IsDownloadingUpdate)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(LatestInstallerUrl))
        {
            UpdateStatusText = "Installer asset not found in latest release.";
            return;
        }

        IsDownloadingUpdate = true;
        try
        {
            var fileName = string.IsNullOrWhiteSpace(LatestInstallerFileName)
                ? DefaultInstallerAssetName
                : LatestInstallerFileName;

            var tempDir = Path.Combine(Path.GetTempPath(), "VTOLVRWorkshopProfileSwitcher", "updates");
            Directory.CreateDirectory(tempDir);
            var installerPath = Path.Combine(tempDir, fileName);

            UpdateStatusText = $"Downloading {fileName}...";
            using (var response = await GitHubHttpClient.GetAsync(LatestInstallerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(installerPath);
                await source.CopyToAsync(target);
            }

            var runFullInstallerUi = ShouldRunFullInstallerUi(LatestReleaseVersion, CurrentAppVersion);
            UpdateStatusText = runFullInstallerUi
                ? "Download complete. Launching installer UI..."
                : "Download complete. Installing update...";
            var installerArguments = runFullInstallerUi
                ? string.Empty
                : "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS";
            var currentExePath = Environment.ProcessPath;
            var installerProcess = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = installerArguments,
                UseShellExecute = true,
                Verb = "runas"
            });

            if (installerProcess is null)
            {
                UpdateStatusText = "Installer failed to start.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(currentExePath))
            {
                ScheduleRelaunchAfterInstaller(installerProcess.Id, currentExePath, LatestReleaseVersion);
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Win32Exception)
        {
            UpdateStatusText = "Update installation was canceled.";
        }
        catch
        {
            UpdateStatusText = "Update install failed. Please try again.";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private static void ScheduleRelaunchAfterInstaller(int installerProcessId, string exePath, string? expectedVersionTag)
    {
        var escapedExePath = exePath.Replace("'", "''", StringComparison.Ordinal);
        var normalizedExpected = NormalizeTag(expectedVersionTag).Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"$expectedTag = '{normalizedExpected}'; " +
            "$expectedVersion = $null; " +
            "$expectedVersionKey = $null; " +
            "if (-not [string]::IsNullOrWhiteSpace($expectedTag)) { " +
            "  try { " +
            "    $expectedVersion = [version]$expectedTag; " +
            "    $expectedVersionKey = '{0}.{1}.{2}' -f $expectedVersion.Major, $expectedVersion.Minor, $expectedVersion.Build; " +
            "  } catch { $expectedVersion = $null; $expectedVersionKey = $null } " +
            "}; " +
            $"try {{ Wait-Process -Id {installerProcessId} -ErrorAction SilentlyContinue }} catch {{ }}; " +
            "$deadline = (Get-Date).AddMinutes(3); " +
            "while ((Get-Date) -lt $deadline) { " +
            $"  if (-not (Test-Path '{escapedExePath}')) {{ Start-Sleep -Seconds 1; continue }}; " +
            "  if ($null -eq $expectedVersion) { Start-Sleep -Seconds 4; break }; " +
            "  try { " +
            $"    $installedFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo('{escapedExePath}').FileVersion; " +
            "    if (-not [string]::IsNullOrWhiteSpace($installedFileVersion)) { " +
            "      $installedVersion = [version]$installedFileVersion; " +
            "      $installedVersionKey = '{0}.{1}.{2}' -f $installedVersion.Major, $installedVersion.Minor, $installedVersion.Build; " +
            "      if ($installedVersionKey -eq $expectedVersionKey) { break } " +
            "    } " +
            "  } catch { }; " +
            "  Start-Sleep -Seconds 1; " +
            "}; " +
            $"if (Test-Path '{escapedExePath}') {{ Start-Process -FilePath '{escapedExePath}' }}";

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    [RelayCommand]
    private async Task DetectPathsAsync()
    {
        IsBusy = true;
        try
        {
            var paths = await _detector.FindWorkshopPathsAsync();
            if (paths.Count == 0)
            {
                SteamStatusPath = "No VTOL VR workshop path detected automatically";
                ActiveWorkshopPath = "Not detected";
                return;
            }

            var selectedPath = paths
                .Select(path => new
                {
                    Path = path,
                    Count = Directory.Exists(path) ? Directory.EnumerateDirectories(path).Count() : 0
                })
                .OrderByDescending(item => item.Count)
                .First();

            ActiveWorkshopPath = selectedPath.Path;
            var scannedBeforeCleanup = await _scanner.ScanAsync(ActiveWorkshopPath);
            var startupCleanup = await _renameEngine.CleanupDuplicateFoldersAsync(
                ActiveWorkshopPath,
                scannedBeforeCleanup,
                (line, token) => _logger.LogAsync(line, token));

            if (startupCleanup.GroupsWithDuplicates > 0)
            {
                await _logger.LogAsync(
                    $"Startup duplicate cleanup: groups={startupCleanup.GroupsWithDuplicates}, removed={startupCleanup.RemovedFolders}, renamed={startupCleanup.RenamedFolders}, failed={startupCleanup.FailedOperations}");
            }

            SteamStatusPath = $"Auto-detected via Steam libraryfolders.vdf ({CountWorkshopFolders(ActiveWorkshopPath)} folders)";
            await RefreshModsAsync();
            _watcher.Start(ActiveWorkshopPath);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UseManualPathAsync()
    {
        var manualPath = ManualWorkshopPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(manualPath))
        {
            StatusMessage = "Manual path is empty";
            return;
        }

        ActiveWorkshopPath = manualPath;
        SteamStatusPath = $"Manual override active ({CountWorkshopFolders(ActiveWorkshopPath)} folders)";
        _watcher.Start(ActiveWorkshopPath);
        await RefreshModsAsync();
    }

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
            {
                return;
            }

            var scanned = await _scanner.ScanAsync(ActiveWorkshopPath);

            _allMods.Clear();
            foreach (var mod in scanned)
            {
                _allMods.Add(new ModItemViewModel(mod, DeleteModAsync));
            }

            ApplyFilter();
            StatusMessage = _allMods.Count == 0
                ? "No mods found in current workshop path"
                : $"Loaded {_allMods.Count} mods";
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    [RelayCommand]
    private async Task CleanDuplicateModsAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
        {
            StatusMessage = "Workshop path not set";
            return;
        }

        IsBusy = true;
        try
        {
            var current = await _scanner.ScanAsync(ActiveWorkshopPath);
            var cleanup = await _renameEngine.CleanupDuplicateFoldersAsync(
                ActiveWorkshopPath,
                current,
                (line, token) => _logger.LogAsync(line, token));

            await RefreshModsAsync();
            RefreshWorkshopFolderCountInStatus();

            if (cleanup.GroupsWithDuplicates == 0)
            {
                StatusMessage = "No duplicate mod folders found";
                return;
            }

            StatusMessage =
                $"Cleaned {cleanup.GroupsWithDuplicates} mod IDs (removed {cleanup.RemovedFolders}, renamed {cleanup.RenamedFolders}, failed {cleanup.FailedOperations})";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshWorkshopFolderCountInStatus()
    {
        if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
        {
            return;
        }

        var count = CountWorkshopFolders(ActiveWorkshopPath);
        if (SteamStatusPath.StartsWith("Auto-detected via Steam libraryfolders.vdf", StringComparison.OrdinalIgnoreCase))
        {
            SteamStatusPath = $"Auto-detected via Steam libraryfolders.vdf ({count} folders)";
            return;
        }

        if (SteamStatusPath.StartsWith("Manual override active", StringComparison.OrdinalIgnoreCase))
        {
            SteamStatusPath = $"Manual override active ({count} folders)";
        }
    }

    private static int CountWorkshopFolders(string workshopPath)
    {
        if (string.IsNullOrWhiteSpace(workshopPath) || !Directory.Exists(workshopPath))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateDirectories(workshopPath).Count();
        }
        catch
        {
            return 0;
        }
    }

    [RelayCommand]
    private void EnableAll()
    {
        foreach (var mod in _allMods)
        {
            mod.IsEnabled = true;
        }

        StatusMessage = "Enabled all mods in current working set";
    }

    [RelayCommand]
    private void DisableAll()
    {
        foreach (var mod in _allMods)
        {
            mod.IsEnabled = false;
        }

        StatusMessage = "Disabled all mods in current working set";
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        var name = ProfileNameInput.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Profile name is required";
            return;
        }

        var enabled = _allMods.Where(m => m.IsEnabled).Select(m => m.WorkshopId).Distinct().ToList();
        var profile = new ModProfile
        {
            Name = name,
            EnabledMods = enabled,
            IncludedMods = enabled.Distinct(StringComparer.Ordinal).ToList(),
            CreatedAt = DateTime.UtcNow,
            Notes = ProfileNotesInput.Trim()
        };

        await _profileService.SaveProfileAsync(profile);
        await _logger.LogAsync($"Saved profile '{name}' with {enabled.Count} enabled mods");
        await LoadProfilesAsync();
        StatusMessage = $"Profile '{name}' saved";
    }

    [RelayCommand]
    private async Task OpenAddProfileDialogAsync()
    {
        var window = GetMainWindow();
        if (window is null)
        {
            StatusMessage = "Add profile dialog is unavailable";
            return;
        }

        var dialog = new AddProfileWindow();
        var result = await dialog.ShowDialog<AddProfileDialogResult?>(window);
        if (result is null)
        {
            return;
        }

        ProfileNameInput = result.Name;
        ProfileNotesInput = result.Notes;

        foreach (var mod in _allMods)
        {
            mod.IsEnabled = result.ActivateAllMods;
        }

        await SaveProfileAsync();
    }

    [RelayCommand]
    private async Task ExportSelectedProfilePackageAsync()
    {
        if (SelectedProfile is null)
        {
            StatusMessage = "No profile selected to export";
            return;
        }

        var saveFile = await PickSavePackageFileAsync($"{MakeSafeFileName(SelectedProfile.Name)}-profile-package.json");
        if (saveFile is null)
        {
            return;
        }

        await using var stream = await saveFile.OpenWriteAsync();
        await _profilePackageService.ExportAsync(
            stream,
            SelectedProfile.Name,
            new[] { SelectedProfile.Source });

        await _logger.LogAsync($"Exported profile package '{saveFile.Name}' with 1 profile ('{SelectedProfile.Name}')");
        StatusMessage = $"Exported profile package '{saveFile.Name}'";
    }

    [RelayCommand]
    private async Task ExportAllProfilesPackageAsync()
    {
        var allProfiles = await _profileService.LoadProfilesAsync();
        if (allProfiles.Count == 0)
        {
            StatusMessage = "No profiles available to export";
            return;
        }

        var saveFile = await PickSavePackageFileAsync("all-profiles-package.json");
        if (saveFile is null)
        {
            return;
        }

        await using var stream = await saveFile.OpenWriteAsync();
        await _profilePackageService.ExportAsync(
            stream,
            "All Profiles",
            allProfiles);

        await _logger.LogAsync($"Exported profile package '{saveFile.Name}' with {allProfiles.Count} profiles");
        StatusMessage = $"Exported {allProfiles.Count} profiles to '{saveFile.Name}'";
    }

    [RelayCommand]
    private async Task ImportProfilePackageAsync()
    {
        var openFile = await PickPackageImportFileAsync();
        if (openFile is null)
        {
            return;
        }

        var existingProfiles = await _profileService.LoadProfilesAsync();
        await using var stream = await openFile.OpenReadAsync();

        ProfilePackageImportResult importResult;
        try
        {
            importResult = await _profilePackageService.ImportAsync(
                stream,
                existingProfiles,
                ParseConflictPolicy(SelectedImportConflictPolicy));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            StatusMessage = $"Import failed: {ex.Message}";
            await _logger.LogAsync($"Import failed for '{openFile.Name}': {ex.Message}");
            return;
        }

        foreach (var profile in importResult.ImportedProfiles)
        {
            await _profileService.SaveProfileAsync(profile);
        }

        await LoadProfilesAsync();

        var importedOrderedIds = importResult.ImportedProfiles
            .SelectMany(profile => profile.EnabledMods)
            .Where(IsNumericWorkshopId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (importedOrderedIds.Count > 0 &&
            !string.IsNullOrWhiteSpace(ActiveWorkshopPath) &&
            ActiveWorkshopPath != "Not detected")
        {
            var installedIds = await GetInstalledWorkshopIdsAsync();
            var missingAfterImport = BuildMissingWorkshopIdList(importedOrderedIds, installedIds);
            await SetMissingModsAsync(missingAfterImport, "imported profiles");
        }

        await _logger.LogAsync(
            $"Import package '{openFile.Name}' result: imported={importResult.ImportedCount}, renamed={importResult.RenamedCount}, overwritten={importResult.OverwrittenCount}, skipped={importResult.SkippedCount}, invalidProfiles={importResult.InvalidProfileCount}, removedInvalidWorkshopIds={importResult.RemovedInvalidWorkshopIdsCount}");

        StatusMessage =
            $"Import finished: imported {importResult.ImportedCount}, renamed {importResult.RenamedCount}, overwritten {importResult.OverwrittenCount}, skipped {importResult.SkippedCount}";
    }

    [RelayCommand]
    private async Task DeleteSelectedProfileAsync(IList? selectedItems)
    {
        var selectedProfiles = selectedItems?
            .OfType<ProfileItemViewModel>()
            .Distinct()
            .ToList() ?? new List<ProfileItemViewModel>();

        if (selectedProfiles.Count == 0 && SelectedProfile is not null)
        {
            selectedProfiles.Add(SelectedProfile);
        }

        if (selectedProfiles.Count == 0)
        {
            StatusMessage = "No profile selected";
            return;
        }

        var deletedCount = 0;
        foreach (var profile in selectedProfiles)
        {
            await _profileService.DeleteProfileAsync(profile.Name);
            await _logger.LogAsync($"Deleted profile '{profile.Name}'");
            deletedCount++;
        }

        await LoadProfilesAsync();
        StatusMessage = deletedCount == 1
            ? $"Deleted profile '{selectedProfiles[0].Name}'"
            : $"Deleted {deletedCount} profiles";
    }

    [RelayCommand]
    private async Task ApplySelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProfile is null)
        {
            StatusMessage = "No profile selected";
            return;
        }

        var requiredOrderedIds = SelectedProfile.EnabledMods
            .Where(IsNumericWorkshopId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await ApplyEnabledSetAsync(
            requiredOrderedIds.ToHashSet(StringComparer.Ordinal),
            requiredOrderedIds,
            $"profile '{SelectedProfile.Name}'",
            cancellationToken);
        StatusMessage = $"Applied profile '{SelectedProfile.Name}'";
    }

    [RelayCommand]
    private async Task ApplyCurrentTogglesAsync()
    {
        var requiredOrderedIds = _allMods
            .Where(m => m.IsEnabled && IsNumericWorkshopId(m.WorkshopId))
            .Select(m => m.WorkshopId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await ApplyEnabledSetAsync(
            requiredOrderedIds.ToHashSet(StringComparer.Ordinal),
            requiredOrderedIds,
            "current toggles");
        StatusMessage = "Applied current toggle state";
    }

    [RelayCommand]
    private async Task RestoreLastStateAsync()
    {
        var snapshot = await _backupService.LoadLastSnapshotAsync();
        if (snapshot is null)
        {
            StatusMessage = "No snapshot found to restore";
            return;
        }

        var enabledSet = snapshot.Folders
            .Select(name => WorkshopScanner.TryGetWorkshopId(name, out var id, out var enabled) ? (id, enabled) : default)
            .Where(tuple => !string.IsNullOrWhiteSpace(tuple.id))
            .Where(tuple => tuple.enabled)
            .Select(tuple => tuple.id)
            .ToHashSet(StringComparer.Ordinal);

        await ApplyEnabledSetAsync(enabledSet, enabledSet.OrderBy(id => id, StringComparer.Ordinal).ToList(), "restored snapshot");
        StatusMessage = "Last snapshot restored";
    }

    [RelayCommand]
    private async Task OpenNextMissingModAsync()
    {
        if (MissingWorkshopIds.Count == 0)
        {
            StatusMessage = "No missing workshop IDs";
            return;
        }

        if (NextMissingModIndex >= MissingWorkshopIds.Count)
        {
            NextMissingModIndex = 0;
        }

        var workshopId = MissingWorkshopIds[NextMissingModIndex];
        var opened = OpenSteamWorkshopPage(workshopId);
        var currentIndex = NextMissingModIndex + 1;
        NextMissingModIndex++;

        await _logger.LogAsync($"Opened missing mod id {workshopId} ({currentIndex}/{MissingWorkshopIds.Count})");
        StatusMessage = opened
            ? $"Opened missing mod {workshopId} ({currentIndex}/{MissingWorkshopIds.Count})"
            : $"Failed to open missing mod {workshopId}";
    }

    [RelayCommand]
    private async Task CopyAllMissingIdsAsync()
    {
        if (MissingWorkshopIds.Count == 0)
        {
            StatusMessage = "No missing workshop IDs to copy";
            return;
        }

        var topLevel = GetMainWindow();
        if (topLevel?.Clipboard is null)
        {
            StatusMessage = "Clipboard is not available";
            return;
        }

        var text = string.Join(Environment.NewLine, MissingWorkshopIds);
        await topLevel.Clipboard.SetTextAsync(text);
        StatusMessage = $"Copied {MissingWorkshopIds.Count} missing IDs";
    }

    [RelayCommand]
    private async Task RescanMissingModsAsync()
    {
        await RefreshModsAsync();

        if (_lastRequiredOrderedIds.Count == 0)
        {
            StatusMessage = "Rescan complete (no active missing-mod context)";
            return;
        }

        var installedIds = await GetInstalledWorkshopIdsAsync();
        var remainingMissingIds = BuildMissingWorkshopIdList(_lastRequiredOrderedIds, installedIds);
        await SetMissingModsAsync(remainingMissingIds, _lastApplyContext);
        await _logger.LogAsync($"Rescan complete. Remaining missing IDs: {string.Join(", ", remainingMissingIds)}");
        StatusMessage = remainingMissingIds.Count == 0
            ? "All required mods are now installed"
            : $"{remainingMissingIds.Count} required mods still missing";
    }

    [RelayCommand]
    private async Task ApplyAgainAsync()
    {
        if (_lastRequestedEnabledSet is null || _lastRequestedEnabledSet.Count == 0)
        {
            StatusMessage = "No previous apply context available";
            return;
        }

        await ApplyEnabledSetAsync(_lastRequestedEnabledSet, _lastRequiredOrderedIds, _lastApplyContext);
        StatusMessage = "Apply retried";
    }

    [RelayCommand]
    private async Task DeleteModAsync(ModItemViewModel? mod)
    {
        await DeleteModInternalAsync(mod, OpenSteamPageAfterDelete);
    }

    public void OpenModWorkshopPage(ModItemViewModel? mod)
    {
        if (mod is null || string.IsNullOrWhiteSpace(mod.WorkshopId))
        {
            return;
        }

        OpenSteamWorkshopPage(mod.WorkshopId);
    }

    [RelayCommand]
    private void SelectAllFiltered()
    {
        foreach (var mod in FilteredMods)
        {
            mod.IsSelected = true;
        }

        StatusMessage = $"Selected {FilteredMods.Count} filtered mods";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var mod in _allMods)
        {
            mod.IsSelected = false;
        }

        StatusMessage = "Selection cleared";
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = _allMods.Where(m => m.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No selected mods to delete";
            return;
        }

        IsBusy = true;
        try
        {
            var deletedCount = 0;
            foreach (var mod in selected)
            {
                var ok = await DeleteModInternalAsync(mod, openSteamPage: false, refreshAfterDelete: false);
                if (ok)
                {
                    deletedCount++;
                }
            }

            await RefreshModsAsync();
            StatusMessage = $"Deleted {deletedCount}/{selected.Count} selected mods";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> DeleteModInternalAsync(ModItemViewModel? mod, bool openSteamPage, bool refreshAfterDelete = true)
    {
        if (mod is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
        {
            StatusMessage = "Workshop path not set";
            return false;
        }

        var folderPath = Path.Combine(ActiveWorkshopPath, mod.FolderName);
        if (!Directory.Exists(folderPath))
        {
            folderPath = mod.Source.FullPath;
        }

        if (!Directory.Exists(folderPath))
        {
            StatusMessage = $"Folder not found for {mod.WorkshopId}";
            return false;
        }

        var deleted = await TryDeleteDirectoryWithRetryAsync(folderPath);
        if (!deleted)
        {
            StatusMessage = $"Failed to delete mod {mod.WorkshopId}";
            await _logger.LogAsync($"Failed to delete folder {folderPath}");
            return false;
        }

        await _logger.LogAsync($"Deleted mod folder: {folderPath}");
        StatusMessage = $"Deleted mod {mod.WorkshopId}";

        if (openSteamPage)
        {
            OpenSteamWorkshopPage(mod.WorkshopId);
        }

        if (refreshAfterDelete)
        {
            await RefreshModsAsync();
        }

        return true;
    }

    private async Task InitializeAsync()
    {
        await LoadSettingsAsync();
        await DetectPathsAsync();
        await LoadProfilesAsync();
        await LoadShellDataAsync();
        _startupInitializationComplete = true;
        QueueStartupUpdateCheck();
    }

    private void QueueStartupUpdateCheck()
    {
        if (_startupUpdateCheckQueued)
        {
            return;
        }

        _startupUpdateCheckQueued = true;
        _ = RunStartupUpdateCheckAsync();
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            var waitUntil = DateTime.UtcNow.AddSeconds(30);
            while (IsBusy && DateTime.UtcNow < waitUntil)
            {
                await Task.Delay(250);
            }

            await Task.Delay(750);
            await CheckForUpdatesAsync();
        }
        catch
        {
            // Ignore startup update scheduling failures.
        }
    }

    private async Task LoadProfilesAsync()
    {
        var items = await _profileService.LoadProfilesAsync();
        Profiles = new ObservableCollection<ProfileItemViewModel>(items.Select(p => new ProfileItemViewModel(p)));
        ApplyProfileFilter();

        if (Profiles.Count > 0)
        {
            SelectedProfile = Profiles[0];
        }

        if (ProfileUnderEdit is not null)
        {
            ProfileUnderEdit = Profiles.FirstOrDefault(p => string.Equals(p.Name, ProfileUnderEdit.Name, StringComparison.Ordinal));
        }
    }

    private async Task ApplyEnabledSetAsync(
        IReadOnlySet<string> enabledSet,
        IReadOnlyList<string>? requiredOrderedIds = null,
        string applyContext = "apply",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
        {
            StatusMessage = "Workshop path not set";
            return;
        }

        var normalizedEnabledSet = enabledSet
            .Where(IsNumericWorkshopId)
            .ToHashSet(StringComparer.Ordinal);
        var requiredIds = requiredOrderedIds?
            .Where(IsNumericWorkshopId)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? normalizedEnabledSet.OrderBy(id => id, StringComparer.Ordinal).ToList();

        _lastRequestedEnabledSet = normalizedEnabledSet;
        _lastRequiredOrderedIds = requiredIds;
        _lastApplyContext = applyContext;
        OnPropertyChanged(nameof(CanApplyAgain));

        cancellationToken.ThrowIfCancellationRequested();
        var installedIds = await GetInstalledWorkshopIdsAsync(cancellationToken);
        var missingIdsBeforeApply = BuildMissingWorkshopIdList(requiredIds, installedIds);
        await SetMissingModsAsync(missingIdsBeforeApply, applyContext);

        IsBusy = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _backupService.CreateSnapshotAsync(ActiveWorkshopPath, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var requestedEnabledSet = await BuildRequestedEnabledSetWithCwbInferenceAsync(normalizedEnabledSet, cancellationToken);
            var dependencyResult = await _dependencyResolver.ResolveAsync(ActiveWorkshopPath, requestedEnabledSet, cancellationToken);
            var resolvedEnabledSet = dependencyResult.EnabledWorkshopIds.ToHashSet(StringComparer.Ordinal);

            if (dependencyResult.AutoEnabledDependencyIds.Count > 0)
            {
                await _logger.LogAsync(
                    $"Auto-enabled dependency ids: {string.Join(", ", dependencyResult.AutoEnabledDependencyIds)}");
            }

            if (dependencyResult.MissingDependencyIds.Count > 0)
            {
                await _logger.LogAsync(
                    $"Dependency ids missing locally (could not auto-enable): {string.Join(", ", dependencyResult.MissingDependencyIds)}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var cwbDiscovery = await _cwbLoadItemsService.DiscoverPacksAsync(ActiveWorkshopPath, cancellationToken);
            var current = await _scanner.ScanAsync(ActiveWorkshopPath, cancellationToken);
            foreach (var mod in current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (WorkshopScanner.TryGetWorkshopId(mod.FolderName, out _, out var enabledByFolderName))
                {
                    mod.IsEnabled = enabledByFolderName;
                }
            }

            var cwbAlwaysEnabledByName = BuildCwbPackFolderToggleExclusions(cwbDiscovery, current);
            if (cwbAlwaysEnabledByName.Count > 0)
            {
                await _logger.LogAsync(
                    $"CWB packs forced enabled (name-based): {string.Join(", ", cwbAlwaysEnabledByName.OrderBy(id => id, StringComparer.Ordinal))}");
            }

            var enabledForFolderState = resolvedEnabledSet.ToHashSet(StringComparer.Ordinal);
            foreach (var cwbPackId in cwbDiscovery.PackWorkshopIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                enabledForFolderState.Add(cwbPackId);
            }

            // Keep excluded CWB add-ons enabled in both folder state and loaditems sync.
            foreach (var workshopId in cwbAlwaysEnabledByName)
            {
                cancellationToken.ThrowIfCancellationRequested();
                enabledForFolderState.Add(workshopId);
                resolvedEnabledSet.Add(workshopId);
            }

            var usesCustomWeaponsBase = resolvedEnabledSet.Contains(CwbLoadItemsService.CustomWeaponsBaseWorkshopId);
            var changes = 0;
            if (usesCustomWeaponsBase)
            {
                var forceEnabledFolderIds = current
                    .Select(mod => mod.WorkshopId)
                    .Where(IsNumericWorkshopId)
                    .ToHashSet(StringComparer.Ordinal);

                changes = await _renameEngine.ApplyEnabledSetAsync(
                    ActiveWorkshopPath,
                    current,
                    forceEnabledFolderIds,
                    disableUnselectedMods: true,
                    (line, token) => _logger.LogAsync(line, token),
                    cancellationToken);
            }
            else
            {
                changes = await _renameEngine.ApplyEnabledSetAsync(
                    ActiveWorkshopPath,
                    current,
                    enabledForFolderState,
                    disableUnselectedMods: true,
                    (line, token) => _logger.LogAsync(line, token),
                    cancellationToken);
            }

            await _logger.LogAsync($"Apply finished with {changes} rename operations");
            var cwbSyncResult = await _cwbLoadItemsService.SyncAsync(
                ActiveWorkshopPath,
                cwbDiscovery,
                resolvedEnabledSet,
                cancellationToken);
            if (cwbSyncResult.Success)
            {
                await _logger.LogAsync(cwbSyncResult.Message);
            }
            else
            {
                await _logger.LogAsync($"CWB loaditems sync skipped: {cwbSyncResult.Message}");
            }

            var loadOnStartEnabledIds = resolvedEnabledSet.ToHashSet(StringComparer.Ordinal);

            if (IsLaunchingGame)
            {
                await PrimeModManagerBeforeLoadOnStartSyncAsync();
            }

            var syncResult = await _loadOnStartSyncService.SyncAsync(loadOnStartEnabledIds, cancellationToken);
            if (syncResult.Success)
            {
                await _logger.LogAsync($"{syncResult.Message} Enabled workshop ids: {loadOnStartEnabledIds.Count}");
            }
            else
            {
                await _logger.LogAsync($"Load On Start sync skipped: {syncResult.Message}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var applied = await _scanner.ScanAsync(ActiveWorkshopPath, cancellationToken);
            var installedAfterApply = applied
                .Select(mod => mod.WorkshopId)
                .Where(IsNumericWorkshopId)
                .ToHashSet(StringComparer.Ordinal);
            var enabledAfterApply = applied
                .Where(mod => mod.IsEnabled)
                .Select(mod => mod.WorkshopId)
                .Where(IsNumericWorkshopId)
                .ToHashSet(StringComparer.Ordinal);

            var missingAfterApply = BuildMissingWorkshopIdList(requiredIds, installedAfterApply);
            await SetMissingModsAsync(missingAfterApply, applyContext);
            await _logger.LogAsync(
                $"Apply result ({applyContext}): requested={requiredIds.Count}, resolved={loadOnStartEnabledIds.Count}, enabledAfterApply={enabledAfterApply.Count}, missingAfterApply={missingAfterApply.Count}");

            cancellationToken.ThrowIfCancellationRequested();
            await RefreshModsAsync();
            ApplyEnabledSelectionToToggles(loadOnStartEnabledIds);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ManualUpdateAsync()
    {
        if (IsCheckingForUpdates || IsDownloadingUpdate)
        {
            return;
        }

        IsCheckingForUpdates = true;
        try
        {
            UpdateStatusText = "Checking latest release for manual update...";
            var latest = await TryGetLatestReleaseAsync();
            if (latest is null)
            {
                UpdateStatusText = "No GitHub releases published yet.";
                return;
            }

            LatestReleaseVersion = string.IsNullOrWhiteSpace(latest.Value.TagName) ? "Unknown" : latest.Value.TagName;
            LatestReleaseUrl = string.IsNullOrWhiteSpace(latest.Value.HtmlUrl) ? ReleasesPageUrl : latest.Value.HtmlUrl;
            LatestInstallerUrl = latest.Value.InstallerUrl;
            LatestInstallerFileName = latest.Value.InstallerName;

            if (string.IsNullOrWhiteSpace(LatestInstallerUrl))
            {
                UpdateStatusText = "Latest release has no installer asset (.exe).";
                return;
            }

            HasUpdateAvailable = IsUpdateAvailable(LatestReleaseVersion, CurrentAppVersion, CurrentVersionId);
            CanAutoInstallUpdate = HasUpdateAvailable;
            await DownloadAndInstallUpdateAsync();
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            UpdateStatusText = $"Manual update check failed ({(int)ex.StatusCode.Value})";
        }
        catch
        {
            UpdateStatusText = "Manual update failed. Please try again.";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task LoadDowngradeReleasesAsync()
    {
        if (IsLoadingDowngradeReleases || IsCheckingForUpdates || IsDownloadingUpdate)
        {
            return;
        }

        IsLoadingDowngradeReleases = true;
        try
        {
            UpdateStatusText = "Loading downgrade versions...";
            var releases = await TryGetReleasesAsync(DowngradeReleasePageSize);
            var downgradeOptions = releases
                .Where(release => !string.IsNullOrWhiteSpace(release.InstallerUrl))
                .Select(release => new
                {
                    Release = release,
                    Version = ParseVersion(release.TagName)
                })
                .Where(item => item.Version is not null && item.Version < CurrentAppVersion)
                .OrderByDescending(item => item.Version)
                .Select(item => new ReleaseInstallOption(
                    item.Release.TagName,
                    $"{item.Release.TagName} ({item.Release.InstallerName})",
                    item.Release.InstallerUrl,
                    item.Release.InstallerName,
                    item.Release.HtmlUrl))
                .ToList();

            DowngradeReleaseOptions = new ObservableCollection<ReleaseInstallOption>(downgradeOptions);
            SelectedDowngradeRelease = DowngradeReleaseOptions.FirstOrDefault();
            UpdateStatusText = DowngradeReleaseOptions.Count == 0
                ? "No downgrade versions with installer assets were found."
                : $"Loaded {DowngradeReleaseOptions.Count} downgrade version(s).";
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            UpdateStatusText = $"Failed to load downgrade versions ({(int)ex.StatusCode.Value})";
        }
        catch
        {
            UpdateStatusText = "Failed to load downgrade versions.";
        }
        finally
        {
            IsLoadingDowngradeReleases = false;
        }
    }

    [RelayCommand]
    private async Task InstallSelectedDowngradeAsync()
    {
        if (IsDownloadingUpdate || SelectedDowngradeRelease is null)
        {
            return;
        }

        AutoInstallUpdates = false;
        HasUpdateAvailable = false;
        CanAutoInstallUpdate = false;
        LatestReleaseVersion = SelectedDowngradeRelease.TagName;
        LatestReleaseUrl = SelectedDowngradeRelease.HtmlUrl;
        LatestInstallerUrl = SelectedDowngradeRelease.InstallerUrl;
        LatestInstallerFileName = SelectedDowngradeRelease.InstallerName;
        await DownloadAndInstallUpdateAsync();
    }

    private async Task PrimeModManagerBeforeLoadOnStartSyncAsync()
    {
        var modManagerExePath = ResolveModManagerExePath();
        if (string.IsNullOrWhiteSpace(modManagerExePath) || !File.Exists(modManagerExePath))
        {
            return;
        }

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = modManagerExePath,
                WorkingDirectory = Path.GetDirectoryName(modManagerExePath) ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Minimized
            });

            if (process is null)
            {
                return;
            }

            await _logger.LogAsync($"Priming Mod Manager before Load on Start sync (PID {process.Id}).");
            await Task.Delay(1200);

            if (process.HasExited)
            {
                await _logger.LogAsync("Closed priming Mod Manager process.");
                return;
            }

            if (process.CloseMainWindow())
            {
                process.WaitForExit(1500);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }

            await _logger.LogAsync("Closed priming Mod Manager process.");
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Mod Manager pre-sync prime skipped: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private Task OnWorkshopChangedAsync()
    {
        Dispatcher.UIThread.Post(() => _ = HandleWorkshopChangedAsync());
        return Task.CompletedTask;
    }

    private async Task HandleWorkshopChangedAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
        {
            return;
        }

        var cooldownElapsed = DateTime.UtcNow - _lastAutoCleanupUtc > TimeSpan.FromSeconds(2);
        if (cooldownElapsed && await _autoCleanupLock.WaitAsync(0))
        {
            try
            {
                var current = await _scanner.ScanAsync(ActiveWorkshopPath);
                var cleanup = await _renameEngine.CleanupDuplicateFoldersAsync(
                    ActiveWorkshopPath,
                    current,
                    (line, token) => _logger.LogAsync(line, token));

                if (cleanup.GroupsWithDuplicates > 0)
                {
                    _lastAutoCleanupUtc = DateTime.UtcNow;
                    await _logger.LogAsync(
                        $"Auto duplicate cleanup after workshop change: groups={cleanup.GroupsWithDuplicates}, removed={cleanup.RemovedFolders}, renamed={cleanup.RenamedFolders}, failed={cleanup.FailedOperations}");
                    StatusMessage =
                        $"Auto-cleaned dupes: groups {cleanup.GroupsWithDuplicates}, removed {cleanup.RemovedFolders}, renamed {cleanup.RenamedFolders}, failed {cleanup.FailedOperations}";
                }
            }
            finally
            {
                _autoCleanupLock.Release();
            }
        }

        await RefreshModsAsync();
        RefreshWorkshopFolderCountInStatus();
    }

    private void OnModsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyModStatsChanged();

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<ModItemViewModel>())
            {
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ModItemViewModel.IsEnabled))
                    {
                        NotifyModStatsChanged();
                        if (_isLoadingProfileSelectionIntoToggles)
                        {
                            return;
                        }

                        if (IsAddModSelectionMode && ProfileUnderEdit is not null)
                        {
                            StatusMessage = "Profile selection changed (saving...)";
                            QueueAddModeProfileSave();
                            return;
                        }

                        StatusMessage = "Pending changes";
                        if (!_isProjectingDependencyStates)
                        {
                            QueueLiveDependencyProjection();
                        }
                    }
                };
            }
        }
    }

    private void QueueLiveDependencyProjection()
    {
        if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
        {
            return;
        }

        _dependencyPreviewCts?.Cancel();
        _dependencyPreviewCts?.Dispose();
        _dependencyPreviewCts = new CancellationTokenSource();
        _ = ProjectDependenciesLiveAsync(_dependencyPreviewCts.Token);
    }

    private void QueueAddModeProfileSave()
    {
        _addModeProfileSaveCts?.Cancel();
        _addModeProfileSaveCts?.Dispose();
        _addModeProfileSaveCts = new CancellationTokenSource();
        _ = SaveAddModeSelectionToProfileAsync(_addModeProfileSaveCts.Token);
    }

    private void CancelAddModeProfileSave()
    {
        _addModeProfileSaveCts?.Cancel();
        _addModeProfileSaveCts?.Dispose();
        _addModeProfileSaveCts = null;
    }

    private async Task SaveAddModeSelectionToProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(320, cancellationToken);
            var profile = ProfileUnderEdit;
            if (profile is null || !IsAddModSelectionMode)
            {
                return;
            }

            var enabled = _allMods
                .Where(m => m.IsEnabled && IsNumericWorkshopId(m.WorkshopId))
                .Select(m => m.WorkshopId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var source = profile.Source;
            var updated = new ModProfile
            {
                Name = source.Name,
                Notes = source.Notes,
                CreatedAt = source.CreatedAt,
                EnabledMods = enabled,
                IncludedMods = enabled.ToList()
            };

            await _profileService.SaveProfileAsync(updated, cancellationToken);
            await LoadProfilesAsync();
            ProfileUnderEdit = Profiles.FirstOrDefault(p => string.Equals(p.Name, source.Name, StringComparison.Ordinal));
            StatusMessage = $"Saved {enabled.Count} selected mods to profile '{source.Name}'";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            StatusMessage = "Failed to save profile selection";
        }
    }

    private async Task ProjectDependenciesLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken);
            await _liveDependencyLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var requestedEnabledSet = await Dispatcher.UIThread.InvokeAsync(() =>
                _allMods
                    .Where(m => m.IsEnabled && IsNumericWorkshopId(m.WorkshopId))
                    .Select(m => m.WorkshopId)
                    .ToHashSet(StringComparer.Ordinal));
            requestedEnabledSet = await BuildRequestedEnabledSetWithCwbInferenceAsync(requestedEnabledSet, cancellationToken);

            var dependencyResult = await _dependencyResolver.ResolveAsync(
                ActiveWorkshopPath,
                requestedEnabledSet,
                cancellationToken);

            var resolvedEnabledSet = dependencyResult.EnabledWorkshopIds.ToHashSet(StringComparer.Ordinal);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var changedCount = 0;
                _isProjectingDependencyStates = true;
                try
                {
                    foreach (var mod in _allMods)
                    {
                        var shouldBeEnabled = resolvedEnabledSet.Contains(mod.WorkshopId);
                        if (mod.IsEnabled != shouldBeEnabled)
                        {
                            mod.IsEnabled = shouldBeEnabled;
                            changedCount++;
                        }
                    }
                }
                finally
                {
                    _isProjectingDependencyStates = false;
                }

                if (changedCount > 0)
                {
                    StatusMessage = $"Pending changes ({changedCount} dependency toggle updates)";
                }
                else if (dependencyResult.MissingDependencyIds.Count > 0)
                {
                    StatusMessage = $"Pending changes (missing dependencies: {dependencyResult.MissingDependencyIds.Count})";
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = "Pending changes (live dependency check failed)";
            });
        }
        finally
        {
            _liveDependencyLock.Release();
        }
    }

    private async Task<HashSet<string>> BuildRequestedEnabledSetWithCwbInferenceAsync(
        IEnumerable<string> enabledIds,
        CancellationToken cancellationToken = default)
    {
        var requestedEnabledSet = enabledIds
            .Where(IsNumericWorkshopId)
            .ToHashSet(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
        {
            return requestedEnabledSet;
        }

        var cwbDiscovery = await _cwbLoadItemsService.DiscoverPacksAsync(ActiveWorkshopPath, cancellationToken);
        if (cwbDiscovery.PackWorkshopIds.Count == 0)
        {
            return requestedEnabledSet;
        }

        if (requestedEnabledSet.Overlaps(cwbDiscovery.PackWorkshopIds))
        {
            requestedEnabledSet.Add(CwbLoadItemsService.CustomWeaponsBaseWorkshopId);
        }

        return requestedEnabledSet;
    }

    private void ApplyEnabledSelectionToToggles(IReadOnlySet<string> enabledWorkshopIds)
    {
        _isLoadingProfileSelectionIntoToggles = true;
        try
        {
            foreach (var mod in _allMods)
            {
                var shouldBeEnabled = enabledWorkshopIds.Contains(mod.WorkshopId);
                if (mod.IsEnabled != shouldBeEnabled)
                {
                    mod.IsEnabled = shouldBeEnabled;
                }
            }
        }
        finally
        {
            _isLoadingProfileSelectionIntoToggles = false;
        }

        NotifyModStatsChanged();
    }

    private static HashSet<string> BuildCwbPackFolderToggleExclusions(
        CwbPackDiscoveryResult discovery,
        IReadOnlyList<WorkshopMod>? current)
    {
        var excludedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in discovery.PackNamesByWorkshopId)
        {
            if (pair.Value.Any(IsExcludedCwbPackName))
            {
                excludedIds.Add(pair.Key);
            }
        }

        if (current is null || current.Count == 0)
        {
            return excludedIds;
        }

        foreach (var mod in current)
        {
            if (!discovery.PackWorkshopIds.Contains(mod.WorkshopId))
            {
                continue;
            }

            if (IsExcludedCwbPackName(mod.DisplayName))
            {
                excludedIds.Add(mod.WorkshopId);
            }
        }

        return excludedIds;
    }

    private static bool IsExcludedCwbPackName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return CwbPackFolderToggleExcludeNameTokens.Any(token =>
            value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static ProfileImportConflictPolicy ParseConflictPolicy(string? value)
    {
        if (string.Equals(value, "Overwrite", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileImportConflictPolicy.Overwrite;
        }

        if (string.Equals(value, "Skip", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileImportConflictPolicy.Skip;
        }

        return ProfileImportConflictPolicy.Rename;
    }

    private static List<string> BuildMissingWorkshopIdList(
        IEnumerable<string> requiredOrderedIds,
        IReadOnlySet<string> installedIds)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var workshopId in requiredOrderedIds)
        {
            if (!IsNumericWorkshopId(workshopId) || !seen.Add(workshopId))
            {
                continue;
            }

            if (!installedIds.Contains(workshopId))
            {
                missing.Add(workshopId);
            }
        }

        return missing;
    }

    private async Task<HashSet<string>> GetInstalledWorkshopIdsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ActiveWorkshopPath) || ActiveWorkshopPath == "Not detected")
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var scanned = await _scanner.ScanAsync(ActiveWorkshopPath, cancellationToken);
        return scanned
            .Select(mod => mod.WorkshopId)
            .Where(IsNumericWorkshopId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private Task SetMissingModsAsync(IReadOnlyList<string> missingIds, string context)
    {
        MissingWorkshopIds = new ObservableCollection<string>(missingIds);
        MissingModsContext = context;
        NextMissingModIndex = 0;
        return Task.CompletedTask;
    }

    private static string MakeSafeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "profiles" : safe;
    }

    private Window? GetMainWindow()
    {
        return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    }

    private async Task<IStorageFile?> PickPackageImportFileAsync()
    {
        var window = GetMainWindow();
        if (window is null)
        {
            StatusMessage = "File picker is unavailable";
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Profile Package",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON files")
                {
                    Patterns = new[] { "*.json" }
                }
            }
        });

        return files.Count == 0 ? null : files[0];
    }

    private async Task<IStorageFile?> PickSavePackageFileAsync(string suggestedFileName)
    {
        var window = GetMainWindow();
        if (window is null)
        {
            StatusMessage = "File picker is unavailable";
            return null;
        }

        return await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Profile Package",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON files")
                {
                    Patterns = new[] { "*.json" }
                }
            }
        });
    }

    private void ApplyFilter()
    {
        var query = SearchQuery?.Trim() ?? string.Empty;
        IEnumerable<ModItemViewModel> working = _allMods;

        if (!string.IsNullOrWhiteSpace(query))
        {
            working = working.Where(m =>
                m.ModName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                m.WorkshopId.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        FilteredMods = new ObservableCollection<ModItemViewModel>(working.OrderBy(m => m.ModName, StringComparer.OrdinalIgnoreCase));
        NotifyModStatsChanged();
    }

    private void ApplyProfileFilter()
    {
        var query = ProfileSearchQuery?.Trim() ?? string.Empty;
        IEnumerable<ProfileItemViewModel> working = Profiles;

        if (!string.IsNullOrWhiteSpace(query))
        {
            working = working.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Notes.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        FilteredProfiles = new ObservableCollection<ProfileItemViewModel>(
            working.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<bool> TryDeleteDirectoryWithRetryAsync(string folderPath)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(folderPath, recursive: true);
                return true;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(150 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(150 * attempt);
            }
        }

        return false;
    }

    private static bool OpenSteamWorkshopPage(string workshopId)
    {
        var steamUrl = $"steam://url/CommunityFilePage/{workshopId}";
        var webUrl = $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamUrl,
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = webUrl,
                    UseShellExecute = true
                });

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void EnsureGitHubHttpDefaults()
    {
        if (GitHubHttpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            GitHubHttpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("VTOLVRWorkshopProfileSwitcher", "1.0"));
        }
    }

    private async Task LoadSettingsAsync()
    {
        _suppressSettingsSave = true;
        try
        {
            var settings = await _settingsService.LoadAsync();

            if (DesignOptions.Contains(settings.SelectedDesign, StringComparer.OrdinalIgnoreCase))
            {
                SelectedDesign = settings.SelectedDesign;
            }

            OpenSteamPageAfterDelete = settings.OpenSteamPageAfterDelete;
            AutoInstallUpdates = settings.AutoInstallUpdates;
            if (VrRuntimeOptions.Contains(settings.VrRuntime, StringComparer.OrdinalIgnoreCase))
            {
                SelectedVrRuntime = settings.VrRuntime;
            }
            else
            {
                SelectedVrRuntime = VrRuntimeSteamVr;
            }
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private void SaveSettingsIfNeeded()
    {
        if (_suppressSettingsSave)
        {
            return;
        }

        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        var settings = new AppSettings
        {
            SelectedDesign = SelectedDesign,
            OpenSteamPageAfterDelete = OpenSteamPageAfterDelete,
            AutoInstallUpdates = AutoInstallUpdates,
            VrRuntime = SelectedVrRuntime
        };

        try
        {
            await _settingsService.SaveAsync(settings);
        }
        catch
        {
            // Ignore settings save failures.
        }
    }

    private static bool IsUpdateAvailable(string? latestTag, Version currentVersion)
    {
        return IsUpdateAvailable(latestTag, currentVersion, NormalizeTag($"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}"));
    }

    private static bool IsUpdateAvailable(string? latestTag, Version currentVersion, string currentVersionId)
    {
        var normalizedTag = NormalizeTag(latestTag);
        if (string.IsNullOrWhiteSpace(normalizedTag))
        {
            return false;
        }

        if (string.Equals(normalizedTag, NormalizeTag(currentVersionId), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var latest = ParseVersion(latestTag);
        if (latest is null)
        {
            // Ignore non-version tags like "Installer" to avoid false update prompts.
            return false;
        }

        return latest > currentVersion;
    }

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var normalized = NormalizeTag(tag);
        return Version.TryParse(normalized, out var parsed) ? parsed : null;
    }

    private string GetVrRuntimeLaunchArgument()
    {
        if (string.Equals(SelectedVrRuntime, VrRuntimeOculus, StringComparison.OrdinalIgnoreCase))
        {
            return "oculus";
        }

        if (string.Equals(SelectedVrRuntime, VrRuntimeOpenXr, StringComparison.OrdinalIgnoreCase))
        {
            return "openxr";
        }

        return string.Empty;
    }

    private static string NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        var normalized = tag.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        if (normalized.StartsWith(".", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        return normalized.Trim();
    }

    private static bool HasSupportedUpdateTagFormat(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var normalized = NormalizeTag(tag);
        return Version.TryParse(normalized, out _);
    }

    private static bool ShouldRunFullInstallerUi(string? latestTag, Version currentVersion)
    {
        var latest = ParseVersion(latestTag);
        if (latest is null)
        {
            return false;
        }

        return latest.Major != currentVersion.Major ||
               latest.Minor != currentVersion.Minor;
    }

    private static string GetCurrentVersionId()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return NormalizeTag(informational);
        }

        var version = assembly.GetName().Version ?? new Version(1, 0, 0, 0);
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static bool IsNumericWorkshopId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.All(char.IsDigit);
    }

    private async Task<(string TagName, string HtmlUrl, string InstallerUrl, string InstallerName)?> TryGetLatestReleaseAsync()
    {
        var latestUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepoName}/releases/latest";
        using var latestResponse = await GitHubHttpClient.GetAsync(latestUrl);

        if (latestResponse.IsSuccessStatusCode)
        {
            return await ReadReleaseAsync(latestResponse);
        }

        if (latestResponse.StatusCode != HttpStatusCode.NotFound)
        {
            throw new HttpRequestException(
                $"GitHub request failed with status {(int)latestResponse.StatusCode}",
                null,
                latestResponse.StatusCode);
        }

        var fallbackReleases = await TryGetReleasesAsync(1);
        if (fallbackReleases.Count == 0)
        {
            return null;
        }

        var first = fallbackReleases[0];
        return (first.TagName, first.HtmlUrl, first.InstallerUrl, first.InstallerName);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> TryGetReleasesAsync(int perPage)
    {
        var listUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepoName}/releases?per_page={perPage}";
        using var listResponse = await GitHubHttpClient.GetAsync(listUrl);
        if (!listResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub request failed with status {(int)listResponse.StatusCode}",
                null,
                listResponse.StatusCode);
        }

        return await ReadReleaseListAsync(listResponse);
    }

    private static async Task<(string TagName, string HtmlUrl, string InstallerUrl, string InstallerName)> ReadReleaseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        var html = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;
        var (installerUrl, installerName) = ReadInstallerAsset(root);
        return (tag ?? string.Empty, html ?? ReleasesPageUrl, installerUrl, installerName);
    }

    private static async Task<IReadOnlyList<ReleaseInfo>> ReadReleaseListAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return Array.Empty<ReleaseInfo>();
        }

        var releases = new List<ReleaseInfo>(root.GetArrayLength());
        foreach (var release in root.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draftEl) &&
                draftEl.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (!HasSupportedUpdateTagFormat(tag))
            {
                continue;
            }

            var html = release.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;
            var (installerUrl, installerName) = ReadInstallerAsset(release);
            releases.Add(new ReleaseInfo(
                tag ?? string.Empty,
                html ?? ReleasesPageUrl,
                installerUrl,
                installerName));
        }

        return releases;
    }

    private static (string InstallerUrl, string InstallerName) ReadInstallerAsset(JsonElement releaseRoot)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, string.Empty);
        }

        var candidates = assets
            .EnumerateArray()
            .Select(asset =>
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty;
                return (Name: name, Url: url);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) &&
                        !string.IsNullOrWhiteSpace(x.Url) &&
                        x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var preferred = candidates.FirstOrDefault(x => x.Name.Contains("setup", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(preferred.Url))
        {
            preferred = candidates[0];
        }

        return (preferred.Url, preferred.Name);
    }

    public sealed record ReleaseInstallOption(
        string TagName,
        string DisplayName,
        string InstallerUrl,
        string InstallerName,
        string HtmlUrl);

    private readonly record struct ReleaseInfo(
        string TagName,
        string HtmlUrl,
        string InstallerUrl,
        string InstallerName);
}

