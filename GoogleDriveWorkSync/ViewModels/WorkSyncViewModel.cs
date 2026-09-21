using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;

namespace GoogleDriveWorkSync.ViewModels;

public partial class WorkSyncViewModel : ObservableObject
{
    private readonly IDriveSyncService _driveSyncService;

    [ObservableProperty]
    private bool _isDriveSyncConfigured;

    [ObservableProperty]
    private bool _isDriveSyncing;

    [ObservableProperty]
    private string _driveSyncStatusText = "Sin configurar";

    [ObservableProperty]
    private string _driveSyncStatusColor = "Gray";

    [ObservableProperty]
    private string _driveSyncFoldersDisplay = "Sin carpetas configuradas";

    [ObservableProperty]
    private string _driveSyncLastSyncText = "Nunca";

    [ObservableProperty]
    private string _driveSyncDetailText = string.Empty;

    [ObservableProperty]
    private int _driveSyncProgress;

    [ObservableProperty]
    private bool _hasSyncErrors;

    [ObservableProperty]
    private int _syncErrorsCount;

    [ObservableProperty]
    private string _syncErrorsButtonText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SyncErrorItem> _syncErrorsList = new();

    [ObservableProperty]
    private ObservableCollection<OutOfSyncFile> _outOfSyncFilesList = new();

    [ObservableProperty]
    private int _outOfSyncCount;

    [ObservableProperty]
    private int _newFilesCount;

    [ObservableProperty]
    private int _modifiedFilesCount;

    [ObservableProperty]
    private int _monitoredFoldersCount;

    [ObservableProperty]
    private bool _hasOutOfSyncFiles;

    [ObservableProperty]
    private bool _isScanningOutOfSync;

    [ObservableProperty]
    private string _syncActionTitle = "Sincronizar desincronizados";

    public event EventHandler? OutOfSyncPreviewReady;
    public event EventHandler? SyncHistoryRequested;

    public WorkSyncViewModel(IDriveSyncService driveSyncService)
    {
        _driveSyncService = driveSyncService;

        _driveSyncService.SettingsChanged += (s, e) =>
        {
            RefreshStatus();
            _ = RefreshOutOfSync();
        };
        _driveSyncService.SyncProgressChanged += OnSyncProgressChanged;
        _driveSyncService.SyncCompleted += OnSyncCompleted;
        _driveSyncService.SyncErrorsChanged += (s, errors) => UpdateSyncErrorsDisplay(errors);

        RefreshStatus();
        _ = RefreshOutOfSync();
    }

    public void RefreshStatus()
    {
        IsDriveSyncConfigured = _driveSyncService.IsConfigured;
        IsDriveSyncing = _driveSyncService.IsSyncing;
        UpdateSyncErrorsDisplay(_driveSyncService.LastSyncErrors);

        var settings = _driveSyncService.Settings;
        var driveFolders = settings.Sources
            .Where(s => !string.IsNullOrWhiteSpace(s.LocalFolderPath))
            .Select(s => s.EffectiveDestinationPrefix)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DriveSyncFoldersDisplay = driveFolders.Count == 0
            ? "Sin carpetas configuradas"
            : string.Join(" · ", driveFolders);

        DriveSyncLastSyncText = settings.LastSyncTime.HasValue
            ? settings.LastSyncTime.Value.ToString("dd/MM HH:mm")
            : "Nunca";

        MonitoredFoldersCount = driveFolders.Count;

        if (!IsDriveSyncConfigured)
        {
            DriveSyncStatusText = "Sin configurar";
            DriveSyncStatusColor = "#D13438"; // Red
        }
        else if (IsDriveSyncing)
        {
            DriveSyncStatusText = "Sincronizando...";
            DriveSyncStatusColor = "#FF8C00"; // Orange
        }
        else if (HasSyncErrors)
        {
            DriveSyncStatusText = $"Con errores ({SyncErrorsCount})";
            DriveSyncStatusColor = "#D13438"; // Red
        }
        else
        {
            DriveSyncStatusText = "Al día";
            DriveSyncStatusColor = "#107C41"; // Green
        }
    }

    private void OnSyncProgressChanged(object? sender, SyncProgressReport report)
    {
        DriveSyncProgress = report.Percentage;
        DriveSyncDetailText = report.StatusMessage;
    }

    private void OnSyncCompleted(object? sender, SyncResultSummary summary)
    {
        IsDriveSyncing = false;
        DriveSyncProgress = 100;
        DriveSyncDetailText = summary.Message;
        RefreshStatus();
        _ = RefreshOutOfSync();
    }

    private void UpdateSyncErrorsDisplay(System.Collections.Generic.IReadOnlyList<SyncErrorItem> errors)
    {
        SyncErrorsList.Clear();
        foreach (var error in errors)
        {
            SyncErrorsList.Add(error);
        }

        SyncErrorsCount = errors.Count;
        HasSyncErrors = SyncErrorsCount > 0;
        SyncErrorsButtonText = SyncErrorsCount switch
        {
            1 => "1 archivo no pudo sincronizarse",
            _ => $"{SyncErrorsCount} archivos no pudieron sincronizarse"
        };
    }

    /// <summary>
    /// Default action: sync only new or modified files.
    /// </summary>
    [RelayCommand]
    public async Task SyncDriveNow()
    {
        if (!_driveSyncService.IsConfigured)
        {
            DriveSyncDetailText = "Configura la URL y las carpetas en Ajustes antes de sincronizar.";
            return;
        }

        IsDriveSyncing = true;
        DriveSyncProgress = 0;
        DriveSyncDetailText = "Iniciando sincronización incremental...";
        RefreshStatus();

        try
        {
            var result = await Task.Run(() => _driveSyncService.RunSyncAsync(forceFullSync: false));
            if (result != null && !string.IsNullOrWhiteSpace(result.Message))
            {
                DriveSyncDetailText = result.Message;
            }
        }
        catch (Exception ex)
        {
            DriveSyncDetailText = $"Error al sincronizar: {ex.Message}";
        }
        finally
        {
            IsDriveSyncing = _driveSyncService.IsSyncing;
            RefreshStatus();
        }
    }

    /// <summary>
    /// Secondary action: re-upload everything ignoring the hash cache.
    /// </summary>
    [RelayCommand]
    public async Task ForceSyncDrive()
    {
        if (!_driveSyncService.IsConfigured)
        {
            DriveSyncDetailText = "Configura la URL y las carpetas en Ajustes antes de sincronizar.";
            return;
        }

        IsDriveSyncing = true;
        DriveSyncProgress = 0;
        DriveSyncDetailText = "Iniciando sincronización completa forzada (sin omitir archivos)...";
        RefreshStatus();

        try
        {
            var result = await Task.Run(() => _driveSyncService.RunSyncAsync(forceFullSync: true));
            if (result != null && !string.IsNullOrWhiteSpace(result.Message))
            {
                DriveSyncDetailText = result.Message;
            }
        }
        catch (Exception ex)
        {
            DriveSyncDetailText = $"Error al forzar sincronización: {ex.Message}";
        }
        finally
        {
            IsDriveSyncing = _driveSyncService.IsSyncing;
            RefreshStatus();
        }
    }

    [RelayCommand]
    public async Task RefreshOutOfSync()
    {
        if (!_driveSyncService.IsConfigured)
        {
            OutOfSyncFilesList.Clear();
            OutOfSyncCount = 0;
            NewFilesCount = 0;
            ModifiedFilesCount = 0;
            HasOutOfSyncFiles = false;
            SyncActionTitle = "Sincronizar desincronizados";
            return;
        }

        IsScanningOutOfSync = true;
        try
        {
            var outOfSync = await Task.Run(() => _driveSyncService.PreviewOutOfSyncAsync());
            OutOfSyncFilesList.Clear();
            foreach (var file in outOfSync)
            {
                OutOfSyncFilesList.Add(file);
            }

            OutOfSyncCount = OutOfSyncFilesList.Count;
            NewFilesCount = OutOfSyncFilesList.Count(f => f.Reason == "Nuevo");
            ModifiedFilesCount = OutOfSyncFilesList.Count(f => f.Reason == "Modificado");
            HasOutOfSyncFiles = OutOfSyncCount > 0;
            SyncActionTitle = HasOutOfSyncFiles
                ? $"Sincronizar desincronizados ({OutOfSyncCount})"
                : "Sincronizar desincronizados";
        }
        catch (Exception ex)
        {
            DriveSyncDetailText = $"Error al verificar archivos pendientes: {ex.Message}";
        }
        finally
        {
            IsScanningOutOfSync = false;
        }
    }

    [RelayCommand]
    public async Task PreviewOutOfSync()
    {
        await RefreshOutOfSync();
        OutOfSyncPreviewReady?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task RetrySyncErrors()
    {
        if (!_driveSyncService.IsConfigured) return;

        IsDriveSyncing = true;
        DriveSyncProgress = 0;
        DriveSyncDetailText = "Iniciando reintento de archivos con error...";
        RefreshStatus();

        try
        {
            var result = await Task.Run(() => _driveSyncService.RetryFailedFilesAsync());
            if (result != null && !string.IsNullOrWhiteSpace(result.Message))
            {
                DriveSyncDetailText = result.Message;
            }
        }
        catch (Exception ex)
        {
            DriveSyncDetailText = $"Error durante el reintento: {ex.Message}";
        }
        finally
        {
            IsDriveSyncing = _driveSyncService.IsSyncing;
            RefreshStatus();
        }
    }

    [RelayCommand]
    public void CancelDriveSync()
    {
        _driveSyncService.CancelSync();
        DriveSyncDetailText = "Cancelando sincronización...";
    }

    [RelayCommand]
    public void OpenDriveFolder()
    {
        var url = _driveSyncService.Settings.DriveFolderUrl;
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            }
            catch { }
        }
    }

    [RelayCommand]
    public void OpenSyncHistory()
    {
        SyncHistoryRequested?.Invoke(this, EventArgs.Empty);
    }
}
