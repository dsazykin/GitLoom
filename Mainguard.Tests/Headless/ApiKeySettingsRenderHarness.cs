using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Security;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the P2-01 "AI Providers" settings page (ApiKeySettingsView) and the CLI-OAuth ToS notice
// (CliOAuthTosDialog) offscreen (headless Skia) in ALL FIVE themes, so the design-system pass can be
// reviewed without a display. Everything is offline: a fake health-check delegate + a temp-dir keyring
// (no live network, no real keyring). Captures PNGs to the gitignored artifacts_headless/ — visual
// review, plus a non-empty-frame assertion so a blank render fails the build.
public class ApiKeySettingsRenderHarness
{
    [AvaloniaFact]
    public void Capture_ApiKeySettings_AndTosDialog_AllThemes()
    {
        try
        {
            foreach (var theme in ThemeManager.Themes)
            {
                ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                CaptureState(theme.Key, "empty", BuildEmpty());
                CaptureState(theme.Key, "valid", BuildValid());
                CaptureState(theme.Key, "invalid", BuildInvalid());
                CaptureTosDialog(theme.Key);
            }
        }
        finally
        {
            // Restore the default so theme state doesn't leak into other render harnesses.
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    // Empty / no-key state: fresh VM, nothing stored, no status line.
    private ApiKeySettingsViewModel BuildEmpty()
    {
        var dir = NewTempDir();
        return new ApiKeySettingsViewModel((ISecureKeyStore)new SecureKeyring(dir), NoNetworkHealthValid);
    }

    // Valid-key state: a stored Anthropic key + the "supports ~N agents" success line + CLI-OAuth enabled.
    private ApiKeySettingsViewModel BuildValid()
    {
        var dir = NewTempDir();
        var keyring = new SecureKeyring(dir);
        keyring.SaveSecret("llm_anthropic", "sk-ant-demo-stored");
        var vm = new ApiKeySettingsViewModel((ISecureKeyStore)keyring, NoNetworkHealthValid)
        {
            IsHealthError = false,
            HealthMessage = "Key valid — supports ~12 concurrent agents.",
            IsCliOAuthEnabled = true,
        };
        return vm;
    }

    // Invalid-key state: inline error line, nothing stored.
    private ApiKeySettingsViewModel BuildInvalid()
    {
        var dir = NewTempDir();
        var vm = new ApiKeySettingsViewModel((ISecureKeyStore)new SecureKeyring(dir), NoNetworkHealthInvalid)
        {
            SelectedProvider = "openai",
            IsHealthError = true,
            HealthMessage = "The openai API rejected the key (401).",
        };
        return vm;
    }

    private void CaptureState(string themeKey, string state, ApiKeySettingsViewModel vm)
    {
        // ApiKeySettingsView is a UserControl now (embedded as a Settings page) — wrap it in a plain
        // Window for the headless render harness, same as every other migrated page harness.
        var win = new Avalonia.Controls.Window
        {
            Width = 600,
            Height = 600,
            Content = new ApiKeySettingsView { DataContext = vm },
        };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"apikey_settings_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"apikey {themeKey}/{state} PNG is empty");

        HarnessHygiene.Teardown(win);
        Settle();
    }

    private void CaptureTosDialog(string themeKey)
    {
        var vm = new CliOAuthTosDialogViewModel("anthropic");
        var dialog = new CliOAuthTosDialog { DataContext = vm };
        dialog.Show();
        Settle();

        var frame = dialog.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"cli_oauth_tos_{themeKey}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"ToS dialog {themeKey} PNG is empty");

        HarnessHygiene.Teardown(dialog);
        Settle();
    }

    // Offline health-check seams — never touch the network.
    private static System.Threading.Tasks.Task<KeyHealth> NoNetworkHealthValid(string p, string k, CancellationToken ct)
        => System.Threading.Tasks.Task.FromResult(new KeyHealth { IsValid = true, EstimatedConcurrentAgents = 12 });

    private static System.Threading.Tasks.Task<KeyHealth> NoNetworkHealthInvalid(string p, string k, CancellationToken ct)
        => System.Threading.Tasks.Task.FromResult(new KeyHealth { IsValid = false, FailureReason = "rejected" });

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "mainguard-apikey-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
