using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

public partial class ContextDiscoveryViewModel : ObservableObject
{
    private readonly IClaudeDiscoveryService _discoveryService;
    private readonly IDriveSyncService _driveSyncService;
    private CancellationTokenSource? _syncCts;
    private List<CandidateGroup> _allGroups = new();
    private int _repositoriesScannedCount;
    private bool _suppressSettingsSync;

    [ObservableProperty]
    private ObservableCollection<ClaudeDiscoveryCandidate> _candidates = new();

    [ObservableProperty]
    private ObservableCollection<CandidateGroup> _groupedCandidates = new();

    [ObservableProperty]
    private ObservableCollection<OutOfSyncFile> _outOfSyncCandidatesList = new();

    [ObservableProperty]
    private ObservableCollection<SyncErrorItem> _syncErrorsList = new();

    public ObservableCollection<CategoryFilterOption> CategoryFilters { get; } = new(
        ClaudeDiscoveryCategory.DisplayOrder.Select(c => new CategoryFilterOption(c)));

    public IReadOnlyList<string> AvailableCategories => ClaudeDiscoveryCategory.DisplayOrder;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _targetDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    private int _maxDepth = 4;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isDriveSyncConfigured;

    [ObservableProperty]
    private string _claudeSyncStatusText = "Sin configurar";

    [ObservableProperty]
    private string _claudeSyncStatusColor = "#D13438";

    [ObservableProperty]
    private string _repositoriesScannedText = "Sin explorar";

    [ObservableProperty]
    private string _lastSyncShortText = "Nunca";

    [ObservableProperty]
    private string _lastSyncSubtitleText = "En Google Drive";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSyncToDrive))]
    [NotifyPropertyChangedFor(nameof(CanOpenTools))]
    [NotifyCanExecuteChangedFor(nameof(SyncToDriveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceSyncAllToDriveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryClaudeErrorsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCleanupSuggestionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenDuplicatesInspectorCommand))]
    private bool _isSyncingToDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenTools))]
    [NotifyCanExecuteChangedFor(nameof(OpenCleanupSuggestionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenDuplicatesInspectorCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyStatusText = string.Empty;

    public bool CanOpenTools => !IsBusy && !IsSyncingToDrive;

    public void NotifyToolCommandsCanExecute()
    {
        OpenCleanupSuggestionsCommand.NotifyCanExecuteChanged();
        OpenDuplicatesInspectorCommand.NotifyCanExecuteChanged();
    }

    public void SetBusyState(bool isBusy, string statusText = "")
    {
        RunOnUIThread(() =>
        {
            IsBusy = isBusy;
            BusyStatusText = statusText;
            if (isBusy && !string.IsNullOrWhiteSpace(statusText))
            {
                StatusMessage = statusText;
            }
            else if (!isBusy)
            {
                RefreshStatus();
            }
            NotifyToolCommandsCanExecute();
        });
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncButtonText))]
    [NotifyPropertyChangedFor(nameof(CanSyncToDrive))]
    [NotifyPropertyChangedFor(nameof(SyncButtonToolTip))]
    [NotifyCanExecuteChangedFor(nameof(SyncToDriveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceSyncAllToDriveCommand))]
    private int _selectedCandidatesCount;

    [ObservableProperty]
    private int _outOfSyncCandidatesCount;

    [ObservableProperty]
    private int _newCandidatesCount;

    [ObservableProperty]
    private int _modifiedCandidatesCount;

    [ObservableProperty]
    private bool _hasOutOfSyncCandidates;

    [ObservableProperty]
    private bool _hasSyncErrors;

    [ObservableProperty]
    private int _syncErrorsCount;

    [ObservableProperty]
    private string _syncErrorsButtonText = string.Empty;

    [ObservableProperty]
    private string _lastSyncDisplay = string.Empty;

    [ObservableProperty]
    private string _driveSyncStatusMessage = "Listo para respaldar contexto IA.";

    [ObservableProperty]
    private double _driveSyncProgressPercentage;

    [ObservableProperty]
    private int _driveSyncCurrentCount;

    [ObservableProperty]
    private int _driveSyncTotalCount;

    [ObservableProperty]
    private string _driveSyncProgressPercentageText = "0%";

    [ObservableProperty]
    private string _driveSyncProgressDetail = string.Empty;

    [ObservableProperty]
    private bool _isDriveSyncIndeterminate;

    [ObservableProperty]
    private bool _isExplorerExpanded;

    public event EventHandler? OutOfSyncPreviewReady;
    public event EventHandler? CleanupSuggestionsRequested;
    public event EventHandler? DuplicatesInspectorRequested;

    public bool IsDriveConfigured => _driveSyncService.IsConfigured;

    public bool CanSyncToDrive => IsDriveConfigured && !IsSyncingToDrive && SelectedCandidatesCount > 0;

    public string SyncButtonText
    {
        get
        {
            if (IsSyncingToDrive)
            {
                return OutOfSyncCandidatesCount > 0
                    ? $"Sincronizando ({OutOfSyncCandidatesCount} pendientes)..."
                    : "Completando sincronización...";
            }

            return SelectedCandidatesCount switch
            {
                0 => "Sincronizar desincronizados",
                _ => $"Sincronizar desincronizados ({SelectedCandidatesCount})"
            };
        }
    }

    public string SyncButtonToolTip
    {
        get
        {
            if (!IsDriveConfigured)
                return "Configura la Web App de Google Drive en Ajustes para habilitar esto.";
            if (SelectedCandidatesCount == 0)
                return "No hay archivos desincronizados seleccionados.";
            return $"Sube {SelectedCandidatesCount} archivos de contexto en lotes hacia Google Drive.";
        }
    }

    public ContextDiscoveryViewModel(IClaudeDiscoveryService discoveryService, IDriveSyncService driveSyncService)
    {
        _discoveryService = discoveryService;
        _driveSyncService = driveSyncService;

        LoadDirectoryFromSettings();

        foreach (var filter in CategoryFilters)
        {
            filter.PropertyChanged += (s, e) => ApplyCategoryFilter();
        }

        _driveSyncService.SettingsChanged += (s, e) =>
        {
            RunOnUIThread(() =>
            {
                LoadDirectoryFromSettings();
                RefreshStatus();
            });
        };

        _driveSyncService.ClaudeSyncErrorsChanged += (s, errors) => UpdateSyncErrorsDisplay(errors);

        UpdateSyncErrorsDisplay(_driveSyncService.LastClaudeSyncErrors);
        UpdateLastSyncDisplay();
        RefreshStatus();

        // Automatically discover pending Claude context files on startup
        _ = DiscoverAsync();
    }

    private static void RunOnUIThread(Action action)
    {
        DiagnosticLogger.RunOnUIThread(action);
    }

    private void LoadDirectoryFromSettings()
    {
        _suppressSettingsSync = true;
        try
        {
            var settings = _driveSyncService.Settings;
            if (settings != null)
            {
                if (!string.IsNullOrWhiteSpace(settings.ClaudeTargetDirectory))
                {
                    TargetDirectory = settings.ClaudeTargetDirectory;
                }
                if (settings.ClaudeMaxDepth >= 1 && settings.ClaudeMaxDepth <= 10)
                {
                    MaxDepth = settings.ClaudeMaxDepth;
                }
            }
        }
        finally
        {
            _suppressSettingsSync = false;
        }
    }

    private void SaveDirectoryToSettingsIfChanged()
    {
        if (_suppressSettingsSync) return;
        var settings = _driveSyncService.Settings;
        if (settings == null) return;

        var trimmedDir = TargetDirectory?.Trim() ?? string.Empty;
        int clampedDepth = Math.Clamp(MaxDepth, 1, 10);

        if (!string.Equals(settings.ClaudeTargetDirectory, trimmedDir, StringComparison.OrdinalIgnoreCase) ||
            settings.ClaudeMaxDepth != clampedDepth)
        {
            settings.ClaudeTargetDirectory = trimmedDir;
            settings.ClaudeMaxDepth = clampedDepth;
            _driveSyncService.UpdateSettings(settings);
        }
    }

    public void RefreshStatus()
    {
        IsDriveSyncConfigured = _driveSyncService.IsConfigured;
        UpdateLastSyncDisplay();

        if (!IsDriveSyncConfigured)
        {
            ClaudeSyncStatusText = "Sin configurar";
            ClaudeSyncStatusColor = "#D13438"; // Red
        }
        else if (IsSyncingToDrive)
        {
            ClaudeSyncStatusText = "Sincronizando...";
            ClaudeSyncStatusColor = "#FF8C00"; // Orange
        }
        else if (HasSyncErrors)
        {
            ClaudeSyncStatusText = $"Con errores ({SyncErrorsCount})";
            ClaudeSyncStatusColor = "#D13438"; // Red
        }
        else if (OutOfSyncCandidatesCount > 0)
        {
            ClaudeSyncStatusText = "Por sincronizar";
            ClaudeSyncStatusColor = "#0078D4"; // Blue/Accent
        }
        else
        {
            ClaudeSyncStatusText = "Al día";
            ClaudeSyncStatusColor = "#107C41"; // Green
        }

        OnPropertyChanged(nameof(IsDriveConfigured));
        OnPropertyChanged(nameof(CanSyncToDrive));
        OnPropertyChanged(nameof(SyncButtonText));
        OnPropertyChanged(nameof(SyncButtonToolTip));
        SyncToDriveCommand.NotifyCanExecuteChanged();
        ForceSyncAllToDriveCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSyncErrorsDisplay(IReadOnlyList<SyncErrorItem>? errors)
    {
        RunOnUIThread(() =>
        {
            SyncErrorsList.Clear();
            if (errors != null)
            {
                foreach (var error in errors)
                {
                    SyncErrorsList.Add(error);
                }
            }

            SyncErrorsCount = errors?.Count ?? 0;
            HasSyncErrors = SyncErrorsCount > 0;
            SyncErrorsButtonText = SyncErrorsCount switch
            {
                1 => "1 archivo de contexto no pudo sincronizarse",
                _ => $"{SyncErrorsCount} archivos de contexto no pudieron sincronizarse"
            };
            RefreshStatus();
        });
    }

    private Task? _activeDiscoveryTask;

    [RelayCommand]
    public Task DiscoverAsync()
    {
        if (IsScanning && _activeDiscoveryTask != null)
        {
            return _activeDiscoveryTask;
        }

        _activeDiscoveryTask = ExecuteDiscoverInternalAsync();
        return _activeDiscoveryTask;
    }

    private async Task ExecuteDiscoverInternalAsync()
    {
        await DiagnosticLogger.RunOnUIThreadAsync(() =>
        {
            SaveDirectoryToSettingsIfChanged();
            IsScanning = true;
            StatusMessage = "Buscando archivos de contexto IA...";
            DriveSyncStatusMessage = "Escaneando repositorios y configuración global de Claude...";
        });

        try
        {
            var report = await Task.Run(() => _discoveryService.DiscoverAsync(TargetDirectory, MaxDepth));

            await DiagnosticLogger.RunOnUIThreadAsync(() =>
            {
                foreach (var oldCandidate in Candidates)
                {
                    oldCandidate.PropertyChanged -= OnCandidatePropertyChanged;
                }
                Candidates.Clear();
                GroupedCandidates.Clear();
                _allGroups.Clear();

                foreach (var candidate in report.Candidates)
                {
                    candidate.PropertyChanged += OnCandidatePropertyChanged;
                    Candidates.Add(candidate);
                }

                _repositoriesScannedCount = report.RepositoriesScanned;
                RebuildGroups();
                UpdateSelectionState();

                RepositoriesScannedText = report.RepositoriesScanned switch
                {
                    0 => $"{report.Candidates.Count} archivos detectados",
                    1 => $"1 repositorio • {report.Candidates.Count} archivos",
                    _ => $"{report.RepositoriesScanned} repositorios • {report.Candidates.Count} archivos"
                };

                StatusMessage = $"Descubrimiento finalizado: {report.Candidates.Count} archivos detectados ({report.OutOfSyncCount} pendientes, {report.RepositoriesScanned} repositorios).";
                if (!IsSyncingToDrive)
                {
                    DriveSyncStatusMessage = report.OutOfSyncCount > 0
                        ? $"{report.OutOfSyncCount} archivos de contexto pendientes de respaldar en Google Drive."
                        : "Todo tu contexto IA está respaldado y al día en Google Drive.";
                }
                RefreshStatus();
            });
        }
        catch (Exception ex)
        {
            await DiagnosticLogger.RunOnUIThreadAsync(() =>
            {
                StatusMessage = $"Error durante la búsqueda: {ex.Message}";
                DriveSyncStatusMessage = $"Error al escanear: {WorkSyncViewModel.GetDisplayErrorMessage(ex)}";
            });
        }
        finally
        {
            await DiagnosticLogger.RunOnUIThreadAsync(() => IsScanning = false);
        }
    }

    /// <summary>
    /// Default action: sync only selected candidates that are out of sync.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSyncToDrive))]
    public async Task SyncToDriveAsync()
    {
        await ExecuteCandidateSync(forceAll: false);
    }

    /// <summary>
    /// Optional action: force upload all selected candidates regardless of hash status.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSyncToDrive))]
    public async Task ForceSyncAllToDriveAsync()
    {
        await ExecuteCandidateSync(forceAll: true);
    }

    private async Task ExecuteCandidateSync(bool forceAll)
    {
        if (IsSyncingToDrive) return;

        List<ClaudeDiscoveryCandidate> toUpload = new();
        bool shouldAbort = false;

        await DiagnosticLogger.RunOnUIThreadAsync(() =>
        {
            if (IsSyncingToDrive)
            {
                shouldAbort = true;
                return;
            }

            toUpload = Candidates
                .Where(c => c.IsSelected && !c.IsTrackedByGit)
                .Where(c => forceAll || c.SyncStatus != CandidateSyncStatus.UpToDate)
                .ToList();

            if (toUpload.Count == 0)
            {
                DriveSyncStatusMessage = "Todos los archivos seleccionados ya están al día en Google Drive.";
                shouldAbort = true;
                return;
            }

            IsSyncingToDrive = true;
            IsDriveSyncIndeterminate = false;
            DriveSyncStatusMessage = forceAll
                ? "Iniciando sincronización completa forzada por lotes..."
                : "Iniciando sincronización incremental por lotes...";
            DriveSyncProgressDetail = "Preparando lotes...";
            DriveSyncProgressPercentage = 0;
            DriveSyncProgressPercentageText = "0%";
            DriveSyncTotalCount = toUpload.Count;
            DriveSyncCurrentCount = 0;
            RefreshStatus();
        });

        if (shouldAbort) return;

        _syncCts = new CancellationTokenSource();
        var token = _syncCts.Token;

        var progress = new Progress<SyncProgressReport>(OnSyncProgressReported);

        try
        {
            var result = await Task.Run(() => _driveSyncService.SyncClaudeCandidatesAsync(
                toUpload,
                _discoveryService.BuildDriveRelativePath,
                progress,
                token,
                forceFullSync: forceAll));

            await DiagnosticLogger.RunOnUIThreadAsync(() =>
            {
                DriveSyncProgressPercentage = 100;
                DriveSyncProgressPercentageText = "100%";
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    DriveSyncStatusMessage = result.Message;
                }
            });
        }
        catch (OperationCanceledException)
        {
            await DiagnosticLogger.RunOnUIThreadAsync(() => DriveSyncStatusMessage = "Sincronización cancelada por el usuario.");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("ContextDiscoveryViewModel_ExecuteCandidateSync", ex, "Error al sincronizar contexto IA");
            await DiagnosticLogger.RunOnUIThreadAsync(() => DriveSyncStatusMessage = $"Error durante la sincronización: {WorkSyncViewModel.GetDisplayErrorMessage(ex)}");
        }
        finally
        {
            await DiagnosticLogger.RunOnUIThreadAsync(() =>
            {
                IsSyncingToDrive = false;
                _syncCts?.Dispose();
                _syncCts = null;
                UpdateSelectionState();
                RefreshStatus();
            });
        }
    }

    public void OnSyncProgressReported(SyncProgressReport report)
    {
        if (report == null) return;

        RunOnUIThread(() =>
        {
            DriveSyncProgressPercentage = report.Percentage;
            DriveSyncProgressPercentageText = $"{report.Percentage}%";
            DriveSyncStatusMessage = report.StatusMessage;
            DriveSyncProgressDetail = report.StatusMessage;

            if (report.TotalPendingFiles > 0)
            {
                DriveSyncTotalCount = report.TotalPendingFiles;
                DriveSyncCurrentCount = report.UploadedCount + report.ErrorCount;
                OutOfSyncCandidatesCount = report.RemainingPendingFiles;
                HasOutOfSyncCandidates = OutOfSyncCandidatesCount > 0;
                OnPropertyChanged(nameof(SyncButtonText));
            }

            if (report.CompletedItems != null && report.CompletedItems.Count > 0)
            {
                foreach (var item in report.CompletedItems)
                {
                    if (!item.Success) continue;

                    if (item.IsNew)
                    {
                        NewCandidatesCount = Math.Max(0, NewCandidatesCount - 1);
                    }
                    else
                    {
                        ModifiedCandidatesCount = Math.Max(0, ModifiedCandidatesCount - 1);
                    }

                    for (int i = OutOfSyncCandidatesList.Count - 1; i >= 0; i--)
                    {
                        var f = OutOfSyncCandidatesList[i];
                        bool matches = (!string.IsNullOrEmpty(item.FilePath) &&
                                        string.Equals(f.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)) ||
                                       (!string.IsNullOrEmpty(item.FileName) &&
                                        string.Equals(f.FileName, item.FileName, StringComparison.OrdinalIgnoreCase));

                        if (matches)
                        {
                            OutOfSyncCandidatesList.RemoveAt(i);
                            break;
                        }
                    }
                }
            }
        });
    }

    [RelayCommand]
    public async Task RetryClaudeErrorsAsync()
    {
        if (!IsDriveConfigured || IsSyncingToDrive) return;

        bool shouldAbort = false;
        await DiagnosticLogger.RunOnUIThreadAsync(() =>
        {
            if (IsSyncingToDrive)
            {
                shouldAbort = true;
                return;
            }
            IsSyncingToDrive = true;
            DriveSyncProgressPercentage = 0;
            DriveSyncProgressPercentageText = "0%";
            DriveSyncStatusMessage = "Iniciando reintento de archivos de contexto con error...";
            RefreshStatus();
        });

        if (shouldAbort) return;

        _syncCts = new CancellationTokenSource();
        var progress = new Progress<SyncProgressReport>(OnSyncProgressReported);

        try
        {
            var result = await Task.Run(() => _driveSyncService.RetryClaudeFailedFilesAsync(progress, _syncCts.Token));
            await DiagnosticLogger.RunOnUIThreadAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    DriveSyncStatusMessage = result.Message;
                }
            });
            await DiscoverAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("ContextDiscoveryViewModel_RetryClaudeErrors", ex, "Error al reintentar archivos de Claude");
            await DiagnosticLogger.RunOnUIThreadAsync(() => DriveSyncStatusMessage = $"Error durante el reintento: {WorkSyncViewModel.GetDisplayErrorMessage(ex)}");
        }
        finally
        {
            await DiagnosticLogger.RunOnUIThreadAsync(() =>
            {
                IsSyncingToDrive = false;
                _syncCts?.Dispose();
                _syncCts = null;
                UpdateSelectionState();
                RefreshStatus();
            });
        }
    }

    [RelayCommand]
    public void CancelDriveSync()
    {
        _syncCts?.Cancel();
        _driveSyncService.CancelSync();
        DriveSyncStatusMessage = "Cancelando sincronización...";
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
    public void PreviewOutOfSyncCandidates()
    {
        RebuildOutOfSyncList();
        OutOfSyncPreviewReady?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void SelectOnlyOutOfSync()
    {
        foreach (var candidate in Candidates)
        {
            candidate.IsSelected = !candidate.IsTrackedByGit && candidate.SyncStatus != CandidateSyncStatus.UpToDate;
        }
        UpdateSelectionState();
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var candidate in Candidates) candidate.IsSelected = true;
        UpdateSelectionState();
    }

    [RelayCommand]
    public void DeselectAll()
    {
        foreach (var candidate in Candidates) candidate.IsSelected = false;
        UpdateSelectionState();
    }

    [RelayCommand]
    public void ShowAllCategories()
    {
        foreach (var filter in CategoryFilters) filter.IsChecked = true;
    }

    public void SelectOnlyCategory(string category)
    {
        foreach (var candidate in Candidates) candidate.IsSelected = candidate.Category == category;
        UpdateSelectionState();
    }

    public void SetGroupSelection(CandidateGroup group, bool isSelected)
    {
        foreach (var candidate in group) candidate.IsSelected = isSelected;
        UpdateSelectionState();
    }

    public void UpdateLastSyncDisplay()
    {
        var settings = _driveSyncService.Settings;
        if (settings?.LastClaudeSyncAt != null)
        {
            var countText = settings.LastClaudeSyncCount == 1 ? "1 archivo respaldado" : $"{settings.LastClaudeSyncCount} archivos respaldados";
            LastSyncShortText = settings.LastClaudeSyncAt.Value.ToString("dd/MM HH:mm");
            LastSyncSubtitleText = countText;
            LastSyncDisplay = $"Último respaldo Claude: {LastSyncShortText} ({countText})";
        }
        else
        {
            LastSyncShortText = "Nunca";
            LastSyncSubtitleText = "En Google Drive";
            LastSyncDisplay = "Último respaldo Claude: Nunca";
        }
    }

    private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClaudeDiscoveryCandidate.IsSelected) or nameof(ClaudeDiscoveryCandidate.SyncStatus))
        {
            if (!IsSyncingToDrive)
            {
                UpdateSelectionState();
            }
        }
    }

    private void UpdateSelectionState()
    {
        var pendingCandidates = Candidates
            .Where(c => !c.IsTrackedByGit && c.SyncStatus != CandidateSyncStatus.UpToDate)
            .ToList();

        SelectedCandidatesCount = Candidates.Count(c => c.IsSelected && !c.IsTrackedByGit && c.SyncStatus != CandidateSyncStatus.UpToDate);
        OutOfSyncCandidatesCount = pendingCandidates.Count;
        NewCandidatesCount = pendingCandidates.Count(c => c.SyncStatus == CandidateSyncStatus.New);
        ModifiedCandidatesCount = pendingCandidates.Count(c => c.SyncStatus == CandidateSyncStatus.Modified);
        HasOutOfSyncCandidates = OutOfSyncCandidatesCount > 0;

        RebuildOutOfSyncList();
        RefreshStatus();
    }

    private void RebuildOutOfSyncList()
    {
        OutOfSyncCandidatesList.Clear();
        const int maxPreviewItems = 200;
        foreach (var c in Candidates
            .Where(c => !c.IsTrackedByGit && c.SyncStatus != CandidateSyncStatus.UpToDate)
            .Take(maxPreviewItems))
        {
            string driveRelative = _discoveryService.BuildDriveRelativePath(c);
            OutOfSyncCandidatesList.Add(new OutOfSyncFile
            {
                FileName = Path.GetFileName(c.FilePath),
                FilePath = c.FilePath,
                RelativePath = $"{c.Category} • {driveRelative}",
                FileSize = c.FileSizeBytes,
                Reason = c.SyncStatus == CandidateSyncStatus.New ? "Nuevo" : "Modificado"
            });
        }
    }

    private void RebuildGroups()
    {
        _allGroups = CandidateGroup.BuildFrom(Candidates).ToList();
        ApplyCategoryFilter();
    }

    private void ApplyCategoryFilter()
    {
        GroupedCandidates.Clear();
        foreach (var group in _allGroups)
        {
            var filter = CategoryFilters.FirstOrDefault(f => f.Category == group.Category);
            if (filter is null || filter.IsChecked)
            {
                GroupedCandidates.Add(group);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenTools))]
    public void OpenCleanupSuggestions()
    {
        CleanupSuggestionsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanOpenTools))]
    public void OpenDuplicatesInspector()
    {
        DuplicatesInspectorRequested?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<DriveOrphanItem>> DetectOrphansAsync()
        => _driveSyncService.DetectClaudeOrphansAsync();

    public Task<TrashResultSummary> TrashOrphansAsync(IEnumerable<DriveOrphanItem> orphans)
        => _driveSyncService.TrashOrphansAsync(orphans);

    public void DismissOrphans(IEnumerable<DriveOrphanItem> orphans)
        => _driveSyncService.DismissOrphans(orphans);

    public async Task<DuplicateScanResult> DetectDuplicatesAsync()
    {
        if (Candidates.Count == 0 && !IsScanning)
        {
            await DiscoverAsync();
        }
        return await _driveSyncService.DetectCandidateDuplicatesAsync(Candidates);
    }

    public Task<DuplicateCleanupResultSummary> DeleteDuplicatesAsync(IEnumerable<DuplicateFileItem> files, bool trashFromDrive)
        => _driveSyncService.DeleteDuplicateFilesAsync(files, trashFromDrive);
}
