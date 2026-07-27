using System;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Pause logic behind <see cref="HoverPause"/>, kept free of WPF types so it can be
/// unit-tested: paused while hovered, and the pause is retained while a context menu
/// owned by the hovered element is open — otherwise the view would refresh under the
/// user's feet the moment the pointer moves from the control onto the open menu
/// (which raises MouseLeave even though the user is still interacting with the row).
/// </summary>
internal sealed class HoverPauseStateMachine
{
    private int _openContextMenus;
    private Action? _pendingUpdate;

    public bool IsPaused { get; private set; }

    /// <summary>Raised after <see cref="IsPaused"/> actually changes.</summary>
    public event Action<bool>? PauseChanged;

    public void MouseEntered() => SetPaused(true);

    public void MouseLeft()
    {
        // Pointer moved onto an open context menu — stay paused until the menu closes.
        if (_openContextMenus == 0)
            SetPaused(false);
    }

    public void ContextMenuOpened() => _openContextMenus++;

    /// <param name="mouseStillOver">
    /// True when the pointer is over the watched element again after the menu closed
    /// (the pause then stays on until the mouse actually leaves).
    /// </param>
    public void ContextMenuClosed(bool mouseStillOver)
    {
        _openContextMenus = Math.Max(0, _openContextMenus - 1);
        if (_openContextMenus == 0 && !mouseStillOver)
            SetPaused(false);
    }

    /// <summary>
    /// Runs <paramref name="update"/> immediately, or defers it until the pause ends.
    /// Only the latest deferred update is kept.
    /// </summary>
    public void ApplyOrDefer(Action update)
    {
        if (IsPaused)
            _pendingUpdate = update;
        else
            update();
    }

    private void SetPaused(bool paused)
    {
        if (IsPaused == paused)
            return;

        IsPaused = paused;
        PauseChanged?.Invoke(paused);

        if (paused)
            return;
        var pending = _pendingUpdate;
        _pendingUpdate = null;
        pending?.Invoke();
    }
}
