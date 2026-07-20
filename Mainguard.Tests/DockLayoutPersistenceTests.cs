using System.IO;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels.Agents;
using Mainguard.App.Shell.Services;
using Xunit;

namespace Mainguard.Tests;

// TI-P2-13.7: the dock layout round-trips through JSON in appdata, and a corrupt / schema-drifted
// file falls back to the default layout rather than throwing.
public class DockLayoutPersistenceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-dock-" + Path.GetRandomFileName());
        return dir;
    }

    [Fact]
    public void LayoutPersistence_ShouldRoundTripDockState()
    {
        var dir = TempDir();
        try
        {
            var persistence = new DockLayoutPersistence(dir);
            var state = new DockLayoutState(DockLayoutState.CurrentVersion,
                WorkspaceLayoutKind.ConversationDeck, new[] { "terminal", "staging", "diff" });

            persistence.Save("claude", state);
            var restored = persistence.Load("claude");

            Assert.Equal(WorkspaceLayoutKind.ConversationDeck, restored.Layout);
            Assert.Equal(new[] { "terminal", "staging", "diff" }, restored.ToolOrder);
            Assert.Equal(DockLayoutState.CurrentVersion, restored.Version);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_ReturnsDefault_WhenAbsent()
    {
        var dir = TempDir();
        try
        {
            var restored = new DockLayoutPersistence(dir).Load("never-saved");
            Assert.Equal(WorkspaceLayoutKind.FlightDeck, restored.Layout);
            Assert.Equal(new[] { "terminal", "diff", "staging" }, restored.ToolOrder);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_FallsBackToDefault_OnSchemaDrift()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            // A future/garbage schema: wrong version + junk keys.
            File.WriteAllText(Path.Combine(dir, "claude.json"), "{ \"Version\": 999, \"nonsense\": true }");
            var restored = new DockLayoutPersistence(dir).Load("claude");
            Assert.Equal(WorkspaceLayoutKind.FlightDeck, restored.Layout);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_FallsBackToDefault_OnCorruptJson()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "claude.json"), "this is not json {{{");
            var restored = new DockLayoutPersistence(dir).Load("claude", WorkspaceLayoutKind.ConversationDeck);
            Assert.Equal(WorkspaceLayoutKind.ConversationDeck, restored.Layout); // fallback honoured
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
