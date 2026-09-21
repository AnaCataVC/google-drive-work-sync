using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;

namespace GoogleDriveWorkSync.ViewModels;

public partial class ContextDiscoveryViewModel : ObservableObject
{
    private readonly IClaudeDiscoveryService _discoveryService;
    private readonly IDriveSyncService _driveSyncService;
    private CancellationTokenSource? _syncCts;
    private List<CandidateGroup> _allGroups = new();

    [ObservableProperty]
    private ObservableCollection<ClaudeDiscoveryCandidate> _candidates = new();

    [ObservableProperty]
    private ObservableCollection<CandidateGroup> _groupedCandidates = new();

    [ObservableProperty]
    private ObservableCollection<OutOfSyncFile> _outOfSyncCandidatesList = new();

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
    [NotifyPropertyChangedFor(nameof(CanSyncToDrive))]
    [NotifyCanExecuteChangedFor(nameof(SyncToDriveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceSyncAllToDriveCommand))]
    private bool _isSyncingToDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncButtonText))]
    [NotifyPropertyChangedFor(nameof(CanSyncToDrive))]
    [NotifyPropertyChangedFor(nameof(SyncButtonToolTip))]
    [NotifyCanExecuteChangedFor(nameof(SyncToDriveCommand))]
    private int _selectedCandidatesCount;

    [ObservableProperty]
    private int _outOfSyncCandidatesCount;

    [ObservableProperty]
    private string _lastSyncDisplay = string.Empty;

    [ObservableProperty]
    private string _driveSyncStatusMessage = string.Empty;

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

    public event EventHandler? OutOfSyncPreviewReady;

    public bool IsDriveConfigured => _driveSyncService.IsConfigured;

    public bool CanSyncToDrive => IsDriveConfigured && !IsSyncingToDrive && SelectedCandidatesCount > 0;

    public string SyncButtonText => SelectedCandidatesCount switch
    {
        0 => "Sincronizar desincronizados (0 seleccionados)",
        1 => "Sincronizar 1 archivo desincronizado a Drive",
        _ => $"Sincronizar {SelectedCandidatesCount} archivos desincronizados a Drive"
    };

    public string SyncButtonToolTip
    {
        get
        {
            if (!IsDriveConfigured)
                return "Configura la Web App de Google Drive en Ajustes para habilitar esto.";
            if (SelectedCandidatesCount == 0)
                return "No hay archivos desincronizados seleccionados.";
            return $"Sube {SelectedCandidatesCount} archivos de contexto a Google Drive.";
        }
    }

    public ContextDiscoveryViewModel(IClaudeDiscoveryService discoveryService, IDriveSyncService driveSyncService)
    {
        _discoveryService = discoveryService;
        _driveSyncService = driveSyncService;

        foreach (var filter in CategoryFilters)
        {
            filter.PropertyChanged += (s, e) => ApplyCategoryFilter();
        }

        UpdateLastSyncDisplay();
    }

    [RelayCommand]
    public async Task DiscoverAsync()
    {
        if (IsScanning) return;

        IsScanning = true;
        StatusMessage = "Buscando archivos de contexto IA...";
        Candidates.Clear();
        GroupedCandidates.Clear();
        _allGroups.Clear();

        try
        {
            var report = await _discoveryService.DiscoverAsync(TargetDirectory, MaxDepth);

            foreach (var candidate in report.Candidates)
            {
                candidate.PropertyChanged += OnCandidatePropertyChanged;
                Candidates.Add(candidate);
            }

            RebuildGroups();
            UpdateSelectionState();

            StatusMessage = $"Descubrimiento finalizado: {report.Candidates.Count} archivos detectados ({report.OutOfSyncCount} desincronizados, {report.RepositoriesScanned} repositorios).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error durante la búsqueda: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
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

        IsSyncingToDrive = true;
        IsDriveSyncIndeterminate = false;
        DriveSyncStatusMessage = forceAll ? "Forzando sincronización total a Google Drive..." : "Sincronizando archivos desincronizados...";
        DriveSyncProgressDetail = "Preparando archivos...";
        DriveSyncProgressPercentage = 0;
        DriveSyncProgressPercentageText = "0%";

        _syncCts = new CancellationTokenSource();
        var token = _syncCts.Token;

        var toUpload = Candidates
            .Where(c => c.IsSelected && !c.IsTrackedByGit)
            .Where(c => forceAll || c.SyncStatus != CandidateSyncStatus.UpToDate)
            .ToList();

        DriveSyncTotalCount = toUpload.Count;
        DriveSyncCurrentCount = 0;

        if (toUpload.Count == 0)
        {
            DriveSyncStatusMessage = "Todos los archivos seleccionados ya están al día en Google Drive.";
            IsSyncingToDrive = false;
            return;
        }

        int uploaded = 0;
        int failed = 0;

        try
        {
            for (int i = 0; i < toUpload.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var candidate = toUpload[i];
                DriveSyncCurrentCount = i + 1;
                DriveSyncProgressPercentage = (double)(i + 1) / toUpload.Count * 100.0;
                DriveSyncProgressPercentageText = $"{(int)DriveSyncProgressPercentage}%";
                DriveSyncProgressDetail = $"Subiendo {Path.GetFileName(candidate.FilePath)} ({i + 1} de {toUpload.Count})...";

                string destinationPath = _discoveryService.BuildDriveRelativePath(candidate);
                string mimeType = candidate.Category switch
                {
                    ClaudeDiscoveryCategory.Hook => "text/plain",
                    ClaudeDiscoveryCategory.GlobalSetting or ClaudeDiscoveryCategory.Keybinding or ClaudeDiscoveryCategory.McpConfig => "application/json",
                    _ => "text/markdown"
                };

                bool success = await _driveSyncService.UploadSingleFileAsync(candidate.FilePath, destinationPath, mimeType, token);
                if (success)
                {
                    uploaded++;
                    var fi = new FileInfo(candidate.FilePath);
                    string hash = _driveSyncService.ComputeSha256(candidate.FilePath);
                    _driveSyncService.SaveKnownHash(destinationPath, hash, fi);
                    candidate.SyncStatus = CandidateSyncStatus.UpToDate;
                }
                else
                {
                    failed++;
                }

                await Task.Delay(200, token);
            }

            var settings = _driveSyncService.Settings;
            settings.LastClaudeSyncAt = DateTime.Now;
            settings.LastClaudeSyncCount = uploaded;
            _driveSyncService.UpdateSettings(settings);

            DriveSyncStatusMessage = failed == 0
                ? $"Sincronización completada: {uploaded} archivos respaldados con éxito."
                : $"Sincronización finalizada: {uploaded} subidos, {failed} fallidos.";
            UpdateLastSyncDisplay();
        }
        catch (OperationCanceledException)
        {
            DriveSyncStatusMessage = "Sincronización cancelada por el usuario.";
        }
        catch (Exception ex)
        {
            DriveSyncStatusMessage = $"Error durante la sincronización: {ex.Message}";
        }
        finally
        {
            IsSyncingToDrive = false;
            _syncCts?.Dispose();
            _syncCts = null;
            UpdateSelectionState();
        }
    }

    [RelayCommand]
    public void CancelDriveSync()
    {
        _syncCts?.Cancel();
    }

    [RelayCommand]
    public void PreviewOutOfSyncCandidates()
    {
        OutOfSyncCandidatesList.Clear();
        foreach (var c in Candidates.Where(c => !c.IsTrackedByGit && c.SyncStatus != CandidateSyncStatus.UpToDate))
        {
            OutOfSyncCandidatesList.Add(new OutOfSyncFile
            {
                FileName = Path.GetFileName(c.FilePath),
                FilePath = c.FilePath,
                RelativePath = c.RelativePath,
                FileSize = c.FileSizeBytes,
                Reason = c.SyncStatus == CandidateSyncStatus.New ? "Nuevo" : "Modificado"
            });
        }

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
        if (settings.LastClaudeSyncAt.HasValue)
        {
            var countText = settings.LastClaudeSyncCount == 1 ? "1 archivo" : $"{settings.LastClaudeSyncCount} archivos";
            LastSyncDisplay = $"Último respaldo Claude: {settings.LastClaudeSyncAt.Value:dd/MM HH:mm} ({countText})";
        }
        else
        {
            LastSyncDisplay = "Último respaldo Claude: Nunca";
        }
    }

    private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClaudeDiscoveryCandidate.IsSelected))
        {
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        SelectedCandidatesCount = Candidates.Count(c => c.IsSelected);
        OutOfSyncCandidatesCount = Candidates.Count(c => !c.IsTrackedByGit && c.SyncStatus != CandidateSyncStatus.UpToDate);
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
}
