using System;
using System.Collections.Generic;
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
        string dialogTitleSuffix = "")
    {
        var orphans = (await detectOrphansFunc()).ToList();
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

        var scroll = new ScrollViewer { MaxHeight = 380 };
        var stack = new StackPanel { Spacing = 8 };

        var headerInfo = new TextBlock
        {
            Text = "Los siguientes archivos fueron eliminados o movidos en tu equipo local, pero siguen existiendo en Google Drive. Puedes enviarlos a la papelera de Drive o dejar de seguirlos localmente.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(headerInfo);

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

            var cb = new CheckBox
            {
                IsChecked = orphan.IsSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            cb.Checked += (s, e) => orphan.IsSelected = true;
            cb.Unchecked += (s, e) => orphan.IsSelected = false;
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
            Grid.SetColumn(textStack, 1);

            itemGrid.Children.Add(cb);
            itemGrid.Children.Add(textStack);

            border.Child = itemGrid;
            stack.Children.Add(border);
        }

        scroll.Content = stack;

        var dialog = new ContentDialog
        {
            Title = $"Sugerencias de limpieza ({orphans.Count} archivos huérfanos)",
            Content = scroll,
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
                var summary = await trashOrphansFunc(selectedOrphans);
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
        }
    }

    public static async Task ShowDuplicatesInspectorDialogAsync(
        XamlRoot xamlRoot,
        Func<Task<DuplicateScanResult>> detectDuplicatesFunc,
        Func<IEnumerable<DuplicateFileItem>, bool, Task<DuplicateCleanupResultSummary>> deleteDuplicatesFunc,
        string dialogTitleSuffix = "")
    {
        var scanResult = await detectDuplicatesFunc();

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

        var scroll = new ScrollViewer { MaxHeight = 400 };
        var stack = new StackPanel { Spacing = 10 };

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
        stack.Children.Add(kpiBorder);

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
                var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 2, 0, 2) };

                var cb = new CheckBox
                {
                    IsChecked = file.IsSelected,
                    IsEnabled = !file.IsPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                cb.Checked += (s, e) => file.IsSelected = true;
                cb.Unchecked += (s, e) => file.IsSelected = false;
                fileRow.Children.Add(cb);

                if (file.IsPrimary)
                {
                    var primaryBadge = new Border
                    {
                        Background = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 1, 4, 1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = "Original", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
                    };
                    fileRow.Children.Add(primaryBadge);
                }
                else
                {
                    var copyBadge = new Border
                    {
                        Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 1, 4, 1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = "Copia", FontSize = 10 }
                    };
                    fileRow.Children.Add(copyBadge);
                }

                fileRow.Children.Add(new TextBlock
                {
                    Text = file.FilePath,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });

                groupStack.Children.Add(fileRow);
            }

            groupBorder.Child = groupStack;
            stack.Children.Add(groupBorder);
        }

        scroll.Content = stack;

        var dialog = new ContentDialog
        {
            Title = $"Inspector de duplicados ({scanResult.TotalDuplicateGroups} grupos, {scanResult.FormattedTotalWastedBytes} recuperables)",
            Content = scroll,
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
                    var summary = await deleteDuplicatesFunc(selectedFiles, true);
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
