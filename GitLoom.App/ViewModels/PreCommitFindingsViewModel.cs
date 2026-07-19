using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Safety;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// T-30 pre-commit findings panel. Owns the scan results (grouped by severity), the all-clear /
/// blocker states, and the "Commit anyway" override. It runs the scanner off the UI thread and
/// exposes the outcome to the staging panel, which drives the actual commit gating. Severity colors
/// are chosen in the View from Danger/Warning/Info tokens — never here (no color in a VM).
/// </summary>
public partial class PreCommitFindingsViewModel : ViewModelBase
{
    private readonly IPreCommitScanner _scanner;
    private readonly string _repoPath;
    private readonly Func<UserPreferences> _preferences;
    private readonly ISettingsService? _settings;
    private readonly Action<string, int?>? _revealInDiff;

    private IReadOnlyList<PreCommitFinding> _lastFindings = Array.Empty<PreCommitFinding>();

    /// <summary>Raised when the user clicks "Commit anyway" on a blocker; the staging panel commits.</summary>
    public event Func<Task>? CommitConfirmed;

    public PreCommitFindingsViewModel(
        IPreCommitScanner scanner,
        string repoPath,
        Func<UserPreferences>? preferences = null,
        ISettingsService? settings = null,
        Action<string, int?>? revealInDiff = null)
    {
        _scanner = scanner;
        _repoPath = repoPath;
        _preferences = preferences ?? (() => new UserPreferences());
        _settings = settings;
        _revealInDiff = revealInDiff;
    }

    [ObservableProperty]
    private ObservableCollection<PreCommitFindingGroupViewModel> _groups = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllClear))]
    private bool _hasRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllClear))]
    private bool _isScanning;

    /// <summary>The blocker banner is showing and the commit is paused for an explicit decision.</summary>
    [ObservableProperty]
    private bool _awaitingOverride;

    public bool HasFindings => Groups.Count > 0;

    public bool HasBlockers => _lastFindings.Any(f => f.Severity == FindingSeverity.Blocker);

    /// <summary>Scan ran, nothing found, not mid-scan → the "all clear" affordance.</summary>
    public bool IsAllClear => HasRun && !IsScanning && Groups.Count == 0;

    public int BlockerCount => _lastFindings.Count(f => f.Severity == FindingSeverity.Blocker);
    public int WarningCount => _lastFindings.Count(f => f.Severity == FindingSeverity.Warning);
    public int InfoCount => _lastFindings.Count(f => f.Severity == FindingSeverity.Info);

    public string SummaryText =>
        HasFindings
            ? string.Join("  ·  ", new[]
            {
                BlockerCount > 0 ? $"{BlockerCount} blocking" : null,
                WarningCount > 0 ? $"{WarningCount} warning" : null,
                InfoCount > 0 ? $"{InfoCount} info" : null,
            }.Where(s => s != null))
            : "No issues found";

    /// <summary>Auto-scan-before-commit toggle, persisted to <see cref="UserPreferences"/>.</summary>
    public bool AutoScanEnabled
    {
        get => _preferences().PreCommitScanEnabled;
        set
        {
            if (_preferences().PreCommitScanEnabled == value) return;
            _settings?.Update(p => p.PreCommitScanEnabled = value);
            OnPropertyChanged();
        }
    }

    /// <summary>Runs the scanner off the UI thread and refreshes the grouped results.</summary>
    public async Task<IReadOnlyList<PreCommitFinding>> ScanAsync()
    {
        IsScanning = true;
        try
        {
            var prefs = _preferences();
            var options = new PreCommitScanOptions
            {
                MaxFileBytes = Math.Max(1, prefs.PreCommitMaxFileMB) * 1024L * 1024L,
            };
            var findings = await Task.Run(() => _scanner.ScanStaged(_repoPath, options));
            SetFindings(findings);
            return findings;
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Populates the grouped view from a finding list (also the render/test seam).</summary>
    public void SetFindings(IReadOnlyList<PreCommitFinding> findings)
    {
        _lastFindings = findings;

        var groups = findings
            .GroupBy(f => f.Severity)
            .OrderBy(g => SeverityRank(g.Key))
            .Select(g => new PreCommitFindingGroupViewModel(
                g.Key,
                g.Select(f => new PreCommitFindingRowViewModel(f)).ToList()))
            .ToList();

        Groups = new ObservableCollection<PreCommitFindingGroupViewModel>(groups);
        HasRun = true;

        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasBlockers));
        OnPropertyChanged(nameof(IsAllClear));
        OnPropertyChanged(nameof(BlockerCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(InfoCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Clears the panel back to its pre-scan state.</summary>
    public void Reset()
    {
        _lastFindings = Array.Empty<PreCommitFinding>();
        Groups = new ObservableCollection<PreCommitFindingGroupViewModel>();
        HasRun = false;
        AwaitingOverride = false;
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasBlockers));
        OnPropertyChanged(nameof(BlockerCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(InfoCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    [RelayCommand]
    private async Task Rescan() => await ScanAsync();

    [RelayCommand]
    private void Reveal(PreCommitFindingRowViewModel? row)
    {
        if (row is null || string.IsNullOrEmpty(row.Path)) return;
        _revealInDiff?.Invoke(row.Path, row.Line);
    }

    /// <summary>Explicit override of a blocker — proceed with the commit.</summary>
    [RelayCommand]
    private async Task CommitAnyway()
    {
        AwaitingOverride = false;
        Reset();
        if (CommitConfirmed is not null)
        {
            await CommitConfirmed.Invoke();
        }
    }

    /// <summary>Cancel the blocked commit; leave the findings visible so the user can fix them.</summary>
    [RelayCommand]
    private void Cancel() => AwaitingOverride = false;

    private static int SeverityRank(FindingSeverity s) => s switch
    {
        FindingSeverity.Blocker => 0,
        FindingSeverity.Warning => 1,
        _ => 2,
    };
}

/// <summary>One severity section (Blockers / Warnings / Info) in the findings panel.</summary>
public partial class PreCommitFindingGroupViewModel : ViewModelBase
{
    public PreCommitFindingGroupViewModel(FindingSeverity severity, IReadOnlyList<PreCommitFindingRowViewModel> rows)
    {
        Severity = severity;
        Rows = new ObservableCollection<PreCommitFindingRowViewModel>(rows);
    }

    public FindingSeverity Severity { get; }
    public ObservableCollection<PreCommitFindingRowViewModel> Rows { get; }

    public string HeaderText => Severity switch
    {
        FindingSeverity.Blocker => $"Blockers ({Rows.Count})",
        FindingSeverity.Warning => $"Warnings ({Rows.Count})",
        _ => $"Info ({Rows.Count})",
    };

    // Mutually-exclusive booleans so the View maps to a design token — no color in the VM.
    public bool IsBlocker => Severity == FindingSeverity.Blocker;
    public bool IsWarning => Severity == FindingSeverity.Warning;
    public bool IsInfo => Severity == FindingSeverity.Info;
}

/// <summary>One finding row: kind icon key, path:line, and the redacted message.</summary>
public partial class PreCommitFindingRowViewModel : ViewModelBase
{
    private readonly PreCommitFinding _finding;

    public PreCommitFindingRowViewModel(PreCommitFinding finding) => _finding = finding;

    public FindingKind Kind => _finding.Kind;
    public FindingSeverity Severity => _finding.Severity;
    public string Path => _finding.Path;
    public int? Line => _finding.Line;
    public string Message => _finding.Message;
    public string Rule => _finding.Rule;

    public string Location =>
        string.IsNullOrEmpty(Path)
            ? string.Empty
            : Line.HasValue ? $"{Path}:{Line.Value}" : Path;

    public bool CanReveal => !string.IsNullOrEmpty(Path);

    // Kind → glyph key resolved to a shared icon in the View.
    public bool IsSecret => Kind == FindingKind.Secret;
    public bool IsLargeFile => Kind == FindingKind.LargeFile;
    public bool IsMergeMarker => Kind == FindingKind.MergeMarker;
    public bool IsManyFiles => Kind == FindingKind.ManyFiles;

    public bool IsBlocker => Severity == FindingSeverity.Blocker;
    public bool IsWarning => Severity == FindingSeverity.Warning;
    public bool IsInfo => Severity == FindingSeverity.Info;
}
