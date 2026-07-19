using System.Threading.Tasks;

namespace GitLoom.App.Services;

/// <summary>
/// Asks the user to confirm a destructive or irreversible action. Abstracted so ViewModels
/// (e.g. the T-09 graph "Hard reset" item) can be unit-tested with a fake that records the
/// ask, satisfying the "hard reset always confirms" invariant without a live window.
/// </summary>
public interface IConfirmationService
{
    /// <summary>Returns true only if the user confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmButtonText);
}
