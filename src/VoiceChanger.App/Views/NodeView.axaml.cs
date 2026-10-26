using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VoiceChanger.App.ViewModels;

namespace VoiceChanger.App.Views;

/// <summary>A node card on the editor canvas.</summary>
public sealed partial class NodeView : UserControl
{
    public NodeView()
    {
        InitializeComponent();

        // Clicking anything on the node, even its combo box, selects it.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    private NodeEditorView? Editor => this.FindAncestorOfType<NodeEditorView>();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.Handled || DataContext is not NodeViewModel node || Editor is not { } editor)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            // Pressing the node itself (not one of its controls) starts moving it.
            editor.BeginNodeDrag(node, e);
            e.Handled = true;
        }
        else if (properties.IsRightButtonPressed)
        {
            ShowMenu(node);
            e.Handled = true;
        }
    }

    private void ShowMenu(NodeViewModel node)
    {
        var duplicate = new MenuItem { Header = "Duplicate", Command = node.DuplicateCommand, InputGesture = new KeyGesture(Key.D, KeyModifiers.Control) };
        var delete = new MenuItem { Header = "Delete", Command = node.DeleteCommand, InputGesture = new KeyGesture(Key.Delete) };
        var menu = new MenuFlyout { Items = { duplicate, delete } };
        menu.ShowAt(this, showAtPointer: true);
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is NodeViewModel node && Editor is { } editor)
        {
            editor.Select(node);
        }
    }
}
