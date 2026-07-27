using System.Collections.Generic;
using Xunit;

namespace WindowsDefenderPerformanceTool.Tests;

/// <summary>
/// Tests for the pause state machine behind <see cref="HoverPause"/>, in particular the
/// regression: the pause must survive the pointer moving onto an open context menu.
/// </summary>
public class HoverPauseStateMachineTests
{
    [Fact]
    public void Mouse_enter_pauses_and_mouse_leave_unpauses()
    {
        var sm = new HoverPauseStateMachine();
        var changes = new List<bool>();
        sm.PauseChanged += changes.Add;

        Assert.False(sm.IsPaused);

        sm.MouseEntered();
        Assert.True(sm.IsPaused);

        sm.MouseLeft();
        Assert.False(sm.IsPaused);

        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void Updates_run_immediately_when_not_paused()
    {
        var sm = new HoverPauseStateMachine();
        var ran = 0;

        sm.ApplyOrDefer(() => ran++);

        Assert.Equal(1, ran);
    }

    [Fact]
    public void Updates_are_deferred_while_paused_and_only_the_latest_runs_on_unpause()
    {
        var sm = new HoverPauseStateMachine();
        sm.MouseEntered();

        var ran = new List<int>();
        sm.ApplyOrDefer(() => ran.Add(1));
        sm.ApplyOrDefer(() => ran.Add(2));

        Assert.Empty(ran);

        sm.MouseLeft();
        Assert.Equal(new[] { 2 }, ran);
    }

    [Fact]
    public void Mouse_leave_while_context_menu_is_open_keeps_the_pause()
    {
        var sm = new HoverPauseStateMachine();
        var changes = new List<bool>();
        sm.PauseChanged += changes.Add;

        sm.MouseEntered();
        sm.ContextMenuOpened();
        sm.MouseLeft(); // pointer moved onto the open menu

        Assert.True(sm.IsPaused);
        Assert.Equal(new[] { true }, changes);
    }

    [Fact]
    public void Deferred_update_does_not_run_when_the_menu_closes_with_the_mouse_still_over()
    {
        var sm = new HoverPauseStateMachine();
        sm.MouseEntered();
        sm.ContextMenuOpened();
        sm.MouseLeft();

        var ran = 0;
        sm.ApplyOrDefer(() => ran++);
        sm.ContextMenuClosed(mouseStillOver: true);

        Assert.True(sm.IsPaused);
        Assert.Equal(0, ran);

        // The pause ends only when the mouse actually leaves afterwards.
        sm.MouseLeft();
        Assert.False(sm.IsPaused);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void Menu_closing_with_the_mouse_elsewhere_unpauses_and_runs_the_deferred_update()
    {
        var sm = new HoverPauseStateMachine();
        sm.MouseEntered();
        sm.ContextMenuOpened();
        sm.MouseLeft();

        var ran = 0;
        sm.ApplyOrDefer(() => ran++);
        sm.ContextMenuClosed(mouseStillOver: false);

        Assert.False(sm.IsPaused);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void Pause_is_kept_until_the_last_of_several_open_menus_closes()
    {
        var sm = new HoverPauseStateMachine();
        sm.MouseEntered();
        sm.ContextMenuOpened();
        sm.ContextMenuOpened();

        sm.ContextMenuClosed(mouseStillOver: false);
        Assert.True(sm.IsPaused);

        sm.ContextMenuClosed(mouseStillOver: false);
        Assert.False(sm.IsPaused);
    }

    [Fact]
    public void Context_menu_opened_and_closed_without_hover_never_pauses()
    {
        var sm = new HoverPauseStateMachine();
        var changes = new List<bool>();
        sm.PauseChanged += changes.Add;

        sm.ContextMenuOpened();
        sm.ContextMenuClosed(mouseStillOver: false);

        Assert.False(sm.IsPaused);
        Assert.Empty(changes);
    }
}
