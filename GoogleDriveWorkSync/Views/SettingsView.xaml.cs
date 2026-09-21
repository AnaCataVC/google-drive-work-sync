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

    private void DeleteSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SyncSource source)
        {
            ViewModel.RemoveSourceFolder(source);
        }
    }
}
