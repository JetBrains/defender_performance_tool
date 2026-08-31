using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace DefenderPerformanceTool.Tests.UiAutomation;

/// <summary>
/// Drives a real instance of DefenderPerformanceTool.exe via UI Automation:
/// launches it, clicks the Exclusions button, works the exclusion manager dialog
/// (tab selection, typing, Add, MessageBox confirmation) and shuts it down cleanly.
/// </summary>
public sealed class AppUnderTest : IDisposable
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Title of the exclusion manager dialog (see ExclusionManagerWindow.xaml).</summary>
    internal const string ExclusionManagerTitle = "Exclusion Manager";

    private readonly UIA3Automation _automation = new();
    private Application? _app;
    private Window? _mainWindow;

    private ConditionFactory Cf => _automation.ConditionFactory;

    public void Launch()
    {
        _app = Application.Launch(ResolveExePath());
        _mainWindow = Retry.WhileNull(
                () => TopLevelWindows().FirstOrDefault(
                    w => SafeTitle(w).StartsWith("Microsoft Defender Performance Tool", StringComparison.Ordinal)),
                timeout: LaunchTimeout, interval: PollInterval, ignoreException: true)
            .Result;
        if (_mainWindow == null)
        {
            // Distinguish the two failure modes in CI logs: an unhandled startup exception
            // (e.g. ETW session-name collision — exit code -532462766 / 0xE0434352) vs. a
            // live process whose window genuinely never materialized (no interactive desktop).
            throw new InvalidOperationException(_app.HasExited
                ? $"The app process exited with code {_app.ExitCode} before showing its main window."
                : "The app is still running after " + LaunchTimeout +
                  " but its main window never appeared (no interactive desktop?).");
        }
    }

    /// <summary>Opens Tools → Exclusion Manager and wraps the dialog that opens.</summary>
    public ExclusionManagerDialog OpenExclusionManager()
    {
        // The exclusion manager lives in the main menu's Tools submenu; its UIA element
        // only exists while the submenu is expanded.
        var toolsMenu = WaitFor(() => _mainWindow!
                .FindFirstDescendant(Cf.ByAutomationId("ToolsMenu"))?
                .AsMenuItem(),
            "Tools menu on the main window");
        toolsMenu.Expand();

        var menuItem = WaitFor(() => _mainWindow!
                .FindFirstDescendant(Cf.ByAutomationId("ExclusionsButton"))?
                .AsMenuItem(),
            "Exclusion Manager item in the Tools menu");
        menuItem.Invoke();

        // The exclusion manager is an owned dialog: UIA exposes it as a descendant of the
        // main window rather than as a desktop child, so search the subtree first.
        var dialog = Retry.WhileNull(
                () => FindWindowByName(_mainWindow!, ExclusionManagerTitle)
                      ?? TopLevelWindows().FirstOrDefault(w => SafeTitle(w) == ExclusionManagerTitle),
                timeout: UiTimeout, interval: PollInterval, ignoreException: true)
            .Result ?? throw new InvalidOperationException("The exclusion manager dialog never opened.");
        return new ExclusionManagerDialog(this, dialog);
    }

    /// <summary>Finds a window titled <paramref name="name"/> anywhere in the app's UI tree.</summary>
    internal Window? FindWindowByName(AutomationElement root, string name) =>
        root.FindAllDescendants(Cf.ByControlType(ControlType.Window).And(Cf.ByName(name)))
            .Select(e => e.AsWindow())
            .FirstOrDefault(w => SafeTitle(w) == name)
        ?? _automation.GetDesktop()
            .FindAllChildren(Cf.ByControlType(ControlType.Window).And(Cf.ByProcessId(_app!.ProcessId)))
            .Select(e => e.AsWindow())
            .FirstOrDefault(w => SafeTitle(w) == name);

    internal Window MainWindow => _mainWindow!;

    /// <summary>Native handle of the app's main window (WPF hosts all controls in one HWND).</summary>
    public IntPtr MainWindowHandle => _mainWindow!.Properties.NativeWindowHandle.ValueOrDefault;

    /// <summary>Waits for the first data row in the "Top Processes" grid; null when none appears in time.</summary>
    public AutomationElement? TryWaitForTopProcessRow(TimeSpan timeout)
    {
        var grid = WaitFor(() => _mainWindow!
                .FindFirstDescendant(Cf.ByAutomationId("TopProcessesGrid")),
            "the 'Top Processes' grid");
        return Retry.WhileNull(
                () => grid.FindAllDescendants(Cf.ByControlType(ControlType.DataItem)).FirstOrDefault()
                      ?? grid.FindAllDescendants(Cf.ByControlType(ControlType.Custom)).FirstOrDefault(),
                timeout: timeout, interval: PollInterval, ignoreException: true)
            .Result;
    }

    /// <summary>Finds an item of the currently open context menu; null when no menu is open.</summary>
    public AutomationElement? FindContextMenuItem(string namePrefix) =>
        _mainWindow!
            .FindAllDescendants(Cf.ByControlType(ControlType.MenuItem))
            .Concat(_automation.GetDesktop()
                .FindAllChildren(Cf.ByProcessId(_app!.ProcessId))
                .SelectMany(w => w.FindAllDescendants(Cf.ByControlType(ControlType.MenuItem))))
            .FirstOrDefault(m => SafeName(m).StartsWith(namePrefix, StringComparison.Ordinal));

    /// <summary>Waits for an item of the currently open context menu whose name starts with the prefix.</summary>
    public AutomationElement WaitForContextMenuItem(string namePrefix) =>
        WaitFor(() => FindContextMenuItem(namePrefix), $"context menu item '{namePrefix}'");

    /// <summary>Waits for a Win32 MessageBox of the app with the given title.</summary>
    public Window WaitForMessageBox(string title) =>
        WaitFor(() => FindWindowByName(_mainWindow!, title), $"the '{title}' MessageBox");

    /// <summary>Element name lookup that survives elements vanishing mid-enumeration.</summary>
    internal static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? ""; }
        catch { return ""; }
    }

    /// <summary>True when the element's peer is gone (e.g. the row was replaced by a refresh).</summary>
    internal static bool IsStale(AutomationElement element)
    {
        try { _ = element.Name; return false; }
        catch { return true; }
    }

    /// <summary>Real mouse click, falling back to the Invoke pattern when the point is not clickable.</summary>
    internal static void Click(AutomationElement element)
    {
        try
        {
            element.Click();
        }
        catch
        {
            element.Patterns.Invoke.PatternOrDefault?.Invoke();
        }
    }

    /// <summary>Closes the main window and waits for the process to exit; kills it if it does not.</summary>
    public void Shutdown()
    {
        if (_app == null || _app.HasExited) return;
        try
        {
            _mainWindow?.Close();
            if (!_app.HasExited) _app.Close();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!_app.HasExited && sw.Elapsed < TimeSpan.FromSeconds(15))
                System.Threading.Thread.Sleep(100);
        }
        finally
        {
            if (!_app.HasExited) _app.Kill();
        }
    }

    public void Dispose()
    {
        try { Shutdown(); }
        catch { /* process may already be gone */ }
        _automation.Dispose();
    }

    internal Window[] TopLevelWindows() => _app!.GetAllTopLevelWindows(_automation);

    /// <summary>Window title lookup that survives windows vanishing mid-enumeration.</summary>
    internal static string SafeTitle(Window window)
    {
        try { return window.Title ?? ""; }
        catch { return ""; }
    }

    internal T WaitFor<T>(Func<T?> query, string description) where T : class =>
        Retry.WhileNull(query, timeout: UiTimeout, interval: PollInterval, ignoreException: true).Result
        ?? throw new InvalidOperationException($"Timed out waiting for {description}.");

    private static string ResolveExePath()
    {
        const string exeName = "DefenderPerformanceTool.exe";

        // 1. Copied next to the test assembly via the project reference.
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(baseDir, exeName);
        if (File.Exists(candidate)) return candidate;

        // 2. The app project's own build output (same configuration as the test build).
        foreach (var config in new[] { "Debug", "Release" })
        {
            candidate = Path.GetFullPath(Path.Combine(baseDir,
                $@"..\..\..\..\DefenderPerformanceTool\bin\{config}\net48\{exeName}"));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate {exeName} next to the tests or in the app project's build output.");
    }
}

/// <summary>Automation wrapper around the "Exclusion Manager" dialog.</summary>
public sealed class ExclusionManagerDialog : IDisposable
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly AppUnderTest _host;
    private readonly Window _window;

    public ExclusionManagerDialog(AppUnderTest host, Window window)
    {
        _host = host;
        _window = window;
    }

    private ConditionFactory Cf => _window.Automation.ConditionFactory;

    /// <summary>Selects the tab whose header text starts with the given prefix (headers end in " (count)").</summary>
    public void SelectTab(string titlePrefix)
    {
        // The TabItem's own UIA name is the view model's ToString() (ItemsSource peers);
        // the visible header text ("Paths (0)") is a child Text element of the TabItem.
        var tabItem = _host.WaitFor(() => _window
                .FindAllDescendants(Cf.ByControlType(ControlType.TabItem))
                .FirstOrDefault(t => t
                    .FindAllDescendants(Cf.ByControlType(ControlType.Text))
                    .Any(x => SafeName(x).StartsWith(titlePrefix, StringComparison.Ordinal))),
            $"tab '{titlePrefix}'");

        if (tabItem.Patterns.SelectionItem.IsSupported)
            tabItem.Patterns.SelectionItem.Pattern.Select();
        else
            AppUnderTest.Click(tabItem);

        // Wait until this tab's input row is actually on screen.
        WaitVisible("NewValueTextBox", ControlType.Edit);
    }

    /// <summary>Types the value, clicks Add, confirms the warning MessageBox and waits for success.</summary>
    public void AddExclusion(string value)
    {
        var textBox = WaitVisible("NewValueTextBox", ControlType.Edit);
        var addButton = WaitVisible("AddButton", ControlType.Button);

        // The Add command is disabled while the view model is busy refreshing.
        Retry.WhileFalse(
            () => addButton.IsEnabled,
            timeout: UiTimeout, interval: PollInterval, ignoreException: true);

        try
        {
            textBox.Click();
            Keyboard.Type(value);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // SendInput is blocked in some sessions (e.g. non-interactive desktops);
            // drive the control through UIA patterns instead.
            textBox.Focus();
            textBox.AsTextBox().Text = value;
        }
        Retry.WhileFalse(
            () => textBox.AsTextBox().Text == value,
            timeout: UiTimeout, interval: PollInterval, ignoreException: true);

        AppUnderTest.Click(addButton);

        ConfirmAddDialog();

        // Success is reported in the status bar once the WMI write + refresh completed.
        var status = WaitVisible("StatusText", ControlType.Text);
        Retry.WhileFalse(
            () => SafeName(status).IndexOf("Added", StringComparison.OrdinalIgnoreCase) >= 0,
            timeout: UiTimeout, interval: PollInterval, ignoreException: true);
        if (SafeName(status).IndexOf("Added", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException($"Add was not confirmed. Status bar shows: \"{SafeName(status)}\"");
    }

    /// <summary>Current entries of the visible tab's exclusion list.</summary>
    public IReadOnlyList<string> GetListedValues() =>
        WaitVisible("ExclusionsListBox", ControlType.List)
            .FindAllDescendants(Cf.ByControlType(ControlType.ListItem))
            .Select(SafeName)
            .ToArray();

    /// <summary>Waits until the exclusion list contains the value (the dialog refreshes asynchronously).</summary>
    public IReadOnlyList<string> WaitForListedValue(string value)
    {
        IReadOnlyList<string> values = Array.Empty<string>();
        Retry.WhileFalse(() =>
        {
            values = GetListedValues();
            return values.Contains(value);
        }, timeout: UiTimeout, interval: PollInterval, ignoreException: true);
        return values;
    }

    public void Dispose()
    {
        try
        {
            if (!_window.IsOffscreen)
                _window.Close();
        }
        catch { /* window may already be closed */ }
    }

    /// <summary>Finds the element by AutomationId, retrying while the tab content is (re)loaded.</summary>
    private AutomationElement WaitVisible(string automationId, ControlType type) =>
        _host.WaitFor(() => _window
                .FindAllDescendants(Cf.ByAutomationId(automationId).And(Cf.ByControlType(type)))
                .FirstOrDefault(e => !e.Properties.IsOffscreen.ValueOrDefault),
            $"'{automationId}' ({type})");

    /// <summary>The Add command shows a Win32 MessageBox ("Add Defender Exclusion"); click Yes.</summary>
    private void ConfirmAddDialog()
    {
        var box = _host.WaitFor(() => _host.FindWindowByName(_window, "Add Defender Exclusion"),
            "the 'Add Defender Exclusion' confirmation MessageBox");
        var yes = _host.WaitFor(() => box
                .FindFirstDescendant(Cf.ByControlType(ControlType.Button).And(Cf.ByName("Yes")))?
                .AsButton(),
            "the 'Yes' button of the confirmation MessageBox");
        AppUnderTest.Click(yes);
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? ""; }
        catch { return ""; }
    }
}
