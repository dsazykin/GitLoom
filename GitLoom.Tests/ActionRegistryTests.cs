using System;
using System.Linq;
using System.Threading.Tasks;
using GitLoom.Core.Actions;
using Xunit;

namespace GitLoom.Tests;

// TI-18: the registry is UI-free and must (a) throw on duplicate ids at registration time and (b) filter
// unavailable actions by their live CanExecute — never list an action that would crash on invoke.
public class ActionRegistryTests
{
    private static AppAction Make(string id, Func<bool>? canExecute = null) => new()
    {
        Id = id,
        Title = id,
        Category = "Test",
        CanExecute = canExecute ?? (() => true),
        Execute = () => Task.CompletedTask,
    };

    [Fact]
    public void Registry_ShouldFilterByCanExecute()
    {
        var registry = new ActionRegistry();
        registry.Register(Make("always", () => true));
        registry.Register(Make("never", () => false));
        bool gate = false;
        registry.Register(Make("gated", () => gate));

        Assert.Equal(3, registry.All.Count);

        var enabled = registry.Enabled().Select(a => a.Id).ToList();
        Assert.Contains("always", enabled);
        Assert.DoesNotContain("never", enabled);
        Assert.DoesNotContain("gated", enabled);

        // CanExecute is evaluated live, so flipping the gate flips availability.
        gate = true;
        Assert.Contains("gated", registry.Enabled().Select(a => a.Id));
    }

    [Fact]
    public void Registry_DuplicateIds_ShouldThrowOnRegistration()
    {
        var registry = new ActionRegistry();
        registry.Register(Make("commit"));

        Assert.Throws<ArgumentException>(() => registry.Register(Make("commit")));
    }

    [Fact]
    public void Registry_EmptyId_ShouldThrow()
    {
        var registry = new ActionRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(Make("")));
    }

    [Fact]
    public void Registry_All_ShouldPreserveRegistrationOrder()
    {
        var registry = new ActionRegistry();
        registry.Register(Make("a"));
        registry.Register(Make("b"));
        registry.Register(Make("c"));

        Assert.Equal(new[] { "a", "b", "c" }, registry.All.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void Registry_Find_ShouldReturnActionOrNull()
    {
        var registry = new ActionRegistry();
        var commit = Make("commit");
        registry.Register(commit);

        Assert.Same(commit, registry.Find("commit"));
        Assert.Null(registry.Find("missing"));
    }

    [Fact]
    public void Enabled_ShouldTreatThrowingCanExecute_AsUnavailable()
    {
        var registry = new ActionRegistry();
        registry.Register(Make("boom", () => throw new InvalidOperationException()));

        Assert.Empty(registry.Enabled());
    }
}
