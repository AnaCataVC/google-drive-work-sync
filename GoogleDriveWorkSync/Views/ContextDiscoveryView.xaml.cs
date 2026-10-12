using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.ViewModels;

namespace GoogleDriveWorkSync.Views;

public sealed partial class ContextDiscoveryView : Page
{
    public ContextDiscoveryViewModel ViewModel { get; }
    private readonly SemaphoreSlim _dialogLock = new(1, 1);

    public ContextDiscoveryView()
    {
        this.InitializeComponent();
        ViewModel = App.GetService<ContextDiscoveryViewModel>();
        this.DataContext = ViewModel;

        ViewModel.OutOfSyncPreviewReady += async (s, e) =>
        {
            await ShowOutOfSyncDialogAsync();
        };

        ViewModel.CleanupSuggestionsRequested += async (s, e) =>
        {
            await ShowCleanupSuggestionsDialogAsync();
        };

        ViewModel.DuplicatesInspectorRequested += async (s, e) =>
        {
            await ShowDuplicatesInspectorDialogAsync();
        };
    }

    private XamlRoot? GetEffectiveXamlRoot() => this.XamlRoot ?? App.Window.Content?.XamlRoot;

    private void SelectAllInGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CandidateGroup group)
        {
            ViewModel.SetGroupSelection(group, true);
        }
    }

    private void DeselectAllInGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CandidateGroup group)
        {
            ViewModel.SetGroupSelection(group, false);
        }
    }

    private void SelectOnlyCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string category)
        {
            ViewModel.SelectOnlyCategory(category);
        }
    }

    private async Task ShowOutOfSyncDialogAsync()
    {
        if (!await _dialogLock.WaitAsync(0)) return;
        try
        {
            var xamlRoot = GetEffectiveXamlRoot();
            if (xamlRoot == null) return;

            await SyncDialogHelper.ShowOutOfSyncDialogAsync(
                xamlRoot,
                ViewModel.OutOfSyncCandidatesList.ToList(),
                emptyTitle: "Archivos de Claude al día",
                emptyMessage: "Todos los archivos de contexto IA están al día en Google Drive.",
                activeTitleFormat: "Contexto IA pendiente de sincronizar ({0})",
                canSync: ViewModel.SyncToDriveCommand.CanExecute(null),
                onSyncAction: async () => await ViewModel.SyncToDriveCommand.ExecuteAsync(null));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("ContextDiscoveryView_OutOfSyncDialog", ex, "Error en diálogo de desincronizados de contexto");
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    private async Task ShowCleanupSuggestionsDialogAsync()
    {
        if (!await _dialogLock.WaitAsync(0)) return;
        try
        {
            var xamlRoot = GetEffectiveXamlRoot();
            if (xamlRoot == null) return;

            await SyncDialogHelper.ShowCleanupSuggestionsDialogAsync(
                xamlRoot,
                () => ViewModel.DetectOrphansAsync(),
                orphans => ViewModel.TrashOrphansAsync(orphans),
                orphans => ViewModel.DismissOrphans(orphans),
                dialogTitleSuffix: " (Claude)");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("ContextDiscoveryView_CleanupSuggestions", ex, "Error en diálogo de sugerencias de limpieza");
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    private async Task ShowDuplicatesInspectorDialogAsync()
    {
        if (!await _dialogLock.WaitAsync(0)) return;
        try
        {
            var xamlRoot = GetEffectiveXamlRoot();
            if (xamlRoot == null) return;

            await SyncDialogHelper.ShowDuplicatesInspectorDialogAsync(
                xamlRoot,
                () => ViewModel.DetectDuplicatesAsync(),
                (files, trash) => ViewModel.DeleteDuplicatesAsync(files, trash),
                dialogTitleSuffix: " (Claude)");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("ContextDiscoveryView_DuplicatesInspector", ex, "Error en diálogo de duplicados de contexto");
        }
        finally
        {
            _dialogLock.Release();
        }
    }
}
