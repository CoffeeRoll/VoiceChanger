using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceChanger.App.Controls;

/// <summary>Draws an equalizer's magnitude response on a logarithmic frequency axis.</summary>
public sealed class FrequencyResponseView : Control
{
    public static readonly StyledProperty<double[]?> FrequenciesProperty =
        AvaloniaProperty.Register<FrequencyResponseView, double[]?>(nameof(Frequencies));

    public static readonly StyledProperty<double[]?> ResponseProperty =
        AvaloniaProperty.Register<FrequencyResponseView, double[]?>(nameof(Response));

    private const double MinFrequency = 20;
    private const double MaxFrequency = 20000;
    private const double RangeDb = 15;
    private const double LabelGutter = 18;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#14171D"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#262B36")), 1);
    private static readonly IPen ZeroPen = new Pen(new SolidColorBrush(Color.Parse("#3A4150")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6B7385"));
    private static readonly IPen CurvePen = new Pen(new SolidColorBrush(Color.Parse("#9D86FF")), 2, lineJoin: PenLineJoin.Round);
    private static readonly IBrush CurveFill = new SolidColorBrush(Color.Parse("#7C5CFF"), 0.18);
    private static readonly Typeface LabelFace = new(FontFamily.Default);

    static FrequencyResponseView()
    {
        AffectsRender<FrequencyResponseView>(FrequenciesProperty, ResponseProperty);
    }

    public double[]? Frequencies
    {
        get => GetValue(FrequenciesProperty);
        set => SetValue(FrequenciesProperty, value);
    }

    /// <summary>Gain in dB for each entry of <see cref="Frequencies"/>.</summary>
    public double[]? Response
    {
        get => GetValue(ResponseProperty);
        set => SetValue(ResponseProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(BackgroundBrush, null, bounds, 8, 8);

        var plot = new Rect(8, 8, Math.Max(0, bounds.Width - 16), Math.Max(0, bounds.Height - 16 - LabelGutter));
        if (plot.Width < 10 || plot.Height < 10)
        {
            return;
        }

        foreach (double db in new[] { -12.0, -6, 6, 12 })
        {
            double y = YFor(db, plot);
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        double zero = YFor(0, plot);
        context.DrawLine(ZeroPen, new Point(plot.Left, zero), new Point(plot.Right, zero));

        foreach (var (hz, label) in new[] { (50.0, "50"), (100.0, "100"), (500.0, "500"), (1000.0, "1k"), (5000.0, "5k"), (10000.0, "10k") })
        {
            double x = XFor(hz, plot);
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, LabelBrush);
            context.DrawText(text, new Point(x - text.Width / 2, plot.Bottom + 4));
        }

        foreach (var (db, label) in new[] { (12.0, "+12"), (-12.0, "-12") })
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, LabelBrush);
            context.DrawText(text, new Point(plot.Left + 2, YFor(db, plot) - text.Height - 1));
        }

        var frequencies = Frequencies;
        var response = Response;
        if (frequencies is null || response is null || frequencies.Length < 2 || response.Length != frequencies.Length)
        {
            return;
        }

        var curve = new StreamGeometry();
        var area = new StreamGeometry();
        using (var line = curve.Open())
        using (var fill = area.Open())
        {
            var first = new Point(XFor(frequencies[0], plot), YFor(response[0], plot));
            line.BeginFigure(first, isFilled: false);
            fill.BeginFigure(new Point(first.X, zero), isFilled: true);
            fill.LineTo(first);
            for (int i = 1; i < frequencies.Length; i++)
            {
                var point = new Point(XFor(frequencies[i], plot), YFor(response[i], plot));
                line.LineTo(point);
                fill.LineTo(point);
            }

            fill.LineTo(new Point(XFor(frequencies[^1], plot), zero));
            line.EndFigure(isClosed: false);
            fill.EndFigure(isClosed: true);
        }

        using (context.PushClip(plot))
        {
            context.DrawGeometry(CurveFill, null, area);
            context.DrawGeometry(null, CurvePen, curve);
        }
    }

    private static double XFor(double hz, Rect plot) =>
        plot.Left + plot.Width * Math.Log(Math.Clamp(hz, MinFrequency, MaxFrequency) / MinFrequency) / Math.Log(MaxFrequency / MinFrequency);

    private static double YFor(double db, Rect plot) =>
        plot.Top + plot.Height * (1 - (Math.Clamp(db, -RangeDb * 1.5, RangeDb * 1.5) + RangeDb) / (2 * RangeDb));
}
