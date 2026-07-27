using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Pause-on-hover behavior shared by the treemap and the top-processes grid: while the
/// mouse is over the watched element, incoming data updates can be deferred so the view
/// doesn't change under the cursor; the pause is indicated by an orange frame with a
/// pause icon overlaid on the host panel. Deferred updates run when the mouse leaves.
/// </summary>
public sealed class HoverPause
{
    private static readonly Brush IndicatorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));

    private readonly Panel _overlayHost;
    private Border? _overlay;
    private Action? _pendingUpdate;

    public bool IsPaused { get; private set; }

    /// <summary>Raised after <see cref="IsPaused"/> changes.</summary>
    public event Action<bool>? PauseChanged;

    /// <param name="hoverTarget">Element whose mouse enter/leave toggles the pause.</param>
    /// <param name="overlayHost">Panel that receives the pause indicator overlay
    /// (the hovered element itself or a wrapper around it).</param>
    public HoverPause(FrameworkElement hoverTarget, Panel overlayHost)
    {
        _overlayHost = overlayHost;

        hoverTarget.MouseEnter += (_, __) =>
        {
            IsPaused = true;
            ShowOverlay();
            PauseChanged?.Invoke(true);
        };
        hoverTarget.MouseLeave += (_, __) =>
        {
            IsPaused = false;
            HideOverlay();
            PauseChanged?.Invoke(false);
            var pending = _pendingUpdate;
            _pendingUpdate = null;
            pending?.Invoke();
        };
    }

    /// <summary>
    /// Runs <paramref name="update"/> immediately, or defers it until the mouse leaves
    /// when paused. Only the latest deferred update is kept.
    /// </summary>
    public void ApplyOrDefer(Action update)
    {
        if (IsPaused)
            _pendingUpdate = update;
        else
            update();
    }

    /// <summary>Re-adds the overlay after the host's children were rebuilt while paused.</summary>
    public void RefreshOverlay()
    {
        HideOverlay();
        if (IsPaused) ShowOverlay();
    }

    private void ShowOverlay()
    {
        if (_overlay != null || _overlayHost.ActualWidth < 20 || _overlayHost.ActualHeight < 20) return;

        _overlay = new Border
        {
            BorderBrush = IndicatorBrush,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "\u23F8", // ⏸
                FontSize = 20,
                Foreground = Brushes.White,
                Background = IndicatorBrush,
                Padding = new Thickness(6, 3, 6, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4),
                IsHitTestVisible = false,
            },
        };

        // A Canvas doesn't stretch its children — track the host size explicitly.
        if (_overlayHost is Canvas)
        {
            _overlay.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(FrameworkElement.ActualWidth)) { Source = _overlayHost });
            _overlay.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(FrameworkElement.ActualHeight)) { Source = _overlayHost });
        }

        Panel.SetZIndex(_overlay, int.MaxValue);
        _overlayHost.Children.Add(_overlay);
    }

    private void HideOverlay()
    {
        if (_overlay == null) return;
        _overlayHost.Children.Remove(_overlay);
        _overlay = null;
    }
}
