using Avalonia;
using Avalonia.Controls;

namespace ProxyDock.App.Controls;

/// <summary>
/// Адаптивная сетка карточек лаунчера: автоматически подбирает число колонок (от 1 до MaxColumns)
/// по доступной ширине окна и выравнивает высоту всех карточек по единому значению без обрезания контента.
/// </summary>
public sealed class AdaptiveCardGridPanel : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<AdaptiveCardGridPanel, double>(nameof(MinColumnWidth), 232.0);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<AdaptiveCardGridPanel, int>(nameof(MaxColumns), 4);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<AdaptiveCardGridPanel, double>(nameof(ColumnSpacing), 14.0);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<AdaptiveCardGridPanel, double>(nameof(RowSpacing), 14.0);

    public static readonly StyledProperty<double> MinCardHeightProperty =
        AvaloniaProperty.Register<AdaptiveCardGridPanel, double>(nameof(MinCardHeight), 162.0);

    static AdaptiveCardGridPanel()
    {
        AffectsMeasure<AdaptiveCardGridPanel>(
            MinColumnWidthProperty,
            MaxColumnsProperty,
            ColumnSpacingProperty,
            RowSpacingProperty,
            MinCardHeightProperty);
        AffectsArrange<AdaptiveCardGridPanel>(
            MinColumnWidthProperty,
            MaxColumnsProperty,
            ColumnSpacingProperty,
            RowSpacingProperty,
            MinCardHeightProperty);
    }

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public double MinCardHeight
    {
        get => GetValue(MinCardHeightProperty);
        set => SetValue(MinCardHeightProperty, value);
    }

    public static int CalculateColumnCount(double availableWidth, double minColumnWidth, double columnSpacing, int maxColumns)
    {
        var clampedMax = Math.Max(1, maxColumns);
        var effectiveMinWidth = Math.Max(120.0, minColumnWidth);
        var spacing = Math.Max(0.0, columnSpacing);

        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return 1;
        }

        var rawCols = (int)Math.Floor((availableWidth + spacing) / (effectiveMinWidth + spacing));
        return Math.Clamp(rawCols, 1, clampedMax);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = new List<Control>(Children.Count);
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                visible.Add(child);
            }
        }

        if (visible.Count == 0)
        {
            return default;
        }

        var colSpacing = Math.Max(0.0, ColumnSpacing);
        var rowSpacing = Math.Max(0.0, RowSpacing);
        var columns = CalculateColumnCount(availableSize.Width, MinColumnWidth, colSpacing, MaxColumns);

        var totalSpacingWidth = (columns - 1) * colSpacing;
        var cardWidth = double.IsInfinity(availableSize.Width)
            ? Math.Max(120.0, MinColumnWidth)
            : Math.Max(100.0, (availableSize.Width - totalSpacingWidth) / columns);

        var uniformHeight = Math.Max(80.0, MinCardHeight);
        var probeConstraint = new Size(cardWidth, double.PositiveInfinity);

        foreach (var child in visible)
        {
            child.Measure(probeConstraint);
            if (child.DesiredSize.Height > uniformHeight)
            {
                uniformHeight = child.DesiredSize.Height;
            }
        }

        uniformHeight = Math.Ceiling(uniformHeight);
        var exactConstraint = new Size(cardWidth, uniformHeight);
        foreach (var child in visible)
        {
            child.Measure(exactConstraint);
        }

        var rows = (visible.Count + columns - 1) / columns;
        var totalHeight = (rows * uniformHeight) + ((rows - 1) * rowSpacing);
        var desiredWidth = double.IsInfinity(availableSize.Width)
            ? (columns * cardWidth) + totalSpacingWidth
            : availableSize.Width;

        return new Size(desiredWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = new List<Control>(Children.Count);
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                visible.Add(child);
            }
        }

        if (visible.Count == 0)
        {
            return finalSize;
        }

        var colSpacing = Math.Max(0.0, ColumnSpacing);
        var rowSpacing = Math.Max(0.0, RowSpacing);
        var columns = CalculateColumnCount(finalSize.Width, MinColumnWidth, colSpacing, MaxColumns);
        var cardWidth = Math.Max(100.0, (finalSize.Width - ((columns - 1) * colSpacing)) / columns);

        var uniformHeight = Math.Max(80.0, MinCardHeight);
        foreach (var child in visible)
        {
            if (child.DesiredSize.Height > uniformHeight)
            {
                uniformHeight = child.DesiredSize.Height;
            }
        }

        uniformHeight = Math.Ceiling(uniformHeight);

        for (var index = 0; index < visible.Count; index++)
        {
            var col = index % columns;
            var row = index / columns;
            var x = col * (cardWidth + colSpacing);
            var y = row * (uniformHeight + rowSpacing);
            visible[index].Arrange(new Rect(x, y, cardWidth, uniformHeight));
        }

        var rows = (visible.Count + columns - 1) / columns;
        var arrangedHeight = (rows * uniformHeight) + ((rows - 1) * rowSpacing);
        return new Size(finalSize.Width, Math.Max(finalSize.Height, arrangedHeight));
    }
}
