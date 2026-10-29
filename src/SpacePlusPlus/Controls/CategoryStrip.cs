using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpacePlusPlus.Controls;

public sealed class CategorySegment
{
    public required string Label { get; init; }
    public required long Bytes { get; init; }
    public required string ColorHex { get; init; }
}

/// <summary>Proportional single-row bar of category shares, drawn directly for performance.</summary>
public class CategoryStrip : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<CategorySegment>), typeof(CategoryStrip),
        new PropertyMetadata(null, static (d, _) => ((CategoryStrip)d).InvalidateVisual()));

    public IReadOnlyList<CategorySegment>? Segments
    {
        get => (IReadOnlyList<CategorySegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private readonly List<(Rect Rect, CategorySegment Segment)> _hitRects = [];

    public CategoryStrip()
    {
        SnapsToDevicePixels = true;
        MinHeight = 10;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hitRects.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var segments = Segments;
        if (segments is null || segments.Count == 0 || ActualWidth < 4) return;

        long total = segments.Sum(s => s.Bytes);
        if (total <= 0) return;

        const double gap = 2;
        double usable = Math.Max(0, ActualWidth - gap * (segments.Count - 1));
        double x = 0;
        var clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushClip(clip);
        foreach (var segment in segments)
        {
            double w = usable * segment.Bytes / (double)total;
            if (w < 1) { x += w + gap; continue; }
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(segment.ColorHex));
            var rect = new Rect(x, 0, w, Math.Max(1, ActualHeight));
            dc.PushClip(new RectangleGeometry(rect, 3, 3));
            dc.DrawRectangle(brush, null, rect);
            dc.Pop();
            _hitRects.Add((rect, segment));
            x += w + gap;
        }
        dc.Pop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        foreach (var (rect, segment) in _hitRects)
        {
            if (rect.Contains(pos))
            {
                long total = Segments?.Sum(s => s.Bytes) ?? 0;
                ToolTip = new ToolTip
                {
                    Content = new TextBlock
                    {
                        Inlines =
                        {
                            new System.Windows.Documents.Run($"{segment.Label}: ") { FontWeight = FontWeights.SemiBold },
                            new System.Windows.Documents.Run($"{Infrastructure.SizeFormat.Format(segment.Bytes)} ({segment.Bytes * 100.0 / Math.Max(1, total):F1}%)")
                        }
                    }
                };
                return;
            }
        }
    }
}
