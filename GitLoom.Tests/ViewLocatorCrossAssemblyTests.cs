using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GitLoom.App;
using GitLoom.App.ViewModels;
using Xunit;

namespace GitLoom.Tests;

// ────────────────────────────────────────────────────────────────────────────────────────────────
// 1e — multi-assembly ViewLocator (ADR-0001). Proves the cross-assembly resolution mechanism WHILE the
// app is still single-project, using a probe VM/View pair that lives in THIS test assembly — the same
// shape a Phase-2 Pro-UI assembly (Mainguard.Agents.UI) will have: a ViewModel whose parallel-named
// View sits outside the shell. The two directions are the whole contract: with only the shell
// registered the probe cannot resolve (its View isn't in the shell), and registering the probe's own
// assembly makes it resolve. That is exactly the path the assembly split will exercise.
// ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A dummy ViewModel living in the TEST assembly (not the shell). Its
/// <c>GitLoom.Tests.ProbeViewModel</c> full name transforms to <c>GitLoom.Tests.ProbeView</c> — a type the
/// shell assembly does NOT contain — so it is the perfect negative/positive probe for cross-assembly
/// resolution. Minimal: <see cref="ViewModelBase"/> is all the locator's <c>Match</c> needs.</summary>
public sealed class ProbeViewModel : ViewModelBase
{
}

/// <summary>The parallel-named View for <see cref="ProbeViewModel"/> (…<c>ProbeViewModel</c> →
/// …<c>ProbeView</c>, same namespace) — a bare <see cref="UserControl"/> with no XAML, so
/// <c>Activator.CreateInstance</c> succeeds without an <c>InitializeComponent</c>.</summary>
public sealed class ProbeView : UserControl
{
}

public class ViewLocatorCrossAssemblyTests
{
    // [AvaloniaFact] because Build() constructs Avalonia controls (the Not-Found TextBlock and the probe
    // UserControl), which need the headless app + UI thread — same host the render harnesses use.
    [AvaloniaFact]
    public void Build_ResolvesProbeView_OnlyWhenItsAssemblyIsRegistered()
    {
        var original = ViewLocator.ViewAssemblies;
        try
        {
            var locator = new ViewLocator();

            // (1) Only the shell registered (the startup default): the probe's View lives in the TEST
            // assembly, not the shell, so resolution misses and Build returns the honest placeholder.
            ViewLocator.ViewAssemblies = new[] { typeof(ViewLocator).Assembly };
            var missed = locator.Build(new ProbeViewModel());
            var placeholder = Assert.IsType<TextBlock>(missed);
            Assert.Equal("Not Found: GitLoom.Tests.ProbeView", placeholder.Text);

            // (2) Register the probe's own assembly — the exact cross-assembly path the Phase-2 split
            // uses — and the same ViewModel now resolves to its parallel-named View.
            ViewLocator.ViewAssemblies = new[] { typeof(ViewLocator).Assembly, typeof(ProbeViewModel).Assembly };
            var resolved = locator.Build(new ProbeViewModel());
            Assert.IsType<ProbeView>(resolved);
        }
        finally
        {
            // Restore the process-wide default so no other test in the shared headless session is
            // affected by our registration (the render harnesses rely on the shell-only default).
            ViewLocator.ViewAssemblies = original;
        }
    }
}
