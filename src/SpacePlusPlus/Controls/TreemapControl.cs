using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Controls;

/// <summary>ViewModel-facing item rendered by the treemap.</summary>
public sealed class TreemapItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long SizeBytes { get; init; }
    public long FileCount { get; init; }
    public bool IsAggregate { get; init; }

    /// <summary>Lazily built children so deep trees never materialize fully.</summary>
    public Func<IReadOnlyList<TreemapItem>?>? ChildrenProvider { private get; init; }

    private IReadOnlyList<TreemapItem>? _children;
    private bool _childrenResolved;

    public IReadOnlyList<TreemapItem>? Children
    {
        get
        {
            if (!_childrenResolved)
            {
                _children = ChildrenProvider?.Invoke();
                _childrenResolved = true;
            }
            return _children;
        }
    }

    public string SizeText => Infrastructure.SizeFormat.Format(SizeBytes);
}

public static class SquarifiedLayout
{
    /// <summary>
    /// Squarified treemap (Bruls et al.). Items are laid out in descending size order;
    /// rows are placed along the shorter side of the remaining rectangle.
    /// </summary>
    public static void Layout(IReadOnlyList<(TreemapItem Item, double Area)> scaled, Rect area, Action<TreemapItem, Rect> emit)
    {
        double total = 0;
        foreach (var s in scaled) total += s.Area;
        if (total <= 0 || area.Width <= 1 || area.Height <= 1) return;

        var items = scaled.Where(s => s.Area > 0).OrderByDescending(s => s.Area).ToList();
        if (items.Count == 0) return;

        // Scale to available pixel area.
        double factor = area.Width * area.Height / items.Sum(s => s.Area);
        var queue = new Queue<(TreemapItem Item, double Area)>(items.Select(i => (i.Item, i.Area * factor)));

        LayoutRest(queue, area, emit);
    }

    private static void LayoutRest(Queue<(TreemapItem Item, double Area)> rest, Rect area, Action<TreemapItem, Rect> emit)
    {
        var row = new List<(TreemapItem Item, double Area)>();
        while (rest.Count > 0)
        {
            double side = Math.Min(area.Width, area.Height);
            var candidate = rest.Peek();

            if (row.Count == 0)
            {
                row.Add(candidate);
                rest.Dequeue();
                continue;
            }

            double rowArea = row.Sum(r => r.Area);
            double worstCurrent = Worst(row, rowArea, side);
            double worstWithCandidate = Worst(row, rowArea + candidate.Area, side);

            if (worstWithCandidate <= worstCurrent)
            {
                row.Add(candidate);
                rest.Dequeue();
            }
            else
            {
                area = EmitRow(row, area, emit);
                row.Clear();
            }
        }
        if (row.Count > 0) EmitRow(row, area, emit);
    }

    private static Rect EmitRow(List<(TreemapItem Item, double Area)> row, Rect area, Action<TreemapItem, Rect> emit)
    {
        double rowArea = row.Sum(r => r.Area);
        if (rowArea <= 0 || area.Width <= 0 || area.Height <= 0) return area;

        if (area.Height < area.Width)
        {
            // Wider than tall: vertical strip anchored at the left edge.
            // Thickness runs along the width, items stack along the height.
            double thickness = rowArea / area.Height;
            double y = area.Y;
            foreach (var (item, a) in row)
            {
                double h = a / thickness;
                emit(item, new Rect(area.X, y, thickness, h));
                y += h;
            }
            return new Rect(area.X + thickness, area.Y, Math.Max(0, area.Width - thickness), area.Height);
        }
        else
        {
            // Taller than wide: horizontal strip anchored at the top edge.
            double thickness = rowArea / area.Width;
            double x = area.X;
            foreach (var (item, a) in row)
            {
                double w = a / thickness;
                emit(item, new Rect(x, area.Y, w, thickness));
                x += w;
            }
            return new Rect(area.X, area.Y + thickness, area.Width, Math.Max(0, area.Height - thickness));
        }
    }

    private static double Worst(List<(TreemapItem Item, double Area)> row, double totalArea, double side)
    {
        if (row.Count == 0 || totalArea <= 0 || side <= 0) return double.MaxValue;
        // Row thickness t = totalArea / side; each item's length = area / t.
        // Aspect ratio = max(length/t, t/length) = max(a/t², t²/a).
        double thickness = totalArea / side;
        double t2 = thickness * thickness;
        double worst = 0;
        foreach (var (_, a) in row)
        {
            if (a <= 0) return double.MaxValue;
            double ratio = Math.Max(a / t2, t2 / a);
            worst = Math.Max(worst, ratio);
        }
        return worst;
    }
}

/// <summary>
/// Interactive treemap rendered as composited Border elements (capped element budget).
/// Clicking a directory raises ItemActivated for navigation.
/// </summary>
public class TreemapControl : Canvas
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<TreemapItem>), typeof(TreemapControl),
        new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty RootSizeProperty = DependencyProperty.Register(
        nameof(RootSize), typeof(long), typeof(TreemapControl),
        new PropertyMetadata(0L, OnDataChanged));

    public static readonly DependencyProperty MinItemRatioProperty = DependencyProperty.Register(
        nameof(MinItemRatio), typeof(double), typeof(TreemapControl),
        new PropertyMetadata(0.002, OnDataChanged));

    public event EventHandler<TreemapItem>? ItemActivated;

    private static readonly int MaxElements = 750;

    public IEnumerable<TreemapItem>? ItemsSource
    {
        get => (IEnumerable<TreemapItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public long RootSize
    {
        get => (long)GetValue(RootSizeProperty);
        set => SetValue(RootSizeProperty, value);
    }

    public double MinItemRatio
    {
        get => (double)GetValue(MinItemRatioProperty);
        set => SetValue(MinItemRatioProperty, value);
    }

    private bool _pending;

    public TreemapControl()
    {
        Background = Brushes.Transparent; // required for hit testing and background clicks
        SizeChanged += (_, _) => ScheduleRebuild();
        DataContextChanged += (_, _) => ScheduleRebuild();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (TreemapControl)d;
        c.ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        if (_pending) return;
        _pending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pending = false;
            Rebuild();
        }));
    }

    private void Rebuild()
    {
        Children.Clear();
        var items = ItemsSource?.ToList();
        if (items is null || items.Count == 0 || ActualWidth < 20 || ActualHeight < 20 || RootSize <= 0)
        {
            var empty = new TextBlock
            {
                Text = "No scan data yet — run a scan to populate the storage map.",
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetLeft(empty, ActualWidth / 2 - 160);
            SetTop(empty, ActualHeight / 2 - 10);
            Children.Add(empty);
            return;
        }

        double minBytes = RootSize * Math.Clamp(MinItemRatio, 0, 1);
        int budget = MaxElements;
        var visible = new List<(TreemapItem Item, double Area)>();
        long remainderBytes = 0;
        int remainderCount = 0;

        foreach (var item in items)
        {
            if (item.SizeBytes < minBytes)
            {
                remainderBytes += item.SizeBytes;
                remainderCount++;
                continue;
            }
            double share = (double)item.SizeBytes / RootSize;
            visible.Add((item, share));
        }

        double availableArea = ActualWidth * ActualHeight;
        double totalShare = visible.Sum(v => v.Area);
        if (remainderBytes > 0)
        {
            double remShare = (double)remainderBytes / RootSize;
            if (remShare > 0)
            {
                double remPixels = remShare / totalShare * (availableArea - 0);
                // Keep remainder as its own aggregate entry when it would occupy visible space.
                if (remainderCount > 0 && remPixels > 120)
                    visible.Add((new TreemapItem
                    {
                        Name = $"{remainderCount} smaller items",
                        Path = string.Empty,
                        IsDirectory = false,
                        SizeBytes = remainderBytes,
                        IsAggregate = true
                    }, remShare));
            }
        }

        double used = 0;
        SquarifiedLayout.Layout(visible, new Rect(0, 0, ActualWidth, ActualHeight), (item, rect) =>
        {
            used += rect.Width * rect.Height;
            AddItem(item, rect, 0, ref budget, minBytes, RootSize);
        });
    }

    private void AddItem(TreemapItem item, Rect rect, int depth, ref int budget, double minBytes, double parentSize)
    {
        if (budget <= 0) return;
        budget--;

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(1),
            Background = BrushFor(item, parentSize),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Child = BuildLabel(item, rect),
            ToolTip = BuildTooltip(item)
        };

        border.MouseEnter += (_, _) => border.BorderBrush = (Brush)FindResource("Accent");
        border.MouseLeave += (_, _) => border.BorderBrush = Brushes.Transparent;

        if (item.IsDirectory)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ItemActivated?.Invoke(this, item);
            };
        }
        else if (!item.IsAggregate)
        {
            border.Cursor = Cursors.Arrow;
            border.MouseLeftButtonUp += (_, e) => e.Handled = true;
        }

        AttachContextMenu(item, border);

        SetLeft(border, rect.Left);
        SetTop(border, rect.Top);
        border.Width = Math.Max(0, rect.Width - 2);
        border.Height = Math.Max(0, rect.Height - 2);
        Children.Add(border);

        if (item.IsDirectory && item.Children is { Count: > 0 } && depth < 6 && budget > 40 &&
            rect.Width > 52 && rect.Height > LabelHeight(rect) + 26)
        {
            // Reserve the label strip so nested rectangles never cover the text.
            double lh = LabelHeight(rect);
            var inner = new Rect(rect.X + 5, rect.Y + lh, rect.Width - 10, rect.Height - lh - 4);
            if (inner.Width > 20 && inner.Height > 20)
            {
                double childMinBytes = item.SizeBytes * 0.004;
                var childVisible = new List<(TreemapItem, double)>();
                long childTotal = item.Children.Sum(c => c.SizeBytes);
                foreach (var child in item.Children)
                {
                    if (child.SizeBytes < Math.Max(minBytes / 8, childMinBytes)) continue;
                    childVisible.Add((child, childTotal <= 0 ? 0 : (double)child.SizeBytes / childTotal));
                }
                int childBudget = budget;
                SquarifiedLayout.Layout(childVisible, inner, (child, childRect) =>
                    AddItem(child, childRect, depth + 1, ref childBudget, minBytes, item.SizeBytes));
                budget = childBudget;
            }
        }
    }

    /// <summary>True when the item rect is large enough for a name + size label.</summary>
    private static bool ShowSizeLine(Rect rect) => rect.Height > 56 && rect.Width > 64;

    /// <summary>Height of the reserved label strip; nested layout must start below it.</summary>
    private static double LabelHeight(Rect rect) => ShowSizeLine(rect) ? 36 : 22;

    private UIElement? BuildLabel(TreemapItem item, Rect rect)
    {
        if (rect.Width < 36 || rect.Height < 20) return null;
        var stack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5, 4, 5, 0) };
        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = item.IsDirectory ? 11 : 10.5,
            FontWeight = item.IsDirectory ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource(item.IsDirectory ? "TextPrimary" : "TextSecondary")
        };
        stack.Children.Add(name);
        if (ShowSizeLine(rect))
        {
            var size = new TextBlock
            {
                Text = item.SizeText + (item.IsAggregate ? "" : (item.IsDirectory ? $" — {item.FileCount} files" : "")),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(size);
        }
        return stack;
    }

    private object BuildTooltip(TreemapItem item)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380
        });
        var line = item.IsDirectory
            ? $"{item.SizeText} — {item.FileCount} files"
            : item.SizeText;
        stack.Children.Add(new TextBlock
        {
            Text = line,
            Foreground = (Brush)FindResource("TextSecondary")
        });
        if (!item.IsAggregate && !string.IsNullOrEmpty(item.Path))
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.Path,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380
            });
        }
        if (item.IsAggregate)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Aggregated small items below the display threshold.",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextMuted")
            });
        }
        return stack;
    }

    private Brush BrushFor(TreemapItem item, double parentSize)
    {
        if (item.IsAggregate)
            return (Brush)FindResource("TreemapAggregate");

        // Relative to the enclosing folder so every nesting level shows contrast
        // (global ratio would make deep items uniformly flat).
        double rel = parentSize <= 0 ? 0 : Math.Clamp((double)item.SizeBytes / parentSize, 0, 1);
        byte intensity = (byte)(Math.Sqrt(rel) * 255);
        static Color Lerp(Color low, Color high, byte t) => Color.FromRgb(
            (byte)(low.R + (high.R - low.R) * t / 255),
            (byte)(low.G + (high.G - low.G) * t / 255),
            (byte)(low.B + (high.B - low.B) * t / 255));

        if (item.IsDirectory)
        {
            var low = ((SolidColorBrush)FindResource("TreemapDirLow")).Color;
            var high = ((SolidColorBrush)FindResource("TreemapDirHigh")).Color;
            return new SolidColorBrush(Lerp(low, high, intensity));
        }
        var fileLow = ((SolidColorBrush)FindResource("TreemapFileLow")).Color;
        var fileHigh = ((SolidColorBrush)FindResource("TreemapFileHigh")).Color;
        return new SolidColorBrush(Lerp(fileLow, fileHigh, intensity));
    }

    private void AttachContextMenu(TreemapItem item, FrameworkElement element)
    {
        var menu = new ContextMenu();
        var vm = DataContext;

        if (item.IsDirectory && !item.IsAggregate)
        {
            AddItem(menu, "Navigate here", () => ItemActivated?.Invoke(this, item));
        }
        AddItem(menu, "Open location", () => (vm as ICommandProvider)?.OpenLocation.Execute(item.Path));
        AddItem(menu, "Copy path", () => (vm as ICommandProvider)?.CopyPath.Execute(item.Path));
        if (!item.IsDirectory && !item.IsAggregate)
        {
            AddItem(menu, "Delete…", () => (vm as ICommandProvider)?.DeleteItem.Execute(item));
        }

        element.ContextMenu = menu;
    }

    private static void AddItem(ContextMenu menu, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }

    public interface ICommandProvider
    {
        ICommand OpenLocation { get; }
        ICommand CopyPath { get; }
        ICommand DeleteItem { get; }
    }
}
