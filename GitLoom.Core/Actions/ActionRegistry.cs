using System;
using System.Collections.Generic;
using System.Linq;

namespace GitLoom.Core.Actions;

/// <summary>
/// The UI-free catalog of invokable <see cref="AppAction"/>s (T-18). Registration is order-preserving;
/// a duplicate <see cref="AppAction.Id"/> is a programming error and throws. <see cref="Enabled"/> filters
/// by each action's live <see cref="AppAction.CanExecute"/> so the palette never lists an action that would
/// crash on invoke.
/// </summary>
public sealed class ActionRegistry
{
    private readonly List<AppAction> _actions = new();
    private readonly Dictionary<string, AppAction> _byId = new(StringComparer.Ordinal);

    /// <summary>Registers an action. Throws <see cref="ArgumentException"/> on a duplicate id.</summary>
    public void Register(AppAction action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (string.IsNullOrEmpty(action.Id))
            throw new ArgumentException("AppAction.Id must be non-empty.", nameof(action));
        if (_byId.ContainsKey(action.Id))
            throw new ArgumentException($"An action with id '{action.Id}' is already registered.", nameof(action));

        _byId.Add(action.Id, action);
        _actions.Add(action);
    }

    /// <summary>Every registered action, in registration order.</summary>
    public IReadOnlyList<AppAction> All => _actions;

    /// <summary>Actions whose <see cref="AppAction.CanExecute"/> currently returns true.</summary>
    public IReadOnlyList<AppAction> Enabled() => _actions.Where(a => SafeCanExecute(a)).ToList();

    /// <summary>Looks up an action by id, or null if none is registered.</summary>
    public AppAction? Find(string id) => _byId.TryGetValue(id, out var a) ? a : null;

    private static bool SafeCanExecute(AppAction a)
    {
        try { return a.CanExecute(); }
        catch { return false; }
    }
}
