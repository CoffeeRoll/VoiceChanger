using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceChanger.App.Controls;

/// <summary>Background grid of the node editor that pans and zooms with the canvas.</summary>
public sealed class CanvasGrid : Control
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<CanvasGrid, double>(nameof(Zoom), 1);

    public static readonly StyledProperty<double> PanXProperty =
        AvaloniaProperty.Register<CanvasGrid, double>(nameof(PanX));

    public static readonly StyledProperty<double> PanYProperty =
        AvaloniaProperty.Register<CanvasGrid, double>(nameof(PanY));

    private const double Cell = 24;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0C0E12"));
    private static readonly IPen MinorPen = new Pen(new SolidColorBrush(Color.Parse("#151820")), 1);
    private static readonly IPen MajorPen = new Pen(new SolidColorBrush(Color.Parse("#1D212B")), 1);

    static CanvasGrid()
    {
        AffectsRender<CanvasGrid>(ZoomProperty, PanXProperty, PanYProperty);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double PanX
    {
        get => GetValue(PanXProperty);
        set => SetValue(PanXProperty, value);
    }

    public double PanY
    {
        get => GetValue(PanYProperty);
        set => SetValue(PanYProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        double step = Cell * Zoom;
        if (step < 6)
        {
            return;
        }

        // Every fifth line is brighter.
        int firstColumn = (int)Math.Floor(-PanX / step);
        for (int i = firstColumn; i * step + PanX <= bounds.Width; i++)
        {
            double x = Math.Round(i * step + PanX) + 0.5;
            context.DrawLine(i % 5 == 0 ? MajorPen : MinorPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        int firstRow = (int)Math.Floor(-PanY / step);
        for (int i = firstRow; i * step + PanY <= bounds.Height; i++)
        {
            double y = Math.Round(i * step + PanY) + 0.5;
            context.DrawLine(i % 5 == 0 ? MajorPen : MinorPen, new Point(0, y), new Point(bounds.Width, y));
        }
    }
}
