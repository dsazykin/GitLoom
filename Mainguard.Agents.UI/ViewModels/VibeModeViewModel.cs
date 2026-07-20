using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Vibe Mode (Lane E Part 3; docs/design/VibeModeDesign.md): the zero-knowledge 2-pane surface.
/// Orchestrator events render as friendly cards per the §2 translation table (the Vibe dialect:
/// plain words, safety stated in the same breath). The triage screen carries exactly three
/// actions (P3-02), with the honest disabled state for "Go back" when no green checkpoint exists.
/// </summary>
public partial class VibeModeViewModel : ViewModelBase
{
    private readonly IVibeService _vibe;
    private readonly ICoordinatorService _coordinator;
    private readonly Action _backToPro;
    private int _seenCheckpoints;
    private bool _firstCheckpointCardShown;

    public ObservableCollection<VibeCardViewModel> Cards { get; } = new();

    [ObservableProperty] private string _composerText = "";
    [ObservableProperty] private string _statusLineText = "";
    [ObservableProperty] private bool _isDetailsOpen;
    [ObservableProperty] private string _technicalDetailsText = "";

    // Triage (P3-02)
    [ObservableProperty] private bool _isTriageVisible;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private string _goBackConsequence = "";
    [ObservableProperty] private string _tryAgainConsequence = "Starts fresh with what we learned from the failed attempts.";
    private int _escalationCount;

    // Deploy (P3-04)
    [ObservableProperty] private bool _isPublishing;
    [ObservableProperty] private string _publishChecklistText = "";
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _liveUrl = "";

    public VibeModeViewModel(IVibeService vibe, ICoordinatorService coordinator, Action backToPro)
    {
        _vibe = vibe;
        _coordinator = coordinator;
        _backToPro = backToPro;
        _seenCheckpoints = vibe.GetCheckpoints().Count;

        _vibe.DeployChanged += () => Dispatcher.UIThread.Post(RefreshDeploy);

        Cards.Add(new VibeCardViewModel("AgentWaitingIcon", VibeCardTone.Muted,
            "Tell me what to build or change", "Describe it in your own words — nothing here needs a technical term."));
        RefreshStatusLine();
        RefreshTriageGate();
    }

    // ---- the OPS §3.4 → friendly-card translation (VibeModeDesign §2) ----

    public void OnOrchestratorEvent(AgentEvent e)
    {
        switch (e.Type)
        {
            case "queue_state" when e.Payload.Contains("→Verifying"):
                AddCard("AgentVerifyingIcon", VibeCardTone.Info, "Checking your change works…", null);
                break;
            case "queue_state" when e.Payload.Contains("→Verified"):
                AddCard("CheckmarkIcon", VibeCardTone.Success, "Checked — everything still works.", null);
                break;
            case "rate_limited":
                AddCard("AgentThrottledIcon", VibeCardTone.Muted,
                    "Taking a short breather", "The AI service asked us to slow down. Back in under a minute.");
                break;
            case "budget_exceeded":
                AddCard("AgentThrottledIcon", VibeCardTone.Warning,
                    "Today's budget is used up", "Raise it in Settings, or continue tomorrow.");
                break;
            case "killswitch" when e.Payload.Contains("QueueFrozen"):
                AddCard("AgentPausedIcon", VibeCardTone.Muted,
                    "Everything is paused", "Nothing was lost — your work is saved. Resume whenever you're ready.");
                break;
        }
        RefreshStatusLine();
    }

    /// <summary>Ticked by the shell on telemetry samples: detects new checkpoints (P3-01).</summary>
    public void OnTick()
    {
        var checkpoints = _vibe.GetCheckpoints();
        if (checkpoints.Count > _seenCheckpoints)
        {
            _seenCheckpoints = checkpoints.Count;
            var subtitle = "You can always come back to this point.";
            if (!_firstCheckpointCardShown)
            {
                subtitle += " That's automatic — it happens after every change.";
                _firstCheckpointCardShown = true;
            }
            AddCard("CheckmarkIcon", VibeCardTone.Success, "Progress saved", subtitle);
        }
        RefreshStatusLine();
        RefreshTriageGate();
    }

    private void AddCard(string glyphKey, VibeCardTone tone, string title, string? subtitle)
    {
        // Collapse consecutive duplicates (a readout that repeats is noise, not news).
        if (Cards.LastOrDefault() is { } last && last.Title == title) return;
        Cards.Add(new VibeCardViewModel(glyphKey, tone, title, subtitle));
        while (Cards.Count > 50) Cards.RemoveAt(0);
    }

    private void RefreshStatusLine()
    {
        var lastSaved = _vibe.GetCheckpoints().LastOrDefault();
        var age = lastSaved is null ? "" : $" · saved {(int)Math.Max(0, (DateTimeOffset.Now - lastSaved.When).TotalMinutes)} min ago";
        StatusLineText = $"Working on: your app{age}";
        TechnicalDetailsText = string.Join("\n",
            _vibe.GetCheckpoints().Select(c => $"{c.Sha}  {(c.VerifiedGreen ? "green" : "unverified")}  {c.Summary}"));
    }

    private void RefreshTriageGate()
    {
        var green = _vibe.LastVerifiedGreen;
        CanGoBack = green is not null;
        GoBackConsequence = green is not null
            ? $"Returns to your last saved point that passed its checks — {(int)Math.Max(0, (DateTimeOffset.Now - green.When).TotalMinutes)} minutes ago."
            : "There's no saved point that passed its checks yet — this appears after the first one.";
    }

    // ---- chat ----

    [RelayCommand]
    private void Send()
    {
        var text = ComposerText.Trim();
        if (text.Length == 0) return;
        ComposerText = "";
        Cards.Add(new VibeCardViewModel(null, VibeCardTone.Human, text, null));
        AddCard("AgentWorkingIcon", VibeCardTone.Muted, "On it", "Making the change now — progress saves automatically.");
    }

    // ---- triage (P3-02: exactly three actions) ----

    /// <summary>Prototype affordance: trips the triage screen the way a circuit breaker would.</summary>
    [RelayCommand]
    private void SimulateSnag()
    {
        _escalationCount++;
        TryAgainConsequence = _escalationCount >= 3
            ? "This has happened 3 times — \"Get help\" may be the faster path."
            : "Starts fresh with what we learned from the failed attempts.";
        RefreshTriageGate();
        IsTriageVisible = true;
    }

    [RelayCommand]
    private void ChooseTryAgain()
    {
        IsTriageVisible = false;
        AddCard("WarningIcon", VibeCardTone.Warning, "Hit a snag", "You chose \"Try a different approach\" — starting fresh.");
    }

    [RelayCommand]
    private async Task ChooseGoBackAsync()
    {
        if (_vibe.LastVerifiedGreen is not { } green) return;
        IsTriageVisible = false;
        await _vibe.RestoreCheckpointAsync(green.Sha);
        AddCard("CheckmarkIcon", VibeCardTone.Success, "Back to when it worked",
            "Restored your last saved point that passed its checks. Nothing else changed.");
    }

    [RelayCommand]
    private void ChooseGetHelp()
    {
        IsTriageVisible = false;
        AddCard("DocumentIcon", VibeCardTone.Muted, "Help bundle ready",
            "What happened was packaged with secrets removed, so a person can look at it.");
    }

    // ---- publish (P3-04) ----

    [RelayCommand]
    private async Task PublishAsync()
    {
        IsPublishing = true;
        await _vibe.PublishAsync();
        RefreshDeploy();
    }

    private void RefreshDeploy()
    {
        var d = _vibe.Deploy;
        PublishChecklistText = d.Phase switch
        {
            DeployPhase.Saving => "Saving your progress…",
            DeployPhase.Uploading => "Saved ✓ · Sending your app…",
            DeployPhase.Building => "Saved ✓ · Sent ✓ · Building…",
            DeployPhase.GoingLive => "Saved ✓ · Sent ✓ · Built ✓ · Going live…",
            DeployPhase.Live => "",
            DeployPhase.Failed => "Publishing didn't finish",
            _ => "",
        };
        IsPublishing = d.Phase is DeployPhase.Saving or DeployPhase.Uploading or DeployPhase.Building or DeployPhase.GoingLive;
        IsLive = d.Phase == DeployPhase.Live;
        if (IsLive && d.LiveUrl is { } url && LiveUrl != url)
        {
            LiveUrl = url;
            AddCard("CheckmarkIcon", VibeCardTone.Success, "Published to " + url.Replace("https://", ""),
                "Updates only when you publish again.");
        }
    }

    [RelayCommand]
    private void BackToPro() => _backToPro();
}

public enum VibeCardTone { Human, Success, Info, Warning, Muted }

/// <summary>One chat card: glyph + words first; the brush booleans map to tokens in the View.</summary>
public sealed class VibeCardViewModel : ViewModelBase
{
    public string? GlyphKey { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool HasGlyph => GlyphKey is not null;
    public bool IsHuman { get; }
    public bool IsSuccess { get; }
    public bool IsInfo { get; }
    public bool IsWarning { get; }
    public bool IsMuted { get; }

    public VibeCardViewModel(string? glyphKey, VibeCardTone tone, string title, string? subtitle)
    {
        GlyphKey = glyphKey;
        Title = title;
        Subtitle = subtitle;
        IsHuman = tone == VibeCardTone.Human;
        IsSuccess = tone == VibeCardTone.Success;
        IsInfo = tone == VibeCardTone.Info;
        IsWarning = tone == VibeCardTone.Warning;
        IsMuted = tone == VibeCardTone.Muted;
    }
}
