using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Security;

namespace GitLoom.App.ViewModels;

/// <summary>
/// SSH keys preferences page (T-14): lists the key pairs in <c>~/.ssh</c>, generates a
/// new ed25519 key (optionally passphrase-protected — the passphrase goes to the
/// keyring, never a log/URL/argv on any network path), and copies a public key.
/// Constructed directly (no DI); the <see cref="SshKeyService"/> is injectable so the
/// VM is unit-testable against a temp <c>~/.ssh</c>.
/// </summary>
public partial class SshKeysViewModel : ViewModelBase
{
    private readonly SshKeyService _ssh;

    public ObservableCollection<SshKeyRowViewModel> Keys { get; } = new();

    [ObservableProperty]
    private string _newKeyName = "id_ed25519";

    [ObservableProperty]
    private string _newKeyComment = string.Empty;

    [ObservableProperty]
    private string _newKeyPassphrase = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Set by the View to copy public-key text to the OS clipboard (UI concern).</summary>
    public Action<string>? CopyToClipboard { get; set; }

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public SshKeysViewModel(SshKeyService? sshKeyService = null)
    {
        _ssh = sshKeyService ?? new SshKeyService();
        Reload();
    }

    public string SshDirectory => _ssh.SshDirectory;

    private void Reload()
    {
        Keys.Clear();
        foreach (var key in _ssh.ListKeys())
            Keys.Add(new SshKeyRowViewModel(this, key));
    }

    private bool CanGenerate => !string.IsNullOrWhiteSpace(NewKeyName) && !IsBusy;
    partial void OnNewKeyNameChanged(string value) => GenerateKeyCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => GenerateKeyCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateKey()
    {
        var name = NewKeyName.Trim();
        var path = Path.Combine(_ssh.SshDirectory, name);
        if (File.Exists(path) || File.Exists(path + ".pub"))
        {
            StatusMessage = $"A key named '{name}' already exists.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Generating key…";
        var passphrase = NewKeyPassphrase; // captured before the field is cleared
        var comment = string.IsNullOrWhiteSpace(NewKeyComment) ? null : NewKeyComment.Trim();
        try
        {
            await Task.Run(() => _ssh.Generate(path, passphrase, comment));
            NewKeyPassphrase = string.Empty;
            StatusMessage = $"Generated {name}.";
            Reload();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Key generation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void CopyPublicKey(SshKeyRowViewModel row)
    {
        CopyToClipboard?.Invoke(row.PublicKeyText);
        StatusMessage = $"Copied public key for {row.Name}.";
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One key row on the SSH keys page.</summary>
public partial class SshKeyRowViewModel : ViewModelBase
{
    private readonly SshKeysViewModel _parent;

    public string Name { get; }
    public string KeyType { get; }
    public string Comment { get; }
    public string PublicKeyText { get; }
    public string PrivateKeyPath { get; }
    public bool HasStoredPassphrase { get; }

    public SshKeyRowViewModel(SshKeysViewModel parent, SshKeyInfo info)
    {
        _parent = parent;
        Name = info.Name;
        KeyType = info.KeyType;
        Comment = info.Comment;
        PublicKeyText = info.PublicKeyText;
        PrivateKeyPath = info.PrivateKeyPath;
        HasStoredPassphrase = info.HasStoredPassphrase;
    }

    [RelayCommand]
    private void CopyPublicKey() => _parent.CopyPublicKey(this);
}
