using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.ViewModels;

namespace GoogleDriveWorkSync.Views;

public sealed partial class RestoreView : Page
{
    public RestoreViewModel ViewModel { get; }

    public RestoreView()
    {
        this.InitializeComponent();
        ViewModel = App.GetService<RestoreViewModel>();
        this.DataContext = ViewModel;
    }

    private async void ChooseAlternateRootButton_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker();
        folderPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        folderPicker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null)
        {
            ViewModel.AlternateRoot = folder.Path;
        }
    }

    private async void RestoreSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var xamlRoot = this.XamlRoot ?? App.Window.Content?.XamlRoot;
        try
        {
            var summary = await ViewModel.RestoreSelectedAsync();
            if (summary != null && xamlRoot != null)
            {
                await SyncDialogHelper.ShowRestoreResultDialogAsync(xamlRoot, summary);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogCrash("RestoreView_RestoreSelected", ex, ex.Message);
            ViewModel.StatusText = $"Error al restaurar: {ex.Message}";
        }
    }
}
