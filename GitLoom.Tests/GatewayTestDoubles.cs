using System.Collections.Generic;
using Mainguard.Agents.Agents;

namespace GitLoom.Tests;

/// <summary>
/// A recording <see cref="IAgentSupervisor"/> for the P2-08 tests. Note there is deliberately <b>no</b>
/// kill hook — budget exhaustion pauses, it never kills (rejection trigger), so "not killed" is
/// structural. Tests assert the pause/resume/state calls it recorded.
/// </summary>
public sealed class FakeAgentSupervisor : IAgentSupervisor
{
    public List<string> Paused { get; } = new();

    public List<string> Resumed { get; } = new();

    public List<(string Agent, string State, string? Reason)> StateChanges { get; } = new();

    public void PauseInput(string agentId) => Paused.Add(agentId);

    public void ResumeInput(string agentId) => Resumed.Add(agentId);

    public void MarkState(string agentId, string state, string? reason) => StateChanges.Add((agentId, state, reason));

    public string? LastState(string agentId)
    {
        for (var i = StateChanges.Count - 1; i >= 0; i--)
        {
            if (StateChanges[i].Agent == agentId)
            {
                return StateChanges[i].State;
            }
        }

        return null;
    }
}
