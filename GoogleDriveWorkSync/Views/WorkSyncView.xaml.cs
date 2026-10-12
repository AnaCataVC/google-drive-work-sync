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

public sealed partial class WorkSyncView : Page
{
    public WorkSyncViewModel ViewModel { get; }
    private readonly SemaphoreSlim _dialogLock = new(1, 1);

    public WorkSyncView()
    {
        this.InitializeComponent();
        ViewModel = App.GetService<WorkSyncViewModel>();
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

    private async void ShowSyncErrorsDialog_Click(object sender, RoutedEventArgs e)
    {
        if (!await _dialogLock.WaitAsync(0)) return;

        try
        {
            var xamlRoot = GetEffectiveXamlRoot();
            if (xamlRoot == null) return;

            var errorsSnapshot = ViewModel.SyncErrorsList.ToList();
            var scroll = new ScrollViewer { MaxHeight = 350 };
            var stack = new StackPanel { Spacing = 8 };

            foreach (var err in errorsSnapshot)
            {
                var border = new Border
                {
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8)
                };

                var itemStack = new StackPanel { Spacing = 4 };
                itemStack.Children.Add(new TextBlock { Text = err.FileName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                itemStack.Children.Add(new TextBlock { Text = $"Categoría: {err.ErrorCategory}", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                itemStack.Children.Add(new TextBlock { Text = err.ErrorMessage, FontSize = 12, TextWrapping = TextWrapping.Wrap });

                border.Child = itemStack;
                stack.Children.Add(border);
            }

            scroll.Content = stack;

            var dialog = new ContentDialog
            {
                Title = $"Archivos con error ({errorsSnapshot.Count})",
                Content = scroll,
                CloseButtonText = "Cerrar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _dialogLock.Release();
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
                ViewModel.OutOfSyncFilesList.ToList(),
                emptyTitle: "Archivos desincronizados",
                emptyMessage: "Todos los archivos locales están sincronizados y al día en Drive.",
                activeTitleFormat: "Archivos que faltan por sincronizar ({0})",
                canSync: ViewModel.SyncDriveNowCommand.CanExecute(null),
                onSyncAction: async () => await ViewModel.SyncDriveNowCommand.ExecuteAsync(null));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("WorkSyncView_OutOfSyncDialog", ex, "Error en diálogo de desincronizados");
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
                orphans => ViewModel.DismissOrphans(orphans));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("WorkSyncView_CleanupSuggestions", ex, "Error en diálogo de sugerencias de limpieza");
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
                (files, trash) => ViewModel.DeleteDuplicatesAsync(files, trash));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("WorkSyncView_DuplicatesInspector", ex, "Error en diálogo de duplicados");
        }
        finally
        {
            _dialogLock.Release();
        }
    }
}
