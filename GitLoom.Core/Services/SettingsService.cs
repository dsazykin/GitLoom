using System;
using System.IO;
using System.Text.Json;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

public class SettingsService : ISettingsService
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private UserPreferences _current;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirPath = Path.Combine(appData, "GitLoom");
        _filePath = Path.Combine(dirPath, "config.json");
        
        // Load settings immediately on construction to ensure _current is never null and is available in O(1)
        _current = new UserPreferences();
        Load();
    }

    // Constructor that allows custom path, useful for testing!
    public SettingsService(string customFilePath)
    {
        _filePath = customFilePath;
        _current = new UserPreferences();
        Load();
    }

    public UserPreferences Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Update(Action<UserPreferences> updateAction)
    {
        if (updateAction == null) throw new ArgumentNullException(nameof(updateAction));

        lock (_lock)
        {
            updateAction(_current);
            Save();
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var preferences = JsonSerializer.Deserialize<UserPreferences>(json);
                    if (preferences != null)
                    {
                        _current = preferences;
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to a default UserPreferences object without crashing
            }
            
            if (_current == null)
            {
                _current = new UserPreferences();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = _filePath + ".tmp";
                var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
                
                File.WriteAllText(tempPath, json);
                
                if (File.Exists(_filePath))
                {
                    File.Replace(tempPath, _filePath, null);
                }
                else
                {
                    File.Move(tempPath, _filePath);
                }
            }
            catch (Exception)
            {
                // In case File.Replace fails or other errors occur, fall back to basic WriteAllText
                try
                {
                    var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception)
                {
                    // Fail silently to keep invariant: no crashes on settings write error.
                }
            }
        }
    }
}
