using System.Collections.Generic;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Bounded, thread-safe LRU cache of computed blame (T-11). Keyed by
/// <c>(repoPath, path, revisionSha)</c> — because the revision is pinned to a concrete
/// commit SHA, a new commit (HEAD moving) produces a different key and misses naturally,
/// so cached blame is never stale. <see cref="InvalidateRepo"/> gives the watcher an
/// eager path to drop a repo's entries on <c>RepositoryChanged</c>. Never unbounded
/// (an unbounded blame cache is a T-11 rejection trigger): once <see cref="Capacity"/>
/// entries are held, inserting evicts the least-recently-used one.
/// </summary>
internal sealed class BlameCache
{
    internal readonly record struct Key(string RepoPath, string Path, string RevisionSha);

    private readonly int _capacity;
    private readonly object _gate = new();

    // A dictionary for O(1) lookup paired with a recency list whose head is the
    // most-recently-used key. Small capacity (~32) keeps list moves trivial.
    private readonly Dictionary<Key, IReadOnlyList<BlameLine>> _entries = new();
    private readonly LinkedList<Key> _recency = new();
    private readonly Dictionary<Key, LinkedListNode<Key>> _nodes = new();

    public BlameCache(int capacity = 32)
    {
        _capacity = capacity < 1 ? 1 : capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public bool TryGet(Key key, out IReadOnlyList<BlameLine> value)
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
        value = System.Array.Empty<BlameLine>();
        return false;
    }

    public void Set(Key key, IReadOnlyList<BlameLine> value)
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

    /// <summary>Drops every entry belonging to <paramref name="repoPath"/> (watcher invalidation).</summary>
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
                if (_nodes.TryGetValue(key, out var node))
                {
                    _recency.Remove(node);
                    _nodes.Remove(key);
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
            _nodes.Clear();
        }
    }

    // Move an existing key to the most-recently-used position. Caller holds _gate.
    private void Touch(Key key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            _recency.Remove(node);
            _recency.AddFirst(node);
        }
    }
}
