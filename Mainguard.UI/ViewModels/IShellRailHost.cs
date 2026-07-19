using CommunityToolkit.Mvvm.Input;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The narrow shell contract the Pro agent rail (<c>AgentRailView</c>, now in Mainguard.Agents.UI) binds UP
/// to through its host Window's DataContext: the collapse state it mirrors and the "open this agent"
/// command it invokes. Defined in the base UI layer (step 2e) so the Pro rail can name it — as
/// <c>vm:IShellRailHost</c> — in its compiled XAML WITHOUT the Pro assembly referencing the shell, and
/// <c>MainWindowViewModel</c> (the shell) satisfies it. Kept minimal: exactly the two members the rail
/// reaches through the window.
/// </summary>
public interface IShellRailHost
{
    /// <summary>Whether the section rail is expanded (labels shown) vs collapsed (icons only).</summary>
    bool IsRailExpanded { get; }

    /// <summary>Open (and focus) the given agent's document — the CommandParameter is the agent id.</summary>
    IRelayCommand<string> ShowAgentCommand { get; }
}
