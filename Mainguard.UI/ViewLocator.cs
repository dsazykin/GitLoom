using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Mainguard.UI.ViewModels;

namespace Mainguard.UI;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    /// <summary>
    /// The assemblies the locator searches for a resolved View type, in order (1e, ADR-0001). Defaults
    /// to just the shell's own assembly so a bare <c>new ViewLocator()</c> — tests and harnesses that
    /// don't run full startup — still resolves in-shell Views. App startup seeds this from the selected
    /// edition's <see cref="Editions.IEditionManifest.ViewAssemblies"/> (see
    /// <c>App.Initialize</c>), so when the Phase-2 assembly split moves the Pro surfaces into their own
    /// <c>Mainguard.Agents.UI</c> the shell resolves those Views too. Kept a manifest-contributed list
    /// rather than an <see cref="AppDomain.GetAssemblies"/> scan on purpose — it stays trim-honest: the
    /// Client head simply never lists the Pro assembly. Mutable-static, matching App's static-<c>Settings</c>
    /// composition pattern (no DI container).
    /// </summary>
    public static IReadOnlyList<Assembly> ViewAssemblies { get; set; } =
        new[] { typeof(ViewLocator).Assembly };

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        // …ViewModels.FooViewModel → …Views.FooView: a whole-string "ViewModel"→"View" replace, which
        // also turns the ".ViewModels." namespace segment into ".Views.". Phase-2 Pro ViewModels MUST
        // keep this parallel `…ViewModels.* → …Views.*` shape (the same relative name under a sibling
        // Views namespace) or the locator would need a namespace map — deliberately NOT built now (1e).
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);

        // Probe each registered assembly by simple (non-assembly-qualified) name. asm.GetType(name) looks
        // the type up in THAT assembly only, so the manifest-contributed list is exactly what widens
        // resolution past the shell — a bare Type.GetType(name) only ever sees the executing assembly and
        // could not reach a future Pro-UI assembly without assembly-qualified names.
        foreach (var asm in ViewAssemblies)
        {
            var type = asm.GetType(name);
            if (type != null)
                return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
