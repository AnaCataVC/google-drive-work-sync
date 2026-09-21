using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;

namespace GoogleDriveWorkSync.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    // Auto-save debounce: cancels the pending save whenever a new property
    // change arrives, then reschedules after 800 ms of inactivity.
    private CancellationTokenSource _autoSaveCts = new();
    private bool _suppressConfirmation;
    private bool _isLoading;

    private readonly IDriveSyncService _driveSyncService;
    private readonly ISyncScheduleService _scheduleService;
    private readonly IUpdateService _updateService;

    // Google Drive Web App settings
    [ObservableProperty]
    private string _driveWebAppUrl = string.Empty;

    [ObservableProperty]
    private string _driveAuthToken = string.Empty;

    [ObservableProperty]
    private string _driveFolderUrl = string.Empty;

    [ObservableProperty]
    private string _driveIncludedExtensions = string.Empty;

    [ObservableProperty]
    private string _driveExcludedExtensions = ".tmp, .log, .exe, .bak, .zip";

    [ObservableProperty]
    private string _driveExcludedFolders = "node_modules, .git, bin, obj, .vs, temp";

    [ObservableProperty]
    private double _driveMaxFileSizeMb = 20;

    [ObservableProperty]
    private bool _driveOnlyModifiedOrNew = true;

    [ObservableProperty]
    private ObservableCollection<SyncSource> _driveSyncSources = new();

    // Claude prefix settings
    [ObservableProperty]
    private string _claudeDestinationPrefix = "claude-md-unversioned";

    [ObservableProperty]
    private string _claudeNoRepoBucketName = "_sin-repo";

    [ObservableProperty]
    private string _claudeConfigBucketName = "_claude-config";

    // Testing connection
    [ObservableProperty]
    private bool _isDriveTesting;

    [ObservableProperty]
    private string _driveConnectionStatus = string.Empty;

    // Scheduler settings
    [ObservableProperty]
    private bool _isScheduleEnabled;

    [ObservableProperty]
    private TimeSpan _scheduledTime = new(18, 0, 0);

    [ObservableProperty]
    private bool _isMonday = true;

    [ObservableProperty]
    private bool _isTuesday = true;

    [ObservableProperty]
    private bool _isWednesday = true;

    [ObservableProperty]
    private bool _isThursday = true;

    [ObservableProperty]
    private bool _isFriday = true;

    [ObservableProperty]
    private bool _isSaturday = false;

    [ObservableProperty]
    private bool _isSunday = false;

    [ObservableProperty]
    private bool _syncClaudeContextAlso = true;

    // Autostart
    [ObservableProperty]
    private bool _isAutostartEnabled;

    // Update
    [ObservableProperty]
    private string _currentVersion = "1.0.0";

    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private bool _showSaveConfirmation;

    [ObservableProperty]
    private string _saveConfirmationMessage = string.Empty;

    public SettingsViewModel(
        IDriveSyncService driveSyncService,
        ISyncScheduleService scheduleService,
        IUpdateService updateService)
    {
        _driveSyncService = driveSyncService;
        _scheduleService = scheduleService;
        _updateService = updateService;

        LoadSettings();
    }

    public void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var s = _driveSyncService.Settings;
            DriveWebAppUrl = s.WebAppUrl;
            DriveAuthToken = s.AuthToken;
            DriveFolderUrl = s.DriveFolderUrl;
            DriveIncludedExtensions = s.IncludedExtensions;
            DriveExcludedExtensions = s.ExcludedExtensions;
            DriveExcludedFolders = s.ExcludedFolders;
            DriveMaxFileSizeMb = s.MaxFileSizeMb;
            DriveOnlyModifiedOrNew = s.OnlyModifiedOrNew;
            ClaudeDestinationPrefix = s.ClaudeDestinationPrefix;
            ClaudeNoRepoBucketName = s.ClaudeNoRepoBucketName;
            ClaudeConfigBucketName = s.ClaudeConfigBucketName;

            DriveSyncSources.Clear();
            foreach (var src in s.Sources)
            {
                DriveSyncSources.Add(new SyncSource
                {
                    LocalFolderPath = src.LocalFolderPath,
                    DestinationPrefix = src.DestinationPrefix
                });
            }

            var sch = _scheduleService.Settings;
            IsScheduleEnabled = sch.IsEnabled;
            ScheduledTime = sch.ScheduledTime;
            SyncClaudeContextAlso = sch.SyncClaudeContextAlso;

            IsMonday = sch.ScheduledDays.Contains(DayOfWeek.Monday);
            IsTuesday = sch.ScheduledDays.Contains(DayOfWeek.Tuesday);
            IsWednesday = sch.ScheduledDays.Contains(DayOfWeek.Wednesday);
            IsThursday = sch.ScheduledDays.Contains(DayOfWeek.Thursday);
            IsFriday = sch.ScheduledDays.Contains(DayOfWeek.Friday);
            IsSaturday = sch.ScheduledDays.Contains(DayOfWeek.Saturday);
            IsSunday = sch.ScheduledDays.Contains(DayOfWeek.Sunday);

            IsAutostartEnabled = AutostartHelper.IsAutostartEnabled();
            CurrentVersion = _updateService.CurrentAppVersion;
        }
        finally
        {
            _isLoading = false;
        }
    }

    [RelayCommand]
    public void SaveAllSettings()
    {
        var s = _driveSyncService.Settings;
        s.WebAppUrl = DriveWebAppUrl.Trim();
        s.AuthToken = DriveAuthToken.Trim();
        s.DriveFolderUrl = DriveFolderUrl.Trim();
        s.IncludedExtensions = DriveIncludedExtensions;
        s.ExcludedExtensions = DriveExcludedExtensions;
        s.ExcludedFolders = DriveExcludedFolders;
        s.MaxFileSizeMb = (long)DriveMaxFileSizeMb;
        s.OnlyModifiedOrNew = DriveOnlyModifiedOrNew;
        s.ClaudeDestinationPrefix = ClaudeDestinationPrefix.Trim();
        s.ClaudeNoRepoBucketName = ClaudeNoRepoBucketName.Trim();
        s.ClaudeConfigBucketName = ClaudeConfigBucketName.Trim();

        s.Sources = DriveSyncSources
            .Where(src => !string.IsNullOrWhiteSpace(src.LocalFolderPath))
            .ToList();

        _driveSyncService.UpdateSettings(s);

        var days = new List<DayOfWeek>();
        if (IsMonday) days.Add(DayOfWeek.Monday);
        if (IsTuesday) days.Add(DayOfWeek.Tuesday);
        if (IsWednesday) days.Add(DayOfWeek.Wednesday);
        if (IsThursday) days.Add(DayOfWeek.Thursday);
        if (IsFriday) days.Add(DayOfWeek.Friday);
        if (IsSaturday) days.Add(DayOfWeek.Saturday);
        if (IsSunday) days.Add(DayOfWeek.Sunday);

        var sch = _scheduleService.Settings;
        sch.IsEnabled = IsScheduleEnabled;
        sch.ScheduledTime = ScheduledTime;
        sch.ScheduledDays = days;
        sch.SyncClaudeContextAlso = SyncClaudeContextAlso;

        _scheduleService.UpdateSettings(sch);

        AutostartHelper.SetAutostart(IsAutostartEnabled);

        if (!_suppressConfirmation)
        {
            SaveConfirmationMessage = "Ajustes guardados correctamente.";
            ShowSaveConfirmation = true;
        }
    }

    /// <summary>
    /// Triggered on every observable property change.
    /// Skips read-only / status properties and load-time population, then
    /// starts an 800 ms debounce timer that calls SaveAllSettings silently.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Skip properties that should never trigger a save.
        if (_isLoading) return;
        if (e.PropertyName is
            nameof(IsDriveTesting) or
            nameof(DriveConnectionStatus) or
            nameof(ShowSaveConfirmation) or
            nameof(SaveConfirmationMessage) or
            nameof(IsCheckingUpdate) or
            nameof(UpdateStatusText) or
            nameof(CurrentVersion))
        {
            return;
        }

        // Restart the debounce timer.
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _autoSaveCts, cts);
        old.Cancel();
        old.Dispose();

        _ = AutoSaveAsync(cts.Token);
    }

    private async Task AutoSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(800, token);
            _suppressConfirmation = true;
            try { SaveAllSettings(); }
            finally { _suppressConfirmation = false; }
        }
        catch (OperationCanceledException)
        {
            // A newer change arrived before the 800 ms window — no-op.
        }
    }

    [RelayCommand]
    public async Task TestDriveConnection()
    {
        if (string.IsNullOrWhiteSpace(DriveWebAppUrl))
        {
            DriveConnectionStatus = "Ingresa la URL del Web App antes de probar.";
            return;
        }

        IsDriveTesting = true;
        DriveConnectionStatus = "Probando conexión con probe file temporal...";

        try
        {
            var result = await _driveSyncService.TestConnectionAsync(DriveWebAppUrl, DriveAuthToken);
            DriveConnectionStatus = result != null
                ? "Conexión exitosa: archivo de prueba verificado en Drive."
                : "Falló la prueba: el servidor no confirmó la subida.";
        }
        catch (Exception ex)
        {
            DriveConnectionStatus = $"Error de conexión: {ex.Message}";
        }
        finally
        {
            IsDriveTesting = false;
        }
    }

    [RelayCommand]
    public void AddSourceFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        bool exists = DriveSyncSources.Any(s => string.Equals(s.LocalFolderPath, folderPath, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            var dirName = new DirectoryInfo(folderPath.TrimEnd('\\', '/')).Name;
            DriveSyncSources.Add(new SyncSource
            {
                LocalFolderPath = folderPath,
                DestinationPrefix = dirName
            });
        }
    }

    [RelayCommand]
    public void RemoveSourceFolder(SyncSource source)
    {
        DriveSyncSources.Remove(source);
    }

    [RelayCommand]
    public void ClearHashIndex()
    {
        _driveSyncService.ClearHashIndex();
        SaveConfirmationMessage = "Caché de hashes restablecido. El próximo run reexaminará todos los archivos.";
        ShowSaveConfirmation = true;
    }

    [RelayCommand]
    public async Task CheckForUpdates()
    {
        IsCheckingUpdate = true;
        UpdateStatusText = "Buscando actualizaciones...";

        try
        {
            var info = await _updateService.CheckForUpdatesAsync();
            if (info.IsUpdateAvailable)
            {
                UpdateStatusText = $"Nueva versión disponible: v{info.LatestVersion}.";
            }
            else
            {
                UpdateStatusText = $"Estás en la última versión (v{info.CurrentVersion}).";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Error al buscar: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }
}
