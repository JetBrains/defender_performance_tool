using Xunit;

namespace WindowsDefenderPerformanceTool.Tests.UiAutomation;

/// <summary>
/// All UI automation tests share a single test collection so xUnit never runs them in
/// parallel: they drive one interactive desktop (real mouse/keyboard input), launch app
/// instances that compete for machine-wide resources (ETW sessions, the ETW provider
/// state), and change the machine's real Windows Defender configuration. Running them
/// concurrently lets them steal each other's input focus, close each other's dialogs,
/// and starve each other of scan events.
/// </summary>
[CollectionDefinition(Name)]
public sealed class UiAutomationCollection
{
    public const string Name = "UI automation";
}
