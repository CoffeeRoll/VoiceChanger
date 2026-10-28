using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VoiceChanger.App.Controls;
using VoiceChanger.App.ViewModels;
using VoiceChanger.Core.Graph;

namespace VoiceChanger.App.Views;

/// <summary>
/// The node editor canvas. Handles the mouse: moving around (drag the background, scroll to zoom),
/// moving nodes, and drawing wires between sockets.
/// </summary>
public sealed partial class NodeEditorView : UserControl
{
    /// <summary>How close (in screen pixels) a wire has to be dropped to a socket to snap to it.</summary>
    private const double SnapDistance = 18;
    private const double DragThreshold = 3;

    private readonly HashSet<NodeSocket> _sockets = [];
    private NodeEditorViewModel? _viewModel;
    private Drag _drag;
    private Point _pressPosition;
    private Point _startOffset;
    private NodeViewModel? _draggedNode;
    private PortViewModel? _wireAnchor;
    private Point _lastPointer;
    private bool _moved;
    private bool _fitted;

    public NodeEditorView()
    {
        InitializeComponent();
        World.RenderTransformOrigin = RelativePoint.TopLeft;
        Viewport.PointerPressed += OnViewportPointerPressed;
        Viewport.PointerMoved += OnViewportPointerMoved;
        Viewport.PointerReleased += OnViewportPointerReleased;
        Viewport.PointerCaptureLost += (_, _) => CancelDrag();
        Viewport.PointerWheelChanged += OnViewportPointerWheelChanged;
        Viewport.SizeChanged += (_, _) => UpdateViewCenter();
        LayoutUpdated += OnLayoutUpdated;
    }

    private enum Drag
    {
        None,
        Pan,
        Node,
        Wire,
    }

    /// <summary>Zooms and scrolls so that every node is visible (at most at 100 %).</summary>
    public void FitToView()
    {
        if (_viewModel is null || Viewport.Bounds.Width <= 0 || Viewport.Bounds.Height <= 0)
        {
            return;
        }

        var rects = _viewModel.Nodes.Select(NodeRect).ToList();
        if (rects.Count == 0)
        {
            _viewModel.Zoom = 1;
            _viewModel.PanX = 0;
            _viewModel.PanY = 0;
            return;
        }

        var content = rects.Aggregate((a, b) => a.Union(b));
        const double margin = 48;
        double zoom = Math.Min(
            (Viewport.Bounds.Width - 2 * margin) / content.Width,
            (Viewport.Bounds.Height - 2 * margin) / content.Height);
        zoom = Math.Clamp(zoom, NodeEditorViewModel.MinZoom, 1);
        _viewModel.Zoom = zoom;
        _viewModel.PanX = Math.Round(Viewport.Bounds.Width / 2 - content.Center.X * zoom);
        _viewModel.PanY = Math.Round(Viewport.Bounds.Height / 2 - content.Center.Y * zoom);
    }

    /// <summary>Opens the menu of node types; the new node goes to <paramref name="worldPosition"/>.</summary>
    public void ShowAddMenu(Point worldPosition)
    {
        if (_viewModel is null)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = PlacementMode.Pointer };
        NodeCategory? category = null;
        var plugins = _viewModel.AddNodeOptions.Where(o => o.Descriptor.Group is not null).ToList();
        foreach (var option in _viewModel.AddNodeOptions.Where(o => o.Descriptor.Group is null))
        {
            if (category is not null && option.Descriptor.Category != category)
            {
                // The plug-ins (all effects) go at the end of the effects.
                if (category == NodeCategory.Effect && plugins.Count > 0)
                {
                    menu.Items.Add(CreatePluginsMenu(plugins, worldPosition));
                }

                menu.Items.Add(new Separator());
            }

            category = option.Descriptor.Category;
            menu.Items.Add(CreateAddItem(option, worldPosition));
        }

        menu.ShowAt(Viewport, showAtPointer: true);
    }

    private MenuItem CreatePluginsMenu(IReadOnlyList<AddNodeOption> plugins, Point worldPosition)
    {
        var menu = new MenuItem { Header = AddMenuEntry.PluginsName };
        foreach (var vendor in plugins.GroupBy(o => o.Descriptor.Group!, StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem { Header = vendor.Key };
            foreach (var option in vendor.OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                item.Items.Add(CreateAddItem(option, worldPosition));
            }

            menu.Items.Add(item);
        }

        return menu;
    }

    private MenuItem CreateAddItem(AddNodeOption option, Point worldPosition)
    {
        var item = new MenuItem { Header = option.Name };
        ToolTip.SetTip(item, option.Description);
        var descriptor = option.Descriptor;
        item.Click += (_, _) => _viewModel?.AddNode(descriptor, worldPosition);
        return item;
    }

    internal void RegisterSocket(NodeSocket socket) => _sockets.Add(socket);

    internal void UnregisterSocket(NodeSocket socket) => _sockets.Remove(socket);

    internal void Select(NodeViewModel node) => _viewModel?.SelectedNode = node;

    /// <summary>Starts moving a node (called by the node when it is pressed).</summary>
    internal void BeginNodeDrag(NodeViewModel node, PointerPressedEventArgs e)
    {
        Select(node);
        _drag = Drag.Node;
        _draggedNode = node;
        _pressPosition = e.GetPosition(Viewport);
        _startOffset = new Point(node.X, node.Y);
        _moved = false;
        e.Pointer.Capture(Viewport);
        Focus();
    }

    /// <summary>Starts drawing a wire from a socket (called by the socket when it is pressed).</summary>
    internal void BeginWire(PortViewModel port, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var anchor = port;
        if (port.IsInput && _viewModel.SourceOf(port) is { } source)
        {
            // Pick the wire up by its end: drop it on another input, or on empty space to disconnect it.
            _viewModel.Disconnect(port);
            anchor = source;
        }

        _drag = Drag.Wire;
        _wireAnchor = anchor;
        UpdatePendingWire(e.GetPosition(World));
        e.Pointer.Capture(Viewport);
        Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.WiresInvalidated -= OnWiresInvalidated;
            _viewModel.FitRequested -= OnFitRequested;
        }

        _viewModel = DataContext as NodeEditorViewModel;
        _fitted = false;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.WiresInvalidated += OnWiresInvalidated;
            _viewModel.FitRequested += OnFitRequested;
            UpdateTransform();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_viewModel is null || e.Handled)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Delete or Key.Back when _viewModel.SelectedNode is not null:
                _viewModel.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.A when e.KeyModifiers == KeyModifiers.Shift:
                ShowAddMenu(ToWorld(_lastPointer));
                e.Handled = true;
                break;
            case Key.D when e.KeyModifiers == KeyModifiers.Control && _viewModel.SelectedNode is not null:
                _viewModel.DuplicateSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Home:
                FitToView();
                e.Handled = true;
                break;
            case Key.Escape:
                CancelDrag();
                _viewModel.SelectedNode = null;
                e.Handled = true;
                break;
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        // Only presses on the empty background get here; nodes and sockets handle their own.
        var point = e.GetCurrentPoint(Viewport);
        _lastPointer = point.Position;
        Focus();
        if (point.Properties.IsRightButtonPressed)
        {
            ShowAddMenu(ToWorld(point.Position));
            e.Handled = true;
            return;
        }

        _drag = Drag.Pan;
        _pressPosition = point.Position;
        _startOffset = new Point(_viewModel.PanX, _viewModel.PanY);
        _moved = false;
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(Viewport);
        _lastPointer = position;
        if (_viewModel is null || _drag == Drag.None)
        {
            return;
        }

        var delta = position - _pressPosition;
        _moved |= Math.Abs(delta.X) > DragThreshold || Math.Abs(delta.Y) > DragThreshold;
        switch (_drag)
        {
            case Drag.Pan when _moved:
                _viewModel.PanX = _startOffset.X + delta.X;
                _viewModel.PanY = _startOffset.Y + delta.Y;
                break;
            case Drag.Node when _moved && _draggedNode is not null:
                _draggedNode.X = Math.Round(_startOffset.X + delta.X / _viewModel.Zoom);
                _draggedNode.Y = Math.Round(_startOffset.Y + delta.Y / _viewModel.Zoom);
                break;
            case Drag.Wire:
                UpdatePendingWire(e.GetPosition(World));
                break;
        }
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_drag == Drag.Pan && !_moved)
        {
            // A click on the empty background clears the selection.
            _viewModel.SelectedNode = null;
        }
        else if (_drag == Drag.Wire && _wireAnchor is not null && FindSocketNear(e.GetPosition(World), _wireAnchor) is { } target)
        {
            _viewModel.TryConnect(_wireAnchor, target);
        }

        var drag = _drag;
        CancelDrag();
        if (drag != Drag.None)
        {
            e.Pointer.Capture(null);
        }
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ZoomAt(e.GetPosition(Viewport), _viewModel.Zoom * Math.Pow(1.12, e.Delta.Y));
        e.Handled = true;
    }

    private void CancelDrag()
    {
        _drag = Drag.None;
        _draggedNode = null;
        _wireAnchor = null;
        WireLayer.Pending = null;
    }

    private void UpdatePendingWire(Point world)
    {
        if (_wireAnchor is null)
        {
            return;
        }

        // Snap to a socket the wire could be dropped on.
        var end = FindSocketNear(world, _wireAnchor)?.Anchor ?? world;
        var anchor = _wireAnchor.Anchor;
        WireLayer.Pending = _wireAnchor.IsInput ? (end, anchor) : (anchor, end);
    }

    /// <summary>The closest socket of the opposite kind on another node within snapping distance.</summary>
    private PortViewModel? FindSocketNear(Point world, PortViewModel from)
    {
        double limit = SnapDistance / (_viewModel?.Zoom ?? 1);
        PortViewModel? best = null;
        double bestDistance = limit;
        foreach (var socket in _sockets)
        {
            if (socket.Port is not { } port || port.IsInput == from.IsInput || port.Owner == from.Owner)
            {
                continue;
            }

            var offset = port.Anchor - world;
            double distance = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
            if (distance <= bestDistance)
            {
                best = port;
                bestDistance = distance;
            }
        }

        return best;
    }

    private Point ToWorld(Point viewport) => _viewModel is null
        ? viewport
        : new Point((viewport.X - _viewModel.PanX) / _viewModel.Zoom, (viewport.Y - _viewModel.PanY) / _viewModel.Zoom);

    private Rect NodeRect(NodeViewModel node)
    {
        var size = NodeItems.ContainerFromItem(node) is Control { Bounds.Width: > 0 } container
            ? container.Bounds.Size
            : new Size(230, 140);
        return new Rect(new Point(node.X, node.Y), size);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // Wires follow the sockets wherever layout put them.
        bool changed = false;
        foreach (var socket in _sockets)
        {
            if (socket.Port is { } port && socket.TranslatePoint(new Point(NodeSocket.Size / 2, NodeSocket.Size / 2), World) is { } anchor && anchor != port.Anchor)
            {
                port.Anchor = anchor;
                changed = true;
            }
        }

        if (changed)
        {
            WireLayer.InvalidateVisual();
        }

        // Show the whole graph the first time the editor appears.
        if (!_fitted && _viewModel is not null && Viewport.Bounds.Width > 0 && _viewModel.Nodes.Count > 0 && _sockets.Count > 0)
        {
            _fitted = true;
            FitToView();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeEditorViewModel.Zoom) or nameof(NodeEditorViewModel.PanX) or nameof(NodeEditorViewModel.PanY))
        {
            UpdateTransform();
        }
    }

    private void OnWiresInvalidated(object? sender, EventArgs e) => WireLayer.InvalidateVisual();

    private void OnFitRequested(object? sender, EventArgs e) => FitToView();

    private void UpdateTransform()
    {
        if (_viewModel is null)
        {
            return;
        }

        World.RenderTransform = new MatrixTransform(
            Matrix.CreateScale(_viewModel.Zoom, _viewModel.Zoom) * Matrix.CreateTranslation(_viewModel.PanX, _viewModel.PanY));
        UpdateViewCenter();
    }

    private void UpdateViewCenter()
    {
        _viewModel?.ViewCenter = ToWorld(new Point(Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2));
    }
}
