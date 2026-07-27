using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsDefenderPerformanceTool.Tests.UiAutomation;
using Xunit;

namespace WindowsDefenderPerformanceTool.Tests;

/// <summary>
/// In-process regression test for the top-processes context menu (the "Add to Defender
/// Exclusions…" item). The original bug wired the MenuItem.Command with an ElementName
/// binding inside a ContextMenu — which silently resolves to null because a ContextMenu
/// lives in a popup outside the window's namescope — so clicking never reached the
/// confirmation dialog.
///
/// The test hosts the real <see cref="MainWindow"/> on an STA thread, opens the shared
/// row ContextMenu against a stand-in row (whose DataContext is a ScanStat, exactly like
/// a real DataGridRow) and clicks the menu item. The confirmation prompt is replaced via
/// <see cref="DefenderExclusions.ShowConfirmation"/> (a real Win32 MessageBox cannot be
/// shown or automated from the test host); the test answers "No", so the machine's
/// Defender configuration is left untouched.
/// </summary>
[Trait("Category", "RequiresElevation")]
public class MainWindowContextMenuTests
{
    [SkippableFact]
    public void Clicking_add_exclusion_in_the_row_context_menu_reaches_the_confirmation_prompt()
    {
        Skip.IfNot(TestEnvironment.IsElevated,
            "This test runs the real window and must run from an elevated test host.");
        Skip.IfNot(TestEnvironment.IsDefenderAvailable(out var unavailable), unavailable);

        RunOnStaThread(() =>
        {
            using var viewModel = new MainViewModel(startLiveMonitoring: false);
            var window = new MainWindow(viewModel);
            var originalPrompt = DefenderExclusions.ShowConfirmation;
            try
            {
                window.Show();

                var stat = new ScanStat($"wdt-ctx-test-{Guid.NewGuid():N}.exe", 1.0);
                viewModel.TopProcesses.Add(stat);

                // Stand-in for a DataGridRow: an element in the window's visual tree whose
                // DataContext is the row item — the DataContext flows through the opened
                // ContextMenu to the MenuItem, just like with a real row.
                var standInRow = new ContentControl { DataContext = stat };
                window.TopProcessesPanel.Children.Add(standInRow);
                PumpDispatcher();

                var menu = (ContextMenu)window.FindResource("ProcessRowContextMenu");
                menu.PlacementTarget = standInRow;
                menu.IsOpen = true;
                PumpDispatcher();

                var item = (MenuItem)menu.Items[0];
                Assert.True(menu.IsOpen, "The row context menu did not open.");
                Assert.Equal(stat, item.DataContext);

                // Capture the confirmation prompt instead of showing a real MessageBox;
                // answering "No" keeps the machine's Defender configuration untouched.
                string? promptTitle = null;
                string? promptMessage = null;
                DefenderExclusions.ShowConfirmation = (message, title) =>
                {
                    promptMessage = message;
                    promptTitle = title;
                    return false; // "No"
                };

                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.Equal("Add Defender Exclusion", promptTitle);
                Assert.NotNull(promptMessage);
                Assert.Contains(stat.Name, promptMessage);

                // "No" was answered — the exclusion must not have been added.
                Assert.DoesNotContain(stat.Name,
                    DefenderExclusionManager.GetExclusions().Processes);
            }
            finally
            {
                DefenderExclusions.ShowConfirmation = originalPrompt;
                window.Close();
            }
        });
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));

    private static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
