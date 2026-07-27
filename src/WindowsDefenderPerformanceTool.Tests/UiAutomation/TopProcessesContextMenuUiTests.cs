using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Xunit;

namespace WindowsDefenderPerformanceTool.Tests.UiAutomation;

/// <summary>
/// Regression test for the top-processes context menu: the menu item used to be wired
/// with an ElementName binding inside a ContextMenu (which silently resolves to null,
/// so clicking did nothing), and opening the menu used to drop the pause-on-hover,
/// refreshing the rows out from under the user's cursor.
///
/// The test drives the real app (elevated), fills the grid by running a real Defender
/// custom scan, right-clicks a row, verifies the grid stays paused while the menu is
/// open, clicks "Add to Defender Exclusions…" and asserts the confirmation MessageBox
/// appears — then answers "No", leaving the machine's Defender configuration untouched.
/// </summary>
[Trait("Category", "RequiresElevation")]
public class TopProcessesContextMenuUiTests : IDisposable
{
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;

    private Process? _scan;

    [SkippableFact]
    public void Add_exclusion_from_top_processes_context_menu_shows_the_confirmation_dialog()
    {
        Skip.IfNot(TestEnvironment.IsElevated,
            "This test drives the real app and must run from an elevated test host.");
        Skip.IfNot(TestEnvironment.IsDefenderAvailable(out var unavailable), unavailable);

        using var app = new AppUnderTest();
        app.Launch();

        // Real scan activity fills the "Top Processes" grid via the ETW listener.
        _scan = StartDefenderCustomScan();

        var row = app.TryWaitForTopProcessRow(TimeSpan.FromSeconds(90));
        Skip.If(row == null, "No Defender scan events were observed — the grid stayed empty.");

        // Prefer a real right-click: hovering the row activates pause-on-hover, which the
        // pause regression check below relies on. In sessions where SendInput is blocked,
        // post the right-button messages straight to the app's window instead.
        var realInput = TryRealRightClick(row!);
        if (realInput)
        {
            // A real right-click must open the menu — anything else is a genuine failure.
        }
        else if (!TryOpenContextMenuViaWindowMessage(app))
        {
            Skip.If(true, "Synthetic input is blocked in this session, " +
                          "so the context menu cannot be opened programmatically.");
        }

        var menuItem = app.WaitForContextMenuItem("Add to Defender Exclusions");

        if (realInput)
        {
            // Move onto the open menu — the moment the pointer leaves the grid for the
            // popup, the old code unpaused and refreshed the rows away.
            try { Mouse.MoveTo(menuItem.GetClickablePoint()); }
            catch { /* input blocked — the check below degenerates to "menu stays open" */ }

            // Several 250ms refresh batches arrive during an active scan; the hovered row
            // must still be the same element (the grid stayed paused while the menu is open).
            Thread.Sleep(1500);
            Assert.False(AppUnderTest.IsStale(row!),
                "The grid refreshed while the context menu was open — the pause was dropped.");
        }

        // The original bug: clicking the item did nothing (broken ElementName binding).
        AppUnderTest.Click(menuItem);
        var box = app.WaitForMessageBox("Add Defender Exclusion");

        // Answer "No" — the test must not change the machine's Defender configuration.
        var cf = box.Automation.ConditionFactory;
        var no = app.WaitFor(() => box
                .FindFirstDescendant(cf.ByControlType(ControlType.Button).And(cf.ByName("No")))?
                .AsButton(),
            "the 'No' button of the confirmation MessageBox");
        AppUnderTest.Click(no);
    }

    private static bool TryRealRightClick(AutomationElement row)
    {
        try
        {
            row.RightClick();
            return true;
        }
        catch (Win32Exception)
        {
            return false; // SendInput is blocked in this session (non-interactive desktop)
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the row's context menu without SendInput by posting right-button messages to
    /// the app window — the same messages a real right-click would produce. The row is
    /// re-fetched every attempt because the grid refreshes while the scan is running.
    /// Returns false when the menu never appeared.
    /// </summary>
    private static bool TryOpenContextMenuViaWindowMessage(AppUnderTest app)
    {
        try
        {
            app.WaitFor(() =>
            {
                var row = app.TryWaitForTopProcessRow(TimeSpan.FromSeconds(5));
                if (row == null)
                    return null;
                try
                {
                    var bounds = row.BoundingRectangle;
                    PostRightClick(app.MainWindowHandle, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                }
                catch
                {
                    return null; // row went stale mid-attempt — retry with a fresh one
                }
                Thread.Sleep(300);
                return app.FindContextMenuItem("Add to Defender Exclusions");
            }, "the row context menu");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void PostRightClick(IntPtr hwnd, int screenX, int screenY)
    {
        var point = new POINT { X = screenX, Y = screenY };
        ScreenToClient(hwnd, ref point);
        var lParam = new IntPtr((point.Y << 16) | (point.X & 0xFFFF));
        PostMessage(hwnd, WmRButtonDown, new IntPtr(0x0002 /*MK_RBUTTON*/), lParam);
        PostMessage(hwnd, WmRButtonUp, IntPtr.Zero, lParam);
    }

    private static Process? StartDefenderCustomScan()
    {
        var mpCmdRun = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(mpCmdRun))
            return null;

        // Scanning the drivers folder takes a while, so events keep flowing during the test.
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
        return Process.Start(new ProcessStartInfo(mpCmdRun, $"-Scan -ScanType 3 -File \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public void Dispose()
    {
        try
        {
            if (_scan is { HasExited: false })
                _scan.Kill();
        }
        catch { /* the scan may already have finished */ }
    }
}
