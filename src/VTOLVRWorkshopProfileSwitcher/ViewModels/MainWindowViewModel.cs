using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
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

namespace VTOLVRWorkshopProfileSwitcher.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const string GitHubOwner = "B1progame";
    private const string GitHubRepoName = "Vtol-Vr-Mod-Profiler";
    private const string ReleasesPageUrl = "https://github.com/B1progame/Vtol-Vr-Mod-Profiler/releases";
    private const string DefaultInstallerAssetName = "VTOLVRWorkshopProfileSwitcher-Setup.exe";
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

    private readonly ObservableCollection<ModItemViewModel> _allMods = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _liveDependencyLock = new(1, 1);
    private readonly SemaphoreSlim _autoCleanupLock = new(1, 1);
    private bool _suppressSettingsSave;
    private bool _isProjectingDependencyStates;
    private CancellationTokenSource? _dependencyPreviewCts;
    private HashSet<string>? _lastRequestedEnabledSet;
    private List<string> _lastRequiredOrderedIds = new();
    private string _lastApplyContext = string.Empty;
    private DateTime _lastAutoCleanupUtc = DateTime.MinValue;

    [ObservableProperty]
    private ObservableCollection<ModItemViewModel> filteredMods = new();

    [ObservableProperty]
    private ObservableCollection<ProfileItemViewModel> profiles = new();

    [ObservableProperty]
    private ProfileItemViewModel? selectedProfile;

    [ObservableProperty]
    private string profileNameInput = string.Empty;

    [ObservableProperty]
    private string profileNotesInput = string.Empty;

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
    private string latestInstallerUrl = string.Empty;

    [ObservableProperty]
    private string latestInstallerFileName = string.Empty;

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
    public int MissingModsCount => MissingWorkshopIds.Count;
    public bool HasMissingMods => MissingModsCount > 0;
    public bool CanApplyAgain => _lastRequestedEnabledSet is not null && _lastRequestedEnabledSet.Count > 0;
    public string AppAuthor => "B1progame";
    public string AppCreatedOn => "2026-02-15";
    public string CurrentVersionText => $"v{CurrentAppVersion.Major}.{CurrentAppVersion.Minor}.{CurrentAppVersion.Build}";
    public string CurrentVersionIdText => CurrentVersionId;

    public MainWindowViewModel()
    {
        _profileService = new ProfileService(_appPaths);
        _profilePackageService = new ProfilePackageService();
        _backupService = new BackupService(_appPaths);
        _settingsService = new AppSettingsService(_appPaths);
        _logger = new AppLogger(_appPaths);

        _allMods.CollectionChanged += OnModsCollectionChanged;
        _watcher.WorkshopChanged += OnWorkshopChangedAsync;
        EnsureGitHubHttpDefaults();

        _ = InitializeAsync();
    }

    public override void Dispose()
    {
        _dependencyPreviewCts?.Cancel();
        _dependencyPreviewCts?.Dispose();
        _watcher.Dispose();
        _autoCleanupLock.Dispose();
        _liveDependencyLock.Dispose();
        _refreshLock.Dispose();
    }

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

    partial void OnMissingWorkshopIdsChanged(ObservableCollection<string> value)
    {
        OnPropertyChanged(nameof(MissingModsCount));
        OnPropertyChanged(nameof(HasMissingMods));
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
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
                ? $"{GitHubRepoName}-Setup.exe"
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

            UpdateStatusText = "Download complete. Starting installer...";
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas"
            });

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch
        {
            UpdateStatusText = "Auto-install failed. Please try again.";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
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
            CreatedAt = DateTime.UtcNow,
            Notes = ProfileNotesInput.Trim()
        };

        await _profileService.SaveProfileAsync(profile);
        await _logger.LogAsync($"Saved profile '{name}' with {enabled.Count} enabled mods");
        await LoadProfilesAsync();
        StatusMessage = $"Profile '{name}' saved";
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
    private async Task DeleteSelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var profileName = SelectedProfile.Name;
        await _profileService.DeleteProfileAsync(profileName);
        await _logger.LogAsync($"Deleted profile '{profileName}'");
        await LoadProfilesAsync();
        StatusMessage = $"Deleted profile '{profileName}'";
    }

    [RelayCommand]
    private async Task ApplySelectedProfileAsync()
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
            $"profile '{SelectedProfile.Name}'");
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
        await CheckForUpdatesAsync();
    }

    private async Task LoadProfilesAsync()
    {
        var items = await _profileService.LoadProfilesAsync();
        Profiles = new ObservableCollection<ProfileItemViewModel>(items.Select(p => new ProfileItemViewModel(p)));

        if (Profiles.Count > 0)
        {
            SelectedProfile = Profiles[0];
        }
    }

    private async Task ApplyEnabledSetAsync(
        IReadOnlySet<string> enabledSet,
        IReadOnlyList<string>? requiredOrderedIds = null,
        string applyContext = "apply")
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

        var installedIds = await GetInstalledWorkshopIdsAsync();
        var missingIdsBeforeApply = BuildMissingWorkshopIdList(requiredIds, installedIds);
        await SetMissingModsAsync(missingIdsBeforeApply, applyContext);

        IsBusy = true;
        try
        {
            await _backupService.CreateSnapshotAsync(ActiveWorkshopPath);

            var requestedEnabledSet = await BuildRequestedEnabledSetWithCwbInferenceAsync(normalizedEnabledSet);
            var dependencyResult = await _dependencyResolver.ResolveAsync(ActiveWorkshopPath, requestedEnabledSet);
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

            var cwbDiscovery = await _cwbLoadItemsService.DiscoverPacksAsync(ActiveWorkshopPath);
            var enabledForFolderState = resolvedEnabledSet.ToHashSet(StringComparer.Ordinal);
            foreach (var cwbPackId in cwbDiscovery.PackWorkshopIds)
            {
                enabledForFolderState.Add(cwbPackId);
            }

            var current = await _scanner.ScanAsync(ActiveWorkshopPath);
            foreach (var mod in current)
            {
                if (WorkshopScanner.TryGetWorkshopId(mod.FolderName, out _, out var enabledByFolderName))
                {
                    mod.IsEnabled = enabledByFolderName;
                }
            }

            var changes = await _renameEngine.ApplyEnabledSetAsync(
                ActiveWorkshopPath,
                current,
                enabledForFolderState,
                (line, token) => _logger.LogAsync(line, token));

            await _logger.LogAsync($"Apply finished with {changes} rename operations");
            var cwbSyncResult = await _cwbLoadItemsService.SyncAsync(
                ActiveWorkshopPath,
                cwbDiscovery,
                resolvedEnabledSet);
            if (cwbSyncResult.Success)
            {
                await _logger.LogAsync(cwbSyncResult.Message);
            }
            else
            {
                await _logger.LogAsync($"CWB loaditems sync skipped: {cwbSyncResult.Message}");
            }

            var applied = await _scanner.ScanAsync(ActiveWorkshopPath);
            var enabledAfterApply = applied
                .Where(mod => mod.IsEnabled)
                .Select(mod => mod.WorkshopId)
                .ToHashSet(StringComparer.Ordinal);

            var syncResult = await _loadOnStartSyncService.SyncAsync(enabledAfterApply);
            if (syncResult.Success)
            {
                await _logger.LogAsync($"{syncResult.Message} Enabled workshop ids: {enabledAfterApply.Count}");
            }
            else
            {
                await _logger.LogAsync($"Load On Start sync skipped: {syncResult.Message}");
            }

            var missingAfterApply = BuildMissingWorkshopIdList(requiredIds, enabledAfterApply);
            await SetMissingModsAsync(missingAfterApply, applyContext);
            await _logger.LogAsync(
                $"Apply result ({applyContext}): requested={requiredIds.Count}, enabledAfterApply={enabledAfterApply.Count}, missingAfterApply={missingAfterApply.Count}");

            await RefreshModsAsync();
        }
        finally
        {
            IsBusy = false;
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
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<ModItemViewModel>())
            {
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ModItemViewModel.IsEnabled))
                    {
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
            AutoInstallUpdates = AutoInstallUpdates
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

        var normalized = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var parsed) ? parsed : null;
    }

    private static string NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        return tag.Trim().TrimStart('v', 'V');
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

        var listUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepoName}/releases?per_page=1";
        using var listResponse = await GitHubHttpClient.GetAsync(listUrl);
        if (!listResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub request failed with status {(int)listResponse.StatusCode}",
                null,
                listResponse.StatusCode);
        }

        await using var listStream = await listResponse.Content.ReadAsStreamAsync();
        using var listDoc = await JsonDocument.ParseAsync(listStream);
        var root = listDoc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return null;
        }

        var first = root[0];
        var tag = first.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        var html = first.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;
        var (installerUrl, installerName) = ReadInstallerAsset(first);
        return (tag ?? string.Empty, html ?? ReleasesPageUrl, installerUrl, installerName);
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
}

