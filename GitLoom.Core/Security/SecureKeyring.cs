using System;
using System.IO;
using Microsoft.AspNetCore.DataProtection;

namespace GitLoom.Core.Security;

public interface ISecureKeyring
{
    void SaveSecret(string key, string secret);
    string? RetrieveSecret(string key);
    void DeleteSecret(string key);
}

public class SecureKeyring : ISecureKeyring
{
    private readonly IDataProtector _protector;
    private readonly string _storageDirectory;

    public SecureKeyring()
    {
        _storageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitLoom", "Keyring");
        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }

        var dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_storageDirectory),
            options => { options.SetApplicationName("GitLoom"); }
        );
        _protector = dataProtectionProvider.CreateProtector("GitLoom.Keyring.v1");
    }

    public void SaveSecret(string key, string secret)
    {
        string encryptedSecret = _protector.Protect(secret);
        string filePath = Path.Combine(_storageDirectory, $"{key}.keyring");
        File.WriteAllText(filePath, encryptedSecret);
    }

    public string? RetrieveSecret(string key)
    {
        string filePath = Path.Combine(_storageDirectory, $"{key}.keyring");
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string encryptedSecret = File.ReadAllText(filePath);
            return _protector.Unprotect(encryptedSecret);
        }
        catch
        {
            // Decryption failed (e.g. moved to a different machine, key revoked)
            return null;
        }
    }

    public void DeleteSecret(string key)
    {
        string filePath = Path.Combine(_storageDirectory, $"{key}.keyring");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
