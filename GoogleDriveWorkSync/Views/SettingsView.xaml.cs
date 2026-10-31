using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using GoogleDriveWorkSync.Models;
using GoogleDriveWorkSync.ViewModels;

namespace GoogleDriveWorkSync.Views;

public sealed partial class SettingsView : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsView()
    {
        this.InitializeComponent();
        ViewModel = App.GetService<SettingsViewModel>();
        this.DataContext = ViewModel;
    }

    private async void AddSourceFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker();
        folderPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        folderPicker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null)
        {
            ViewModel.AddSourceFolder(folder.Path);
        }
    }

    private async void DeleteSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SyncSource source)
        {
            var xamlRoot = this.XamlRoot ?? App.Window.Content?.XamlRoot;
            if (xamlRoot == null) return;

            var dialog = new ContentDialog
            {
                Title = "¿Eliminar carpeta de sincronización?",
                Content = new TextBlock
                {
                    Text = $"La carpeta «{source.LocalFolderPath}» ya no se respaldará en Google Drive.\n\nLos archivos que ya existen en Google Drive no serán eliminados.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Eliminar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ViewModel.RemoveSourceFolder(source);
            }
        }
    }

    private async void ClearHashIndexButton_Click(object sender, RoutedEventArgs e)
    {
        var xamlRoot = this.XamlRoot ?? App.Window.Content?.XamlRoot;
        if (xamlRoot == null) return;

        var dialog = new ContentDialog
        {
            Title = "¿Restablecer caché de firmas de archivos?",
            Content = new TextBlock
            {
                Text = "Se borrará el índice local de hashes SHA-256.\n\nEn la próxima sincronización, la aplicación reexaminará y calculará el hash de todos los archivos locales desde cero. Esta operación puede tardar más tiempo de lo habitual según el tamaño de tus carpetas.\n\n¿Deseas continuar?",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Restablecer caché",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ClearHashIndex();
        }
    }
}
