using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.Services.Interfaces;

namespace GoogleDriveWorkSync.ViewModels;

/// <summary>
/// Lists Drive files, maps them to local destinations, and restores the selected ones.
/// </summary>
public partial class RestoreViewModel : ObservableObject
{
    private readonly IDriveSyncService _driveSyncService;
    private IReadOnlyList<RemoteDriveItem> _remoteItems = Array.Empty<RemoteDriveItem>();
    private List<RestorePlanItem> _planItems = new();
    private CancellationTokenSource? _restoreCts;

    public ObservableCollection<RestorePlanItem> VisibleItems { get; } = new();

    [ObservableProperty]
    private bool _isListing;

    [ObservableProperty]
    private bool _isRestoring;

    [ObservableProperty]
    private string _statusText = "Pulsa «Listar archivos en Drive» para ver qué se puede restaurar.";

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _overwriteExisting;

    [ObservableProperty]
    private string? _alternateRoot;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _progressText = string.Empty;

    public bool HasAlternateRoot => !string.IsNullOrWhiteSpace(AlternateRoot);
    public string AlternateRootDisplay => HasAlternateRoot ? $"Destino alternativo: {AlternateRoot}" : "Destino: carpetas originales";
    public bool IsIdle => !IsListing && !IsRestoring;

    public RestoreViewModel(IDriveSyncService driveSyncService)
    {
        _driveSyncService = driveSyncService;
    }

    partial void OnIsListingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnIsRestoringChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnAlternateRootChanged(string? value)
    {
        OnPropertyChanged(nameof(HasAlternateRoot));
        OnPropertyChanged(nameof(AlternateRootDisplay));
        RebuildPlan();
    }

    /// <summary>Unmapped items have no target until an alternate folder is chosen.</summary>
    public static bool IsRestorable(RestorePlanItem item) => item.CanSelect;

    [RelayCommand]
    public async Task ListRemoteFilesAsync()
    {
        if (!_driveSyncService.IsConfigured)
        {
            StatusText = "Configura la URL del Web App en Ajustes antes de listar.";
            return;
        }

        IsListing = true;
        StatusText = "Listando archivos en Drive...";
        try
        {
            var result = await _driveSyncService.ListRemoteFilesAsync();
            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? "No se pudo listar Drive.";
                return;
            }

            _remoteItems = result.Items;
            RebuildPlan();
        }
        catch (Exception ex)
        {
            StatusText = $"Error al listar Drive: {ex.Message}";
        }
        finally
        {
            IsListing = false;
        }
    }

    private void RebuildPlan()
    {
        _planItems = _driveSyncService.BuildRestorePlan(_remoteItems, AlternateRoot).ToList();
        foreach (var item in _planItems)
        {
            item.IsSelected = IsRestorable(item) && item.Status == RestoreItemStatus.Missing;
        }
        ApplyFilter();
        UpdateStatusSummary();
    }

    private void UpdateStatusSummary()
    {
        if (_planItems.Count == 0)
        {
            StatusText = _remoteItems.Count == 0 ? "No hay archivos en Drive para restaurar." : StatusText;
            return;
        }

        int missing = _planItems.Count(i => i.Status == RestoreItemStatus.Missing && i.PlanError == null);
        int identical = _planItems.Count(i => i.Status == RestoreItemStatus.Identical);
        int overwrite = _planItems.Count(i => i.Status == RestoreItemStatus.WouldOverwrite);
        int unmapped = _planItems.Count(i => i.Status == RestoreItemStatus.Unmapped);
        StatusText = $"{_planItems.Count} archivos en Drive: {missing} faltan localmente, {identical} idénticos, {overwrite} se sobrescribirían, {unmapped} sin destino.";
    }

    private void ApplyFilter()
    {
        var filter = FilterText?.Trim() ?? string.Empty;
        VisibleItems.Clear();
        foreach (var item in _planItems)
        {
            if (filter.Length == 0 ||
                item.DriveRelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (item.LocalTargetPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                item.StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                VisibleItems.Add(item);
            }
        }
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var item in VisibleItems)
        {
            item.IsSelected = IsRestorable(item);
        }
    }

    [RelayCommand]
    public void SelectNone()
    {
        foreach (var item in VisibleItems)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    public void ClearAlternateRoot() => AlternateRoot = null;

    /// <summary>Restores the selected items; returns null when nothing was selected.</summary>
    public async Task<RestoreResultSummary?> RestoreSelectedAsync()
    {
        var selected = _planItems.Where(i => i.IsSelected && IsRestorable(i)).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Selecciona al menos un archivo con destino local.";
            return null;
        }

        _restoreCts = new CancellationTokenSource();
        IsRestoring = true;
        ProgressPercentage = 0;
        ProgressText = "Preparando restauración...";

        var progress = new Progress<SyncProgressReport>(report =>
        {
            ProgressPercentage = report.Percentage;
            ProgressText = string.IsNullOrEmpty(report.StatusMessage)
                ? $"{report.ProcessedFiles} de {report.TotalFiles}"
                : $"{report.StatusMessage} ({report.ProcessedFiles} de {report.TotalFiles})";
        });

        try
        {
            return await _driveSyncService.RestoreFilesAsync(selected, OverwriteExisting, progress, _restoreCts.Token);
        }
        finally
        {
            IsRestoring = false;
            _restoreCts.Dispose();
            _restoreCts = null;
            ProgressText = string.Empty;
            RebuildPlan();
        }
    }

    [RelayCommand]
    public void CancelRestore()
    {
        _restoreCts?.Cancel();
        ProgressText = "Cancelando...";
    }
}
