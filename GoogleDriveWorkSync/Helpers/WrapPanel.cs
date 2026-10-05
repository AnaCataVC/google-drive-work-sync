using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace GoogleDriveWorkSync.Helpers;

/// <summary>
/// Flow layout panel that arranges children horizontally from left to right with uniform spacing,
/// wrapping to the next row when available width is exceeded.
/// </summary>
public class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(WrapPanel),
            new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(WrapPanel),
            new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double hSpacing = HorizontalSpacing;
        double vSpacing = VerticalSpacing;

        double currentRowWidth = 0;
        double currentRowHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;
        int itemsInRow = 0;

        foreach (UIElement child in Children)
        {
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            Size childSize = child.DesiredSize;

            if (childSize.Width == 0 && childSize.Height == 0)
                continue;

            double requiredWidth = itemsInRow == 0 ? childSize.Width : currentRowWidth + hSpacing + childSize.Width;

            if (itemsInRow > 0 && requiredWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, currentRowWidth);
                totalHeight += currentRowHeight + vSpacing;
                currentRowWidth = childSize.Width;
                currentRowHeight = childSize.Height;
                itemsInRow = 1;
            }
            else
            {
                currentRowWidth = requiredWidth;
                currentRowHeight = Math.Max(currentRowHeight, childSize.Height);
                itemsInRow++;
            }
        }

        if (itemsInRow > 0)
        {
            totalWidth = Math.Max(totalWidth, currentRowWidth);
            totalHeight += currentRowHeight;
        }

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double maxWidth = finalSize.Width;
        double hSpacing = HorizontalSpacing;
        double vSpacing = VerticalSpacing;

        double x = 0;
        double y = 0;
        double currentRowHeight = 0;
        int itemsInRow = 0;

        foreach (UIElement child in Children)
        {
            Size childSize = child.DesiredSize;
            if (childSize.Width == 0 && childSize.Height == 0)
                continue;

            if (itemsInRow > 0 && x + childSize.Width > maxWidth)
            {
                x = 0;
                y += currentRowHeight + vSpacing;
                currentRowHeight = 0;
                itemsInRow = 0;
            }

            child.Arrange(new Rect(x, y, childSize.Width, childSize.Height));
            x += childSize.Width + hSpacing;
            currentRowHeight = Math.Max(currentRowHeight, childSize.Height);
            itemsInRow++;
        }

        return finalSize;
    }
}

/// <summary>
/// Arranges children in a fixed number of equal-width columns so multi-row items align vertically.
/// </summary>
public class UniformColumnsPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(UniformColumnsPanel),
            new PropertyMetadata(4, OnLayoutPropertyChanged));

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(UniformColumnsPanel),
            new PropertyMetadata(12.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(UniformColumnsPanel),
            new PropertyMetadata(6.0, OnLayoutPropertyChanged));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UniformColumnsPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int cols = Math.Max(1, Columns);
        int count = Children.Count;
        if (count == 0)
            return new Size(0, 0);

        double hSpacing = HorizontalSpacing;
        double vSpacing = VerticalSpacing;
        bool hasFiniteWidth = !double.IsInfinity(availableSize.Width) && availableSize.Width > 0;
        double colWidth = hasFiniteWidth
            ? Math.Max(0, (availableSize.Width - hSpacing * (cols - 1)) / cols)
            : double.PositiveInfinity;

        int rows = (count + cols - 1) / cols;
        double totalHeight = 0;
        double maxDesiredColWidth = 0;

        for (int r = 0; r < rows; r++)
        {
            double rowHeight = 0;
            for (int c = 0; c < cols; c++)
            {
                int index = r * cols + c;
                if (index >= count) break;

                UIElement child = Children[index];
                child.Measure(new Size(colWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                maxDesiredColWidth = Math.Max(maxDesiredColWidth, child.DesiredSize.Width);
            }

            totalHeight += rowHeight;
            if (r > 0)
                totalHeight += vSpacing;
        }

        double totalWidth = hasFiniteWidth
            ? availableSize.Width
            : maxDesiredColWidth * cols + hSpacing * (cols - 1);

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Math.Max(1, Columns);
        int count = Children.Count;
        if (count == 0)
            return finalSize;

        double hSpacing = HorizontalSpacing;
        double vSpacing = VerticalSpacing;
        double colWidth = Math.Max(0, (finalSize.Width - hSpacing * (cols - 1)) / cols);

        int rows = (count + cols - 1) / cols;
        double y = 0;

        for (int r = 0; r < rows; r++)
        {
            double rowHeight = 0;
            for (int c = 0; c < cols; c++)
            {
                int index = r * cols + c;
                if (index >= count) break;
                rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);
            }

            for (int c = 0; c < cols; c++)
            {
                int index = r * cols + c;
                if (index >= count) break;

                double x = c * (colWidth + hSpacing);
                Children[index].Arrange(new Rect(x, y, colWidth, rowHeight));
            }

            y += rowHeight + vSpacing;
        }

        return finalSize;
    }
}
