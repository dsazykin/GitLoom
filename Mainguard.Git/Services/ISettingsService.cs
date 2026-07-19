using System;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

public interface ISettingsService
{
    UserPreferences Current { get; }
    void Update(Action<UserPreferences> updateAction);
    void Load();
    void Save();
}
