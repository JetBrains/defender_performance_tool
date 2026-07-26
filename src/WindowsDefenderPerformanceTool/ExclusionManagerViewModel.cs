using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using ReactiveUI;

namespace WindowsDefenderPerformanceTool;

/// <summary>One tab of the exclusion manager: a single exclusion list with add/remove.</summary>
public sealed class ExclusionKindViewModel : ReactiveObject
{
    private readonly ExclusionManagerViewModel _owner;

    public ExclusionKind Kind { get; }
    public string Title { get; }
    public string Hint { get; }
    public ObservableCollection<string> Items { get; } = new();

    private string _newValue = "";
    public string NewValue
    {
        get => _newValue;
        set => this.RaiseAndSetIfChanged(ref _newValue, value);
    }

    private string? _selectedItem;
    public string? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<string?, Unit> RemoveCommand { get; }

    public ExclusionKindViewModel(ExclusionManagerViewModel owner, ExclusionKind kind, string title, string hint)
    {
        _owner = owner;
        Kind = kind;
        Title = title;
        Hint = hint;

        var canAdd = this.WhenAnyValue(
                x => x.NewValue,
                x => x._owner.IsBusy,
                (value, busy) => !busy && !string.IsNullOrWhiteSpace(value))
            .ObserveOn(RxApp.MainThreadScheduler);

        AddCommand = ReactiveCommand.CreateFromTask(AddAsync, canAdd);
        AddCommand.ThrownExceptions.Subscribe(_owner.ReportError);

        RemoveCommand = ReactiveCommand.CreateFromTask<string?>(RemoveAsync);
        RemoveCommand.ThrownExceptions.Subscribe(_owner.ReportError);
    }

    private async Task AddAsync()
    {
        var value = ValidateAndNormalize(NewValue); // throws on invalid input

        var confirm = MessageBox.Show(
            $"Add this {Title.TrimEnd('s').ToLowerInvariant()} to the Windows Defender exclusion list?\n\n{value}\n\n" +
            "Every exclusion is a protection gap that lowers your defenses, so use exclusions sparingly.",
            "Add Defender Exclusion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await Task.Run(() => DefenderExclusionManager.AddExclusion(Kind, value));
        NewValue = "";
        await _owner.RefreshAsync();
        _owner.SetStatus($"Added {Title.ToLowerInvariant()} exclusion: {value}", isError: false);
    }

    private async Task RemoveAsync(string? value)
    {
        value ??= SelectedItem;
        if (string.IsNullOrEmpty(value)) return;
        var target = value!; // IsNullOrEmpty isn't null-annotated on net48

        await Task.Run(() => DefenderExclusionManager.RemoveExclusion(Kind, target));
        await _owner.RefreshAsync();
        _owner.SetStatus($"Removed {Title.ToLowerInvariant()} exclusion: {target}", isError: false);
    }

    private string ValidateAndNormalize(string raw)
    {
        var value = raw.Trim();
        switch (Kind)
        {
            case ExclusionKind.Extension:
                if (value.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                    throw new InvalidOperationException(
                        "An extension exclusion is just a file extension (e.g. \".log\" or \"log\"), not a path.");
                break;
            case ExclusionKind.IpAddress:
                if (!IPAddress.TryParse(value, out _))
                    throw new InvalidOperationException($"\"{value}\" is not a valid IP address.");
                break;
        }
        return value;
    }
}

/// <summary>View model behind the exclusion manager dialog.</summary>
public sealed class ExclusionManagerViewModel : ReactiveObject
{
    public ExclusionKindViewModel Paths { get; }
    public ExclusionKindViewModel Processes { get; }
    public ExclusionKindViewModel Extensions { get; }
    public ExclusionKindViewModel IpAddresses { get; }
    public IReadOnlyList<ExclusionKindViewModel> Kinds { get; }

    public bool IsRunningAsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set => this.RaiseAndSetIfChanged(ref _totalCount, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _statusIsError;
    public bool StatusIsError
    {
        get => _statusIsError;
        private set => this.RaiseAndSetIfChanged(ref _statusIsError, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public ExclusionManagerViewModel()
    {
        Paths = new ExclusionKindViewModel(this, ExclusionKind.Path, "Paths",
            "File or folder paths that are never scanned (e.g. C:\\Dev\\Projects).");
        Processes = new ExclusionKindViewModel(this, ExclusionKind.Process, "Processes",
            "Process image names or full paths whose file activity is not scanned (e.g. devenv.exe).");
        Extensions = new ExclusionKindViewModel(this, ExclusionKind.Extension, "Extensions",
            "File extensions that are never scanned (e.g. .pdb).");
        IpAddresses = new ExclusionKindViewModel(this, ExclusionKind.IpAddress, "IP Addresses",
            "IP addresses whose inbound/outbound traffic is not scanned (e.g. 192.168.1.10).");
        Kinds = new[] { Paths, Processes, Extensions, IpAddresses };

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        RefreshCommand.ThrownExceptions.Subscribe(ReportError);

        RefreshCommand.Execute().Subscribe();
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var snapshot = await Task.Run(DefenderExclusionManager.GetExclusions);
            Apply(snapshot);

            if (snapshot.HiddenFromLocalUsers)
                SetStatus("Defender hides exclusions from non-administrators. Restart the tool as administrator to view and manage them.", isError: true);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(ExclusionSnapshot snapshot)
    {
        foreach (var kind in Kinds)
        {
            kind.Items.Clear();
            foreach (var value in snapshot.For(kind.Kind))
                kind.Items.Add(value);
        }
        TotalCount = snapshot.TotalCount;
    }

    public void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    public void ReportError(Exception ex) => SetStatus(ex.Message, isError: true);
}
