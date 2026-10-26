using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;

namespace VoiceChanger.App.Controls;

/// <summary>
/// A round socket on the edge of a node. Dragging from it draws a wire; it reports its position to
/// the editor so wires can be drawn to it.
/// </summary>
public sealed class NodeSocket : Control
{
    public const double Size = 18;

    public static readonly StyledProperty<bool> IsConnectedProperty =
        AvaloniaProperty.Register<NodeSocket, bool>(nameof(IsConnected));

    private static readonly IBrush SocketBrush = new SolidColorBrush(Color.Parse("#22D3EE"));
    private static readonly IBrush HollowBrush = new SolidColorBrush(Color.Parse("#11141A"));
    private static readonly IPen RingPen = new Pen(SocketBrush, 2);
    private static readonly IPen OutlinePen = new Pen(new SolidColorBrush(Color.Parse("#0B0D11")), 1.5);

    private NodeEditorView? _editor;

    static NodeSocket()
    {
        AffectsRender<NodeSocket>(IsConnectedProperty, IsPointerOverProperty);
        CursorProperty.OverrideDefaultValue<NodeSocket>(new Cursor(StandardCursorType.Hand));
    }

    public bool IsConnected
    {
        get => GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    public PortViewModel? Port => DataContext as PortViewModel;

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        // The whole square reacts to the mouse, not just the small circle.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        double radius = IsPointerOver ? 6.5 : 5.5;
        if (IsConnected)
        {
            context.DrawEllipse(SocketBrush, OutlinePen, center, radius, radius);
        }
        else
        {
            context.DrawEllipse(HollowBrush, RingPen, center, radius - 0.5, radius - 0.5);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _editor = this.FindAncestorOfType<NodeEditorView>();
        _editor?.RegisterSocket(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _editor?.UnregisterSocket(this);
        _editor = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Port is { } port && _editor is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _editor.BeginWire(port, e);
            e.Handled = true;
        }
    }
}
