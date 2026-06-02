using System;
    using System.IO;
    using System.Threading;

    namespace GitLoom.Core.Services;

    public class RepositoryWatcher : IDisposable
    {
        private readonly string _repoPath;
        private readonly string _gitPath;
        private FileSystemWatcher? _watcher;
        private readonly Timer _debounceTimer;
        private readonly int _debounceMs;
        private bool _disposed;

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
            if (string.IsNullOrEmpty(_gitPath)) return;

            try
            {
                _watcher = new FileSystemWatcher(_gitPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.
  FileName | NotifyFilters.DirectoryName
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

        private void OnFileSystemEvent(object sender, FileSystemEventArgs
  e)
        {
            var relativePath = Path.GetRelativePath(_gitPath, e.FullPath);

            // Normalize path separators for comparison
            relativePath = relativePath.Replace('\\', '/');

            // Match changes affecting only critical references and state files
            bool isHead = relativePath.Equals("HEAD", StringComparison.
  OrdinalIgnoreCase);
            bool isIndex = relativePath.Equals("index", StringComparison.
  OrdinalIgnoreCase);
            bool isRefs = relativePath.StartsWith("refs/",
  StringComparison.OrdinalIgnoreCase);

            if (isHead || isIndex || isRefs)
            {
                // Reset the debounce timer to push execution back by debounceMs
                _debounceTimer.Change(_debounceMs, Timeout.Infinite);
            }
        }

        private void OnTimerFired(object? state)
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