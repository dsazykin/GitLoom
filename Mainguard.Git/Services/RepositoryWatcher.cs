using System;
using System.IO;
using System.Threading;

namespace Mainguard.Git.Services;

public class RepositoryWatcher : IDisposable
{
    private readonly string _repoPath;
    private readonly string _gitPath;
    private FileSystemWatcher? _watcher;
    private readonly Timer _debounceTimer;
    private readonly int _debounceMs;
    private bool _disposed;

    // Never fire more than once per this window, even under continuous writes
    // (e.g. a build churning bin/obj or many agents writing at once).
    private const int MaxRefreshMs = 250;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    // Cheap prefix denylist so heavy, uninteresting directories don't trigger a
    // status re-read on every write. Mirrors the ignores GetRepositoryStatus
    // already applies, without paying for a full .gitignore evaluation per event.
    private static readonly string[] IgnoredDirSegments =
    {
        "node_modules", "bin", "obj", ".vs", ".idea", "packages", "dist", "target"
    };

    /// <summary>
    /// Event triggered after a debounced delay once changes are detected in HEAD, index, or refs/.
    /// </summary>
    public event Action? RepositoryChanged;

    public RepositoryWatcher(string repoPath, int debounceMs = 300)
    {
        _repoPath = repoPath;
        _debounceMs = debounceMs;

        // Locate the active git folder (.git directory or standard bare path)
        if (Directory.Exists(Path.Combine(repoPath, ".git")))
        {
            _gitPath = Path.Combine(repoPath, ".git");
        }
        else if (repoPath.EndsWith(".git", StringComparison.
OrdinalIgnoreCase) && Directory.Exists(repoPath))
        {
            _gitPath = repoPath;
        }
        else
        {
            _gitPath = string.Empty;
        }

        _debounceTimer = new Timer(OnTimerFired, null, Timeout.
Infinite, Timeout.Infinite);

        StartWatching();
    }

    private void StartWatching()
    {
        if (string.IsNullOrEmpty(_repoPath)) return;

        try
        {
            _watcher = new FileSystemWatcher(_repoPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.CreationTime
            };

            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;

            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Fail gracefully if directory accessibility issues occur
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        var relativePath = Path.GetRelativePath(_repoPath, e.FullPath);

        // Normalize path separators for comparison
        relativePath = relativePath.Replace('\\', '/');

        // A bare touch of the .git directory entry itself (mtime bump) is not
        // actionable and, without this guard, would fall through to the working
        // tree branch and fire a refresh.
        if (relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Determine if this change is inside the .git metadata folder
        if (_gitPath == _repoPath || relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
        {
            string gitRelative = _gitPath == _repoPath ? relativePath : relativePath.Substring(5);

            // Ignore lock-file churn (index.lock, refs/**/*.lock, HEAD.lock, ...).
            // These are written and deleted mid-operation and would otherwise
            // trigger a refresh in the middle of a commit/merge/rebase.
            if (gitRelative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Match changes affecting only critical references and state files
            bool isHead = gitRelative.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            bool isIndex = gitRelative.Equals("index", StringComparison.OrdinalIgnoreCase);
            bool isRefs = gitRelative.StartsWith("refs/", StringComparison.OrdinalIgnoreCase);

            if (isHead || isIndex || isRefs)
            {
                _debounceTimer.Change(_debounceMs, Timeout.Infinite);
            }
        }
        else
        {
            // Working tree change. Skip heavy build/dependency directories so a
            // build or dependency install doesn't hammer status re-reads.
            if (IsIgnoredWorkingTreePath(relativePath))
            {
                return;
            }

            _debounceTimer.Change(_debounceMs, Timeout.Infinite);
        }
    }

    private static bool IsIgnoredWorkingTreePath(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            foreach (var ignored in IgnoredDirSegments)
            {
                if (segment.Equals(ignored, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void OnTimerFired(object? state)
    {
        // Rate cap: if we refreshed very recently, defer this fire to the end of
        // the cap window instead of refreshing again immediately.
        var now = DateTime.UtcNow;
        var sinceLast = (now - _lastRefreshUtc).TotalMilliseconds;
        if (sinceLast < MaxRefreshMs)
        {
            _debounceTimer.Change(MaxRefreshMs - (int)sinceLast, Timeout.Infinite);
            return;
        }

        _lastRefreshUtc = now;
        RepositoryChanged?.Invoke();
    }

    public void ForceRefresh()
    {
        RepositoryChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileSystemEvent;
            _watcher.Created -= OnFileSystemEvent;
            _watcher.Deleted -= OnFileSystemEvent;
            _watcher.Renamed -= OnFileSystemEvent;
            _watcher.Dispose();
        }

        _debounceTimer.Dispose();
    }
}
