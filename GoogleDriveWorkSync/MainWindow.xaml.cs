using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using CommunityToolkit.Mvvm.Input;
using GoogleDriveWorkSync.Services.Interfaces;
using GoogleDriveWorkSync.ViewModels;
using GoogleDriveWorkSync.Views;

namespace GoogleDriveWorkSync;

public sealed partial class MainWindow : Window
{
    private const int MinWindowWidth = 920;
    private const int MinWindowHeight = 620;
    private bool _isExplicitExit;

    public IRelayCommand ShowPanelCommand { get; }
    public IRelayCommand HidePanelCommand { get; }
    public IRelayCommand SyncNowCommand { get; }
    public IRelayCommand ExitCommand { get; }

    public MainWindow()
    {
        App.LogTrace("MainWindow constructor started");
        InitializeComponent();
        App.LogTrace("MainWindow InitializeComponent completed");

        TrySetMicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ShowPanelCommand = new RelayCommand(ShowPanel);
        HidePanelCommand = new RelayCommand(HidePanel);
        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync);
        ExitCommand = new RelayCommand(ExitApplication);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinWindowWidth;
            presenter.PreferredMinimumHeight = MinWindowHeight;
        }

        AppWindow.Resize(new SizeInt32(
            Math.Max(AppWindow.Size.Width, 980),
            Math.Max(AppWindow.Size.Height, 680)));

        AppWindow.Closing += (sender, args) =>
        {
            if (!_isExplicitExit)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
        };

        TrayIcon.LeftClickCommand = new RelayCommand(ToggleWindowVisibility);
    }

    private void TrySetMicaBackdrop()
    {
        if (MicaController.IsSupported())
        {
            this.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(WorkSyncView));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            switch (item.Tag?.ToString())
            {
                case "WorkSync":
                    ContentFrame.Navigate(typeof(WorkSyncView));
                    break;
                case "ContextDiscovery":
                    ContentFrame.Navigate(typeof(ContextDiscoveryView));
                    break;
                case "Settings":
                    ContentFrame.Navigate(typeof(SettingsView));
                    break;
            }
        }
    }

    private void ToggleWindowVisibility()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
        else
        {
            AppWindow.Show();
            AppWindow.MoveInZOrderAtTop();
        }
    }

    private void ShowPanel()
    {
        AppWindow.Show();
        AppWindow.MoveInZOrderAtTop();
    }

    private void HidePanel()
    {
        AppWindow.Hide();
    }

    private async Task SyncNowAsync()
    {
        try
        {
            var driveSyncService = App.GetService<IDriveSyncService>();
            await driveSyncService.RunSyncAsync(forceFullSync: false);
        }
        catch { }
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        TrayIcon.Dispose();
        Application.Current.Exit();
        Environment.Exit(0);
    }
}
