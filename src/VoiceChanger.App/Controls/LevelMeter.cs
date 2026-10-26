using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceChanger.App.Controls;

/// <summary>Horizontal peak meter from -60 dBFS to 0 dBFS (green, then yellow, then red).</summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level), -60);

    private const double MinDb = -60;

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#262A34"));

    private static readonly IBrush FillBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#3BD671"), 0),
            new GradientStop(Color.Parse("#3BD671"), 0.72),
            new GradientStop(Color.Parse("#F2C94C"), 0.85),
            new GradientStop(Color.Parse("#EB5757"), 0.97),
        },
    };

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(LevelProperty);
    }

    /// <summary>Level in dBFS.</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 8);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        double radius = bounds.Height / 2;
        context.DrawRectangle(TrackBrush, null, bounds, radius, radius);

        double fraction = Math.Clamp((Level - MinDb) / -MinDb, 0, 1);
        if (fraction <= 0)
        {
            return;
        }

        // Clip the full-width gradient so colours stay anchored to their dB positions.
        using (context.PushClip(new Rect(0, 0, bounds.Width * fraction, bounds.Height)))
        {
            context.DrawRectangle(FillBrush, null, bounds, radius, radius);
        }
    }
}
