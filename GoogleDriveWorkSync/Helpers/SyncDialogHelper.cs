using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GoogleDriveWorkSync.Models;

namespace GoogleDriveWorkSync.Helpers;

public static class SyncDialogHelper
{
    public static async Task ShowOutOfSyncDialogAsync(
        XamlRoot xamlRoot,
        IReadOnlyList<OutOfSyncFile> files,
        string emptyTitle,
        string emptyMessage,
        string activeTitleFormat,
        bool canSync,
        Func<Task>? onSyncAction = null)
    {
        object dialogContent;

        if (files.Count == 0)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 20, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] });
            panel.Children.Add(new TextBlock { Text = emptyMessage, VerticalAlignment = VerticalAlignment.Center });
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
            Title = files.Count == 0 ? emptyTitle : string.Format(activeTitleFormat, files.Count),
            Content = dialogContent,
            CloseButtonText = "Cerrar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        if (files.Count > 0 && canSync && onSyncAction != null)
        {
            dialog.PrimaryButtonText = "Sincronizar ahora";
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && canSync && onSyncAction != null)
        {
            await onSyncAction();
        }
    }

    public static async Task ShowCleanupSuggestionsDialogAsync(
        XamlRoot xamlRoot,
        Func<Task<IReadOnlyList<DriveOrphanItem>>> detectOrphansFunc,
        Func<IEnumerable<DriveOrphanItem>, Task<TrashResultSummary>> trashOrphansFunc,
        Action<IEnumerable<DriveOrphanItem>> dismissOrphansAction,
        string dialogTitleSuffix = "",
        Action<bool, string>? setBusyState = null)
    {
        string scanMessage = string.IsNullOrWhiteSpace(dialogTitleSuffix)
            ? "Buscando archivos huérfanos en Google Drive..."
            : $"Buscando archivos huérfanos{dialogTitleSuffix} en Google Drive...";

        setBusyState?.Invoke(true, scanMessage);
        List<DriveOrphanItem> orphans;
        try
        {
            orphans = (await detectOrphansFunc()).ToList();
        }
        finally
        {
            setBusyState?.Invoke(false, string.Empty);
        }

        if (orphans.Count == 0)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 20, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] });
            panel.Children.Add(new TextBlock { Text = "No se encontraron archivos huérfanos. Todos los archivos registrados existen localmente.", VerticalAlignment = VerticalAlignment.Center });

            var emptyDialog = new ContentDialog
            {
                Title = $"Sugerencias de limpieza en Google Drive{dialogTitleSuffix}",
                Content = panel,
                CloseButtonText = "Cerrar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };
            await emptyDialog.ShowAsync();
            return;
        }

        var mainPanel = new StackPanel { Spacing = 8 };

        var headerInfo = new TextBlock
        {
            Text = "Los siguientes archivos fueron eliminados o movidos en tu equipo local, pero siguen existiendo en Google Drive. Puedes enviarlos a la papelera de Drive o dejar de seguirlos localmente.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        mainPanel.Children.Add(headerInfo);

        // Toolbar: Search Box & Bulk Selection Buttons
        var toolbarGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchBox = new TextBox
        {
            PlaceholderText = "Buscar por nombre o ruta...",
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(searchBox, 0);

        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var selectAllBtn = new Button { Content = "Seleccionar todo", Padding = new Thickness(8, 4, 8, 4) };
        var deselectAllBtn = new Button { Content = "Deseleccionar todo", Padding = new Thickness(8, 4, 8, 4) };
        buttonsPanel.Children.Add(selectAllBtn);
        buttonsPanel.Children.Add(deselectAllBtn);
        Grid.SetColumn(buttonsPanel, 1);

        toolbarGrid.Children.Add(searchBox);
        toolbarGrid.Children.Add(buttonsPanel);
        mainPanel.Children.Add(toolbarGrid);

        var statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 0, 0, 4)
        };
        mainPanel.Children.Add(statusText);

        var scroll = new ScrollViewer { MaxHeight = 360 };
        var stack = new StackPanel { Spacing = 8 };

        var rowElements = new List<(DriveOrphanItem Orphan, Border Container, CheckBox Cb)>();

        void UpdateMetrics()
        {
            int selectedCount = orphans.Count(o => o.IsSelected);
            long totalBytes = orphans.Where(o => o.IsSelected).Sum(o => o.FileSize);
            statusText.Text = $"{selectedCount} de {orphans.Count} seleccionados • {OutOfSyncFile.FormatBytes(totalBytes)} a liberar en Drive";
        }

        foreach (var orphan in orphans)
        {
            var border = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8)
            };

            var itemGrid = new Grid();
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cb = new CheckBox
            {
                IsChecked = orphan.IsSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            cb.Checked += (s, e) => { orphan.IsSelected = true; UpdateMetrics(); };
            cb.Unchecked += (s, e) => { orphan.IsSelected = false; UpdateMetrics(); };
            Grid.SetColumn(cb, 0);

            var textStack = new StackPanel { Spacing = 2 };
            textStack.Children.Add(new TextBlock { Text = orphan.FileName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            textStack.Children.Add(new TextBlock
            {
                Text = $"{orphan.DriveRelativePath} • {orphan.FormattedFileSize}",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (orphan.IsRenamed)
            {
                var renameBadge = new Border
                {
                    Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = $"Renombrado / movido a: {Path.GetFileName(orphan.RenamedToPath)}",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                    }
                };
                ToolTipService.SetToolTip(renameBadge, $"Nueva ruta local detectada:\n{orphan.RenamedToPath}");
                textStack.Children.Add(renameBadge);
            }

            Grid.SetColumn(textStack, 1);

            // Explorer button
            var openBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE8DA", FontSize = 12 },
                Padding = new Thickness(6, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(openBtn, "Abrir ubicación en el Explorador");
            openBtn.Click += (s, e) =>
            {
                try
                {
                    string target = orphan.IsRenamed && File.Exists(orphan.RenamedToPath)
                        ? orphan.RenamedToPath!
                        : orphan.LocalFilePath;

                    if (File.Exists(target))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{target}\"",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"\"{dir}\"",
                                UseShellExecute = true
                            });
                        }
                    }
                }
                catch { }
            };
            Grid.SetColumn(openBtn, 2);

            itemGrid.Children.Add(cb);
            itemGrid.Children.Add(textStack);
            itemGrid.Children.Add(openBtn);

            border.Child = itemGrid;
            stack.Children.Add(border);

            rowElements.Add((orphan, border, cb));
        }

        scroll.Content = stack;
        mainPanel.Children.Add(scroll);

        // Search filtering logic
        searchBox.TextChanged += (s, e) =>
        {
            string query = searchBox.Text?.Trim().ToLowerInvariant() ?? "";
            foreach (var (orphan, border, _) in rowElements)
            {
                bool matches = string.IsNullOrEmpty(query) ||
                               orphan.FileName.ToLowerInvariant().Contains(query) ||
                               orphan.DriveRelativePath.ToLowerInvariant().Contains(query) ||
                               (orphan.RenamedToPath?.ToLowerInvariant().Contains(query) ?? false);
                border.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            }
        };

        // Bulk selection logic
        selectAllBtn.Click += (s, e) =>
        {
            foreach (var (orphan, border, cb) in rowElements)
            {
                if (border.Visibility == Visibility.Visible)
                {
                    orphan.IsSelected = true;
                    cb.IsChecked = true;
                }
            }
            UpdateMetrics();
        };

        deselectAllBtn.Click += (s, e) =>
        {
            foreach (var (orphan, border, cb) in rowElements)
            {
                if (border.Visibility == Visibility.Visible)
                {
                    orphan.IsSelected = false;
                    cb.IsChecked = false;
                }
            }
            UpdateMetrics();
        };

        UpdateMetrics();

        var dialog = new ContentDialog
        {
            Title = $"Sugerencias de limpieza ({orphans.Count} archivos huérfanos)",
            Content = mainPanel,
            PrimaryButtonText = "Mover seleccionados a papelera",
            SecondaryButtonText = "Descartar del índice",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var actionResult = await dialog.ShowAsync();
        var selectedOrphans = orphans.Where(o => o.IsSelected).ToList();

        if (selectedOrphans.Count == 0 && actionResult != ContentDialogResult.None)
        {
            return;
        }

        if (actionResult == ContentDialogResult.Primary)
        {
            var confirmDialog = new ContentDialog
            {
                Title = $"¿Mover {selectedOrphans.Count} archivos a la papelera de Google Drive?",
                Content = new TextBlock
                {
                    Text = "Los archivos seleccionados se moverán a la papelera de Google Drive. Tendrás un plazo de 30 días para recuperarlos desde drive.google.com antes de su eliminación permanente.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Sí, mover a papelera",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            if (await confirmDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                setBusyState?.Invoke(true, "Moviendo archivos huérfanos a la papelera de Google Drive...");
                TrashResultSummary summary;
                try
                {
                    summary = await trashOrphansFunc(selectedOrphans);
                }
                finally
                {
                    setBusyState?.Invoke(false, string.Empty);
                }

                var outcomeDialog = new ContentDialog
                {
                    Title = summary.Success ? "Limpieza completada" : "Resultado con advertencias",
                    Content = new TextBlock
                    {
                        Text = $"Se enviaron {summary.SuccessfullyTrashed} archivos a la papelera de Drive.\n" +
                               (summary.NotFoundCount > 0 ? $"{summary.NotFoundCount} archivos ya no existían en Drive.\n" : "") +
                               (summary.ErrorCount > 0 ? $"Errores: {string.Join(", ", summary.ErrorMessages)}" : ""),
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = "Aceptar",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = xamlRoot
                };
                await outcomeDialog.ShowAsync();
            }
        }
        else if (actionResult == ContentDialogResult.Secondary)
        {
            dismissOrphansAction(selectedOrphans);
            var outcomeDialog = new ContentDialog
            {
                Title = "Seguimiento actualizado",
                Content = new TextBlock
                {
                    Text = $"Se dejaron de seguir {selectedOrphans.Count} archivos. Ya no se registrarán como huérfanos localmente.",
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "Aceptar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };
            await outcomeDialog.ShowAsync();
        }
    }

    public static async Task ShowDuplicatesInspectorDialogAsync(
        XamlRoot xamlRoot,
        Func<Task<DuplicateScanResult>> detectDuplicatesFunc,
        Func<IEnumerable<DuplicateFileItem>, bool, Task<DuplicateCleanupResultSummary>> deleteDuplicatesFunc,
        string dialogTitleSuffix = "",
        Action<bool, string>? setBusyState = null)
    {
        string scanMessage = string.IsNullOrWhiteSpace(dialogTitleSuffix)
            ? "Escaneando archivos duplicados..."
            : $"Escaneando archivos duplicados{dialogTitleSuffix}...";

        setBusyState?.Invoke(true, scanMessage);
        DuplicateScanResult scanResult;
        try
        {
            scanResult = await detectDuplicatesFunc();
        }
        finally
        {
            setBusyState?.Invoke(false, string.Empty);
        }

        if (scanResult.Groups.Count == 0)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 20, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] });
            panel.Children.Add(new TextBlock { Text = $"Escaneo completado ({scanResult.TotalScannedFiles} archivos analizados). No se encontraron copias duplicadas.", VerticalAlignment = VerticalAlignment.Center });

            var emptyDialog = new ContentDialog
            {
                Title = $"Inspector de archivos duplicados{dialogTitleSuffix}",
                Content = panel,
                CloseButtonText = "Cerrar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };
            await emptyDialog.ShowAsync();
            return;
        }

        var mainPanel = new StackPanel { Spacing = 10 };

        var kpiBorder = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10)
        };
        var kpiGrid = new Grid();
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var kpi1 = new StackPanel { Spacing = 2 };
        kpi1.Children.Add(new TextBlock { Text = $"{scanResult.TotalDuplicateGroups}", FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 16 });
        kpi1.Children.Add(new TextBlock { Text = "Grupos duplicados", FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        Grid.SetColumn(kpi1, 0);

        var kpi2 = new StackPanel { Spacing = 2 };
        kpi2.Children.Add(new TextBlock { Text = $"{scanResult.TotalDuplicateFiles}", FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 16 });
        kpi2.Children.Add(new TextBlock { Text = "Copias redundantes", FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        Grid.SetColumn(kpi2, 1);

        var kpi3 = new StackPanel { Spacing = 2 };
        kpi3.Children.Add(new TextBlock { Text = scanResult.FormattedTotalWastedBytes, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 16, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        kpi3.Children.Add(new TextBlock { Text = "Espacio redundante", FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        Grid.SetColumn(kpi3, 2);

        kpiGrid.Children.Add(kpi1);
        kpiGrid.Children.Add(kpi2);
        kpiGrid.Children.Add(kpi3);
        kpiBorder.Child = kpiGrid;
        mainPanel.Children.Add(kpiBorder);

        // Toolbar: Strategy Selector & Bulk Selection Controls
        var toolbarPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 4) };

        var ruleCombo = new ComboBox
        {
            Header = "Conservar original:",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ruleCombo.Items.Add(new ComboBoxItem { Content = "Más antiguo (Predeterminado)", Tag = DuplicatePrimaryRule.OldestModified });
        ruleCombo.Items.Add(new ComboBoxItem { Content = "Más reciente", Tag = DuplicatePrimaryRule.NewestModified });
        ruleCombo.Items.Add(new ComboBoxItem { Content = "Ruta más corta", Tag = DuplicatePrimaryRule.ShortestPath });
        ruleCombo.Items.Add(new ComboBoxItem { Content = "Evitar carpetas temp/backup", Tag = DuplicatePrimaryRule.PreferCleanPath });
        ruleCombo.SelectedIndex = 0;

        var selectAllCopiesBtn = new Button { Content = "Seleccionar copias", VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(8, 5, 8, 5) };
        var deselectAllBtn = new Button { Content = "Deseleccionar todo", VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(8, 5, 8, 5) };
        var invertBtn = new Button { Content = "Invertir selección", VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(8, 5, 8, 5) };

        toolbarPanel.Children.Add(ruleCombo);
        toolbarPanel.Children.Add(selectAllCopiesBtn);
        toolbarPanel.Children.Add(deselectAllBtn);
        toolbarPanel.Children.Add(invertBtn);
        mainPanel.Children.Add(toolbarPanel);

        var scroll = new ScrollViewer { MaxHeight = 380 };
        var stack = new StackPanel { Spacing = 10 };

        var fileRowControls = new List<(DuplicateFileItem File, CheckBox Cb, Border Badge, TextBlock BadgeText)>();

        void RefreshFileRows()
        {
            foreach (var (file, cb, badge, badgeText) in fileRowControls)
            {
                cb.IsEnabled = !file.IsPrimary;
                cb.IsChecked = file.IsSelected;

                if (file.IsPrimary)
                {
                    badge.Background = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                    badgeText.Text = "Original";
                    badgeText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                }
                else
                {
                    badge.Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
                    badgeText.Text = "Copia";
                    badgeText.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                }
            }
        }

        foreach (var group in scanResult.Groups)
        {
            var groupBorder = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8)
            };

            var groupStack = new StackPanel { Spacing = 6 };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var firstFile = group.Files.FirstOrDefault();
            var groupTitle = new TextBlock
            {
                Text = $"{firstFile?.FileName ?? "Archivo"} ({group.Files.Count} copias)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            Grid.SetColumn(groupTitle, 0);

            var wastedBadge = new TextBlock
            {
                Text = $"Desperdicio: {group.FormattedWastedBytes}",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            Grid.SetColumn(wastedBadge, 1);

            headerGrid.Children.Add(groupTitle);
            headerGrid.Children.Add(wastedBadge);
            groupStack.Children.Add(headerGrid);

            foreach (var file in group.Files)
            {
                var fileGrid = new Grid { Margin = new Thickness(4, 2, 0, 2) };
                fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox
                {
                    IsChecked = file.IsSelected,
                    IsEnabled = !file.IsPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                cb.Checked += (s, e) => file.IsSelected = true;
                cb.Unchecked += (s, e) => file.IsSelected = false;
                Grid.SetColumn(cb, 0);

                var badgeText = new TextBlock
                {
                    Text = file.IsPrimary ? "Original" : "Copia",
                    FontSize = 10,
                    Foreground = file.IsPrimary ? new SolidColorBrush(Microsoft.UI.Colors.White) : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                };
                var badge = new Border
                {
                    Background = file.IsPrimary ? (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Child = badgeText
                };
                Grid.SetColumn(badge, 1);

                var pathText = new TextBlock
                {
                    Text = file.FilePath,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(pathText, 2);

                // Open in Windows Explorer button
                var openBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE8DA", FontSize = 11 },
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(openBtn, "Abrir ubicación en el Explorador de Windows");
                openBtn.Click += (s, e) =>
                {
                    try
                    {
                        if (File.Exists(file.FilePath))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"/select,\"{file.FilePath}\"",
                                UseShellExecute = true
                            });
                        }
                        else
                        {
                            var dir = Path.GetDirectoryName(file.FilePath);
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "explorer.exe",
                                    Arguments = $"\"{dir}\"",
                                    UseShellExecute = true
                                });
                            }
                        }
                    }
                    catch { }
                };
                Grid.SetColumn(openBtn, 3);

                fileGrid.Children.Add(cb);
                fileGrid.Children.Add(badge);
                fileGrid.Children.Add(pathText);
                fileGrid.Children.Add(openBtn);

                groupStack.Children.Add(fileGrid);

                fileRowControls.Add((file, cb, badge, badgeText));
            }

            groupBorder.Child = groupStack;
            stack.Children.Add(groupBorder);
        }

        scroll.Content = stack;
        mainPanel.Children.Add(scroll);

        // Toolbar actions
        ruleCombo.SelectionChanged += (s, e) =>
        {
            if (ruleCombo.SelectedItem is ComboBoxItem item && item.Tag is DuplicatePrimaryRule rule)
            {
                scanResult.ApplyPrimaryRule(rule);
                RefreshFileRows();
            }
        };

        selectAllCopiesBtn.Click += (s, e) =>
        {
            foreach (var (file, cb, _, _) in fileRowControls)
            {
                if (!file.IsPrimary)
                {
                    file.IsSelected = true;
                    cb.IsChecked = true;
                }
            }
        };

        deselectAllBtn.Click += (s, e) =>
        {
            foreach (var (file, cb, _, _) in fileRowControls)
            {
                file.IsSelected = false;
                cb.IsChecked = false;
            }
        };

        invertBtn.Click += (s, e) =>
        {
            foreach (var (file, cb, _, _) in fileRowControls)
            {
                if (!file.IsPrimary)
                {
                    file.IsSelected = !file.IsSelected;
                    cb.IsChecked = file.IsSelected;
                }
            }
        };

        var dialog = new ContentDialog
        {
            Title = $"Inspector de duplicados ({scanResult.TotalDuplicateGroups} grupos, {scanResult.FormattedTotalWastedBytes} recuperables)",
            Content = mainPanel,
            PrimaryButtonText = "Mover seleccionados a papelera",
            CloseButtonText = "Cerrar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult == ContentDialogResult.Primary)
        {
            var selectedFiles = scanResult.Groups
                .SelectMany(g => g.Files)
                .Where(f => !f.IsPrimary && f.IsSelected)
                .ToList();

            if (selectedFiles.Count > 0)
            {
                var confirmDialog = new ContentDialog
                {
                    Title = $"¿Mover {selectedFiles.Count} copias a la papelera?",
                    Content = new TextBlock
                    {
                        Text = "Las copias seleccionadas se moverán a la Papelera de Reciclaje de Windows. Si ya estaban en Google Drive, también se moverán a la papelera de Drive con 30 días de recuperación.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = "Sí, mover a papelera",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot
                };

                if (await confirmDialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    setBusyState?.Invoke(true, "Moviendo copias duplicadas a la papelera...");
                    DuplicateCleanupResultSummary summary;
                    try
                    {
                        summary = await deleteDuplicatesFunc(selectedFiles, true);
                    }
                    finally
                    {
                        setBusyState?.Invoke(false, string.Empty);
                    }

                    var outcomeDialog = new ContentDialog
                    {
                        Title = summary.Success ? "Limpieza de duplicados completada" : "Resultado con advertencias",
                        Content = new TextBlock
                        {
                            Text = $"Se movieron {summary.LocalFilesRecycled} archivos a la Papelera de Reciclaje de Windows ({summary.FormattedBytesFreed} liberados)." +
                                   (summary.DriveFilesTrashed > 0 ? $"\nSe enviaron {summary.DriveFilesTrashed} archivos a la papelera de Google Drive." : "") +
                                   (summary.ErrorMessages.Count > 0 ? $"\nAdvertencias: {string.Join(", ", summary.ErrorMessages)}" : ""),
                            TextWrapping = TextWrapping.Wrap
                        },
                        CloseButtonText = "Aceptar",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = xamlRoot
                    };
                    await outcomeDialog.ShowAsync();
                }
            }
        }
    }
}
