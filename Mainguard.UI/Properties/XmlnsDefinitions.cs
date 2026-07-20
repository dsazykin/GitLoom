using Avalonia.Metadata;

// Step 2c: the design-system types moved here KEEP their GitLoom.App.* CLR namespaces (so the ~40
// consuming XAML files need no xmlns/using rename — GitLoom.App.Controls/.Views/.Converters are split
// namespaces, half staying in the app). Avalonia's compiled XAML resolves a bare `using:`/`clr-namespace:`
// ONLY within the assembly being compiled, so the moved types (ChromedWindow, CustomTitleBar, the two
// converters) would be invisible to GitLoom.App's XAML. Registering their namespaces on the standard
// Avalonia XML namespace exposes them prefix-free under the `xmlns="https://github.com/avaloniaui"`
// every file already declares — the established pattern for a shared Avalonia control library.
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "GitLoom.App.Controls")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "GitLoom.App.Views")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "GitLoom.App.Converters")]
