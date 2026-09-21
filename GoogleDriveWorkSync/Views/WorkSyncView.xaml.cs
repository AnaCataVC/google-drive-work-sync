using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

            var files = ViewModel.OutOfSyncFilesList.ToList();
            object dialogContent;

            if (files.Count == 0)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 8, 0, 8) };
                panel.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 20, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] });
                panel.Children.Add(new TextBlock { Text = "Todos los archivos locales están sincronizados y al día en Drive.", VerticalAlignment = VerticalAlignment.Center });
                dialogContent = panel;
            }
            else
            {
                var scroll = new ScrollViewer { MaxHeight = 380 };
                var stack = new StackPanel { Spacing = 8 };

                foreach (var file in files)
                {
                    var border = new Border
                    {
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8, 12, 8)
                    };

                    var grid = new Grid();
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var header = new Grid();
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var title = new TextBlock { Text = file.FileName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    Grid.SetColumn(title, 0);
                    header.Children.Add(title);

                    var badge = new Border
                    {
                        Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Child = new TextBlock { Text = $"{file.Reason} • {file.FormattedFileSize}", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
                    };
                    Grid.SetColumn(badge, 1);
                    header.Children.Add(badge);

                    var sub = new TextBlock
                    {
                        Text = file.RelativePath,
                        FontSize = 11,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        Margin = new Thickness(0, 4, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    Grid.SetRow(sub, 1);

                    grid.Children.Add(header);
                    grid.Children.Add(sub);

                    border.Child = grid;
                    stack.Children.Add(border);
                }

                scroll.Content = stack;
                dialogContent = scroll;
            }

            var dialog = new ContentDialog
            {
                Title = files.Count == 0 ? "Archivos desincronizados" : $"Archivos que faltan por sincronizar ({files.Count})",
                Content = dialogContent,
                CloseButtonText = "Cerrar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            if (files.Count > 0 && ViewModel.SyncDriveNowCommand.CanExecute(null))
            {
                dialog.PrimaryButtonText = "Sincronizar ahora";
                dialog.DefaultButton = ContentDialogButton.Primary;
            }

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && ViewModel.SyncDriveNowCommand.CanExecute(null))
            {
                await ViewModel.SyncDriveNowCommand.ExecuteAsync(null);
            }
        }
        finally
        {
            _dialogLock.Release();
        }
    }
}
