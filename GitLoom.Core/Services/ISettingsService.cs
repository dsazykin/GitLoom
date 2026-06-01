using System;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

public interface ISettingsService
{
    UserPreferences Current { get; }
    void Update(Action<UserPreferences> updateAction);
    void Load();
    void Save();
}
