using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Step 2: ensure the <c>GitLoomEnv</c> distro exists, importing it from the versioned tarball when
/// absent (<c>wsl --import GitLoomEnv &lt;installDir&gt; &lt;tarball&gt; --version 2</c>). A failed
/// partial import is unregistered before the typed failure so a retry starts clean (edge row 4).
/// </summary>
public sealed class ImportDistroStep : IBootstrapStep
{
    private readonly IWslRunner _wsl;
    private readonly IBootstrapFileSystem _fs;
    private readonly BootstrapOptions _options;

    public ImportDistroStep(IWslRunner wsl, IBootstrapFileSystem fs, BootstrapOptions options)
    {
        _wsl = wsl;
        _fs = fs;
        _options = options;
    }

    public string Name => "Import GitLoomEnv";

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
        var distros = WslRunner.ParseDistroList(result.StdOut);
        return distros.Any(d => string.Equals(d, _options.DistroName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        if (!_fs.FileExists(_options.TarballPath))
            throw new BootstrapException(Name, $"The GitLoomOS tarball was not found at '{_options.TarballPath}'.");

        log.Report($"Importing {_options.DistroName} from {_options.TarballPath}…");
        var result = await _wsl.RunAsync(
            WslCommands.Import(_options.InstallDir, _options.TarballPath), stdin: null, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Nothing half-imported: drop the partial distro before surfacing the failure so a retry
            // is clean (edge row 4). Best-effort — the import failure is the real error.
            try { await _wsl.RunAsync(WslCommands.Unregister(), stdin: null, ct).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }

            throw new BootstrapException(Name,
                $"Importing {_options.DistroName} failed (exit {result.ExitCode}). {result.StdErr}".Trim());
        }

        log.Report($"{_options.DistroName} imported.");
    }
}
