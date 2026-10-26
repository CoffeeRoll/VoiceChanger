using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.App.Controls;

/// <summary>
/// Scrolling graph of recent pitch: the sung pitch and the corrected pitch, drawn over a grid of
/// note lines where the notes of the selected scale are highlighted and labelled.
/// </summary>
public sealed class PitchHistoryView : Control
{
    public static readonly StyledProperty<double[]?> InputPitchesProperty =
        AvaloniaProperty.Register<PitchHistoryView, double[]?>(nameof(InputPitches));

    public static readonly StyledProperty<double[]?> OutputPitchesProperty =
        AvaloniaProperty.Register<PitchHistoryView, double[]?>(nameof(OutputPitches));

    public static readonly StyledProperty<int> ScaleMaskProperty =
        AvaloniaProperty.Register<PitchHistoryView, int>(nameof(ScaleMask), 0xFFF);

    public static readonly StyledProperty<int> RootPitchClassProperty =
        AvaloniaProperty.Register<PitchHistoryView, int>(nameof(RootPitchClass), -1);

    private const double VisibleSemitones = 13;
    private const double MinimumLabelSpacing = 11;
    private const double LabelWidth = 34;
    private const double DefaultCentre = 60;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#14171D"));
    private static readonly IPen ScaleLinePen = new Pen(new SolidColorBrush(Color.Parse("#2A303C")), 1);
    private static readonly IPen RootLinePen = new Pen(new SolidColorBrush(Color.Parse("#7C5CFF"), 0.45), 1);
    private static readonly IPen OtherLinePen = new Pen(new SolidColorBrush(Color.Parse("#1C2028")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6B7385"));
    private static readonly IBrush RootLabelBrush = new SolidColorBrush(Color.Parse("#9D86FF"));
    private static readonly IBrush LegendBrush = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IPen InputPen = new Pen(new SolidColorBrush(Color.Parse("#22D3EE"), 0.75), 1.5, lineJoin: PenLineJoin.Round);
    private static readonly IPen OutputPen = new Pen(new SolidColorBrush(Color.Parse("#9D86FF")), 2.5, lineJoin: PenLineJoin.Round);
    private static readonly Typeface LabelFace = new(FontFamily.Default);

    private double _centre = DefaultCentre;

    static PitchHistoryView()
    {
        AffectsRender<PitchHistoryView>(InputPitchesProperty, OutputPitchesProperty, ScaleMaskProperty, RootPitchClassProperty);
    }

    /// <summary>Sung pitch as fractional MIDI notes, oldest first; NaN where nothing was sung.</summary>
    public double[]? InputPitches
    {
        get => GetValue(InputPitchesProperty);
        set => SetValue(InputPitchesProperty, value);
    }

    /// <summary>Corrected pitch as fractional MIDI notes, oldest first; NaN where nothing was sung.</summary>
    public double[]? OutputPitches
    {
        get => GetValue(OutputPitchesProperty);
        set => SetValue(OutputPitchesProperty, value);
    }

    /// <summary>Pitch classes to highlight (bit 0 = C).</summary>
    public int ScaleMask
    {
        get => GetValue(ScaleMaskProperty);
        set => SetValue(ScaleMaskProperty, value);
    }

    /// <summary>Pitch class of the key's root note (0 = C), highlighted in the grid; −1 for none.</summary>
    public int RootPitchClass
    {
        get => GetValue(RootPitchClassProperty);
        set => SetValue(RootPitchClassProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(BackgroundBrush, null, bounds, 8, 8);
        var plot = new Rect(LabelWidth, 6, Math.Max(0, bounds.Width - LabelWidth - 8), Math.Max(0, bounds.Height - 12));
        if (plot.Width < 20 || plot.Height < 20)
        {
            return;
        }

        var input = InputPitches ?? [];
        var output = OutputPitches ?? [];
        UpdateCentre(output, input);

        double low = _centre - VisibleSemitones / 2;
        double high = _centre + VisibleSemitones / 2;
        double YFor(double note) => plot.Bottom - (note - low) / (high - low) * plot.Height;

        int mask = ScaleMask;
        int root = RootPitchClass;
        double lastLabel = double.PositiveInfinity;
        for (int note = (int)Math.Ceiling(low); note <= (int)Math.Floor(high); note++)
        {
            double y = YFor(note);
            bool inScale = MusicalScale.IsInScale(note, mask);
            bool isRoot = inScale && MusicalScale.PitchClass(note) == root;
            context.DrawLine(isRoot ? RootLinePen : inScale ? ScaleLinePen : OtherLinePen, new Point(plot.Left, y), new Point(plot.Right, y));
            // Label scale notes, skipping one when it would overlap the label below it.
            if (inScale && lastLabel - y >= MinimumLabelSpacing)
            {
                var label = new FormattedText(MusicalScale.NoteName(note), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, isRoot ? RootLabelBrush : LabelBrush);
                context.DrawText(label, new Point(6, y - label.Height / 2));
                lastLabel = y;
            }
        }

        using (context.PushClip(plot))
        {
            DrawCurve(context, input, plot, YFor, InputPen);
            DrawCurve(context, output, plot, YFor, OutputPen);
        }

        DrawLegend(context, plot);
    }

    /// <summary>Follows the singer: the view recentres when the recent pitches leave the middle area.</summary>
    private void UpdateCentre(double[] primary, double[] fallback)
    {
        var recent = primary.Where(double.IsFinite).ToList();
        if (recent.Count == 0)
        {
            recent = fallback.Where(double.IsFinite).ToList();
        }

        if (recent.Count == 0)
        {
            return;
        }

        recent.Sort();
        double median = recent[recent.Count / 2];
        if (Math.Abs(median - _centre) > VisibleSemitones / 4)
        {
            _centre = Math.Round(median);
        }
    }

    private static void DrawCurve(DrawingContext context, double[] notes, Rect plot, Func<double, double> yFor, IPen pen)
    {
        if (notes.Length < 2)
        {
            return;
        }

        double step = plot.Width / (notes.Length - 1);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < notes.Length; i++)
            {
                if (!double.IsFinite(notes[i]))
                {
                    if (open)
                    {
                        path.EndFigure(isClosed: false);
                        open = false;
                    }

                    continue;
                }

                var point = new Point(plot.Left + i * step, yFor(notes[i]));
                if (!open)
                {
                    path.BeginFigure(point, isFilled: false);
                    open = true;
                }
                else
                {
                    path.LineTo(point);
                }
            }

            if (open)
            {
                path.EndFigure(isClosed: false);
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawLegend(DrawingContext context, Rect plot)
    {
        var sung = new FormattedText("sung", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, LegendBrush);
        var corrected = new FormattedText("corrected", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, LegendBrush);
        double x = plot.Right - corrected.Width - sung.Width - 46;
        double y = plot.Top + 4;
        context.DrawLine(InputPen, new Point(x, y + sung.Height / 2), new Point(x + 12, y + sung.Height / 2));
        context.DrawText(sung, new Point(x + 16, y));
        x += 16 + sung.Width + 10;
        context.DrawLine(OutputPen, new Point(x, y + sung.Height / 2), new Point(x + 12, y + sung.Height / 2));
        context.DrawText(corrected, new Point(x + 16, y));
    }
}
