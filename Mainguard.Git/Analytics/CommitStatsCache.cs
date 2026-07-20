using System.Collections.Generic;

namespace Mainguard.Git.Analytics;

/// <summary>
/// Bounded, thread-safe LRU cache of the T-22 history walk (Hotspot Register H1). Keyed by
/// <c>(repoPath, headSha, maxCommits)</c> — because the key pins the exact HEAD commit, a new
/// commit (HEAD moving) produces a different key and misses naturally, so a cached walk is never
/// stale; superseded entries simply age out of the LRU. This is the same never-unbounded shape as
/// <c>BlameCache</c> (an unbounded cache is a rejection trigger): once <see cref="Capacity"/>
/// entries are held, inserting evicts the least-recently-used one. Capacity stays small because
/// each entry is a full capped history walk (~10k <see cref="CommitStat"/>s ≈ 1 MB).
/// </summary>
public sealed class CommitStatsCache
{
    internal readonly record struct Key(string RepoPath, string HeadSha, int MaxCommits);

    private readonly int _capacity;
    private readonly object _gate = new();

    // O(1) lookup dictionary paired with a recency list whose head is the most-recently-used key.
    private readonly Dictionary<Key, IReadOnlyList<CommitStat>> _entries = new();
    private readonly LinkedList<Key> _recency = new();
    private readonly Dictionary<Key, LinkedListNode<Key>> _nodes = new();

    public CommitStatsCache(int capacity = 8)
    {
        _capacity = capacity < 1 ? 1 : capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    internal bool TryGet(Key key, out IReadOnlyList<CommitStat> value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var found))
            {
                Touch(key);
                value = found;
                return true;
            }
        }
        value = System.Array.Empty<CommitStat>();
        return false;
    }

    internal void Set(Key key, IReadOnlyList<CommitStat> value)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(key))
            {
                _entries[key] = value;
                Touch(key);
                return;
            }

            _entries[key] = value;
            _nodes[key] = _recency.AddFirst(key);

            while (_entries.Count > _capacity)
            {
                var lru = _recency.Last;
                if (lru == null) break;
                _recency.RemoveLast();
                _entries.Remove(lru.Value);
                _nodes.Remove(lru.Value);
            }
        }
    }

    /// <summary>Drops every entry belonging to <paramref name="repoPath"/> (eager invalidation seam).</summary>
    public void InvalidateRepo(string repoPath)
    {
        lock (_gate)
        {
            var stale = new List<Key>();
            foreach (var key in _entries.Keys)
            {
                if (key.RepoPath == repoPath) stale.Add(key);
            }

            foreach (var key in stale)
            {
                _entries.Remove(key);
                if (_nodes.Remove(key, out var node)) _recency.Remove(node);
            }
        }
    }

    private void Touch(Key key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            _recency.Remove(node);
            _nodes[key] = _recency.AddFirst(key);
        }
    }
}
