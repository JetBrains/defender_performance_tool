using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Pause-on-hover behavior shared by the treemap and the top-processes grid: while the
/// mouse is over the watched element, incoming data updates can be deferred so the view
/// doesn't change under the cursor; the pause is indicated on the owning <see cref="GroupBox"/>:
/// an orange pause icon is appended to its header and its border turns orange. Deferred
/// updates run when the mouse leaves. The pause is retained while a context menu owned by
/// the watched element is open, so the view doesn't refresh while the user is picking a
/// menu item.
/// </summary>
public sealed class HoverPause
{
    private static readonly Brush IndicatorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));

    private readonly HoverPauseStateMachine _state = new();
    private GroupBox? _indicator;

    // Original GroupBox look, captured the first time the pause indicator is shown
    // so it can be restored exactly when the pause ends.
    private bool _originalsCaptured;
    private object? _originalHeader;
    private Brush? _originalBorderBrush;
    private Thickness _originalBorderThickness;

    public bool IsPaused => _state.IsPaused;

    /// <summary>Raised after <see cref="IsPaused"/> changes.</summary>
    public event Action<bool>? PauseChanged
    {
        add => _state.PauseChanged += value;
        remove => _state.PauseChanged -= value;
    }

    /// <param name="hoverTarget">Element whose mouse enter/leave toggles the pause.</param>
    /// <param name="indicator">GroupBox that displays the pause state (header icon +
    /// orange border). Can also be assigned later via <see cref="Indicator"/>.</param>
    public HoverPause(FrameworkElement hoverTarget, GroupBox? indicator = null)
    {
        _indicator = indicator;

        _state.PauseChanged += paused =>
        {
            if (paused) ShowIndicator(); else HideIndicator();
        };

        hoverTarget.MouseEnter += (_, __) => _state.MouseEntered();
        hoverTarget.MouseLeave += (_, __) => _state.MouseLeft();

        // A context menu on a grid row or treemap tile bubbles these routed events up to
        // the hover target. While the menu is open the pointer may leave the element (onto
        // the popup) without the user being done — the state machine keeps the pause.
        hoverTarget.AddHandler(FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler((_, __) => _state.ContextMenuOpened()));
        hoverTarget.AddHandler(FrameworkElement.ContextMenuClosingEvent,
            new ContextMenuEventHandler((_, __) =>
            {
                // IsMouseOver is still owned by the popup during Closing — re-evaluate
                // once the popup has actually closed and input state has settled.
                hoverTarget.Dispatcher.BeginInvoke(new Action(() =>
                    _state.ContextMenuClosed(hoverTarget.IsMouseOver)));
            }));
    }

    /// <summary>
    /// GroupBox that displays the pause state. Assignable after construction (e.g. by the
    /// window hosting the control); if a pause is active the indicator moves immediately.
    /// </summary>
    public GroupBox? Indicator
    {
        get => _indicator;
        set
        {
            if (_indicator == value) return;
            if (IsPaused) HideIndicator();
            _indicator = value;
            _originalsCaptured = false;
            if (IsPaused) ShowIndicator();
        }
    }

    /// <summary>
    /// Runs <paramref name="update"/> immediately, or defers it until the pause ends
    /// when paused. Only the latest deferred update is kept.
    /// </summary>
    public void ApplyOrDefer(Action update) => _state.ApplyOrDefer(update);

    private void ShowIndicator()
    {
        if (_indicator == null) return;

        if (!_originalsCaptured)
        {
            _originalHeader = _indicator.Header;
            _originalBorderBrush = _indicator.BorderBrush;
            _originalBorderThickness = _indicator.BorderThickness;
            _originalsCaptured = true;
        }

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            ToolTip = "Updates paused while the mouse is over this view — move it away to resume.",
        };
        header.Children.Add(new TextBlock { Text = _originalHeader?.ToString() ?? string.Empty });
        header.Children.Add(CreatePauseIcon());
        _indicator.Header = header;
        _indicator.BorderBrush = IndicatorBrush;
        _indicator.BorderThickness = new Thickness(2);
    }

    // Hand-drawn pause icon (two vertical bars): unlike the ⏸ glyph, which falls back to
    // an emoji font and renders tiny and faint at header size, vector shapes are crisp,
    // scale-independent and always the right color.
    private static FrameworkElement CreatePauseIcon()
    {
        var icon = new Grid
        {
            Width = 12,
            Height = 14,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Children.Add(new Rectangle
        {
            Width = 4,
            RadiusX = 1,
            RadiusY = 1,
            Fill = IndicatorBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        icon.Children.Add(new Rectangle
        {
            Width = 4,
            RadiusX = 1,
            RadiusY = 1,
            Fill = IndicatorBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        return icon;
    }

    private void HideIndicator()
    {
        if (_indicator == null || !_originalsCaptured) return;

        _indicator.Header = _originalHeader;
        _indicator.BorderBrush = _originalBorderBrush;
        _indicator.BorderThickness = _originalBorderThickness;
    }
}
