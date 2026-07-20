namespace Mainguard.Git.Exceptions;

/// <summary>
/// Raised when creating or renaming a <see cref="Models.GitProfile"/> to a name that already
/// exists (case-insensitive) — T-21. Typed so the profiles UI can surface an inline "name in use"
/// message rather than string-matching.
/// </summary>
public sealed class DuplicateProfileNameException : MainguardException
{
    public DuplicateProfileNameException(string name)
        : base($"A profile named '{name}' already exists.")
    {
        ProfileName = name;
    }

    public string ProfileName { get; }
}
