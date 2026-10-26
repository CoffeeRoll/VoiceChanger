using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceChanger.App.Controls;

/// <summary>Tuner style needle showing how far a pitch is from its note (−50 to +50 cents).</summary>
public sealed class TunerMeter : Control
{
    public static readonly StyledProperty<double> CentsProperty =
        AvaloniaProperty.Register<TunerMeter, double>(nameof(Cents));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TunerMeter, bool>(nameof(IsActive));

    private const double Range = 50;
    private const double InTune = 10;

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#14171D"));
    private static readonly IBrush InTuneBrush = new SolidColorBrush(Color.Parse("#3BD671"), 0.16);
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#3A4150")), 1);
    private static readonly IPen CentrePen = new Pen(new SolidColorBrush(Color.Parse("#3BD671"), 0.7), 1.5);
    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#3BD671"));
    private static readonly IBrush CloseBrush = new SolidColorBrush(Color.Parse("#F2C94C"));
    private static readonly IBrush FarBrush = new SolidColorBrush(Color.Parse("#F2994A"));

    static TunerMeter()
    {
        AffectsRender<TunerMeter>(CentsProperty, IsActiveProperty);
    }

    public double Cents
    {
        get => GetValue(CentsProperty);
        set => SetValue(CentsProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 24);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(TrackBrush, null, bounds, 6, 6);

        double XFor(double cents) => bounds.Width / 2 + cents / Range * (bounds.Width / 2 - 6);

        double inTuneLeft = XFor(-InTune);
        context.DrawRectangle(InTuneBrush, null, new Rect(inTuneLeft, 0, XFor(InTune) - inTuneLeft, bounds.Height));

        foreach (double cents in new[] { -40.0, -30, -20, -10, 10, 20, 30, 40 })
        {
            double x = XFor(cents);
            double inset = Math.Abs(cents) % 20 == 0 ? bounds.Height * 0.3 : bounds.Height * 0.4;
            context.DrawLine(TickPen, new Point(x, inset), new Point(x, bounds.Height - inset));
        }

        double centre = XFor(0);
        context.DrawLine(CentrePen, new Point(centre, 3), new Point(centre, bounds.Height - 3));

        if (!IsActive)
        {
            return;
        }

        double value = Math.Clamp(Cents, -Range, Range);
        double needle = XFor(value);
        double distance = Math.Abs(value);
        var brush = distance <= InTune ? GoodBrush : distance <= 25 ? CloseBrush : FarBrush;
        context.DrawRectangle(brush, null, new Rect(needle - 2.5, 2, 5, bounds.Height - 4), 2.5, 2.5);
    }
}
