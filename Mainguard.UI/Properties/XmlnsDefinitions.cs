using Avalonia.Metadata;

// Step 2c/2g: the design-system base types live in Mainguard.UI under the Mainguard.UI.* CLR namespaces,
// but their consuming XAML lives in OTHER assemblies (Mainguard.App.Shell, Mainguard.Agents.UI). Avalonia's
// compiled XAML resolves a bare `using:`/`clr-namespace:` ONLY within the assembly being compiled, so the
// base types (ChromedWindow, CustomTitleBar, ViewLocator, the shared converters) would be invisible to the
// shell/Pro-UI XAML. Registering their namespaces on the standard Avalonia XML namespace exposes them
// prefix-free under the `xmlns="https://github.com/avaloniaui"` every file already declares — the
// established pattern for a shared Avalonia control library. The root mapping covers ViewLocator/
// ViewModelBase (referenced e.g. by App.axaml's <ViewLocator/> DataTemplate).
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Mainguard.UI")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Mainguard.UI.Controls")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Mainguard.UI.Views")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Mainguard.UI.Converters")]
