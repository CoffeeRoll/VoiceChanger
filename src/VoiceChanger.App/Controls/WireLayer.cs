using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VoiceChanger.App.ViewModels;

namespace VoiceChanger.App.Controls;

/// <summary>Draws the wires between node sockets as smooth curves, plus the one being dragged.</summary>
public sealed class WireLayer : Control
{
    public static readonly StyledProperty<IEnumerable<WireViewModel>?> WiresProperty =
        AvaloniaProperty.Register<WireLayer, IEnumerable<WireViewModel>?>(nameof(Wires));

    private static readonly IPen ShadowPen = new Pen(new SolidColorBrush(Color.Parse("#0B0D11")), 5.5, lineCap: PenLineCap.Round);
    private static readonly IPen WirePen = new Pen(new SolidColorBrush(Color.Parse("#7F8AA0")), 2.2, lineCap: PenLineCap.Round);
    private static readonly IPen HighlightPen = new Pen(new SolidColorBrush(Color.Parse("#22D3EE")), 2.6, lineCap: PenLineCap.Round);
    private static readonly IPen PendingPen = new Pen(new SolidColorBrush(Color.Parse("#22D3EE")), 2.2, new DashStyle([3, 2], 0), PenLineCap.Round);

    private (Point Start, Point End)? _pending;

    public WireLayer()
    {
        IsHitTestVisible = false;
    }

    public IEnumerable<WireViewModel>? Wires
    {
        get => GetValue(WiresProperty);
        set => SetValue(WiresProperty, value);
    }

    /// <summary>The wire being dragged, from an output (left end) to an input (right end).</summary>
    public (Point Start, Point End)? Pending
    {
        get => _pending;
        set
        {
            _pending = value;
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WiresProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= OnWiresChanged;
            }

            if (change.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += OnWiresChanged;
            }

            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var wires = Wires?.ToList() ?? [];

        // Highlighted wires last so they are drawn on top.
        foreach (var wire in wires.OrderBy(w => w.IsHighlighted))
        {
            DrawWire(context, wire.From.Anchor, wire.To.Anchor, wire.IsHighlighted ? HighlightPen : WirePen);
        }

        if (_pending is { } pending)
        {
            DrawWire(context, pending.Start, pending.End, PendingPen);
        }
    }

    private static void DrawWire(DrawingContext context, Point start, Point end, IPen pen)
    {
        // Leave each socket horizontally, like the wires in Blender's node editors.
        double bend = Math.Max(40, Math.Abs(end.X - start.X) * 0.5);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(start, isFilled: false);
            figure.CubicBezierTo(start + new Point(bend, 0), end - new Point(bend, 0), end);
            figure.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, ShadowPen, geometry);
        context.DrawGeometry(null, pen, geometry);
    }

    private void OnWiresChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}
