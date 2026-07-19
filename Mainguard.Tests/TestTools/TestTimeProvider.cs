using System;
using System.Collections.Generic;
using System.Threading;

namespace Mainguard.Tests.TestTools;

/// <summary>
/// A minimal manual-advance <see cref="TimeProvider"/> for deterministic virtual-clock tests (e.g. the
/// P2-22 loopback 5-minute timeout). Supports <see cref="CreateTimer"/> so it drives
/// <c>new CancellationTokenSource(delay, timeProvider)</c>: advancing past a timer's due time fires it.
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TestTimer> _timers = new();
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset? start = null) => _now = start ?? DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by)
    {
        TestTimer[] snapshot;
        lock (_gate)
        {
            _now += by;
            snapshot = _timers.ToArray();
        }
        foreach (var t in snapshot)
            t.MaybeFire(_now);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state, _now + dueTime);
        lock (_gate)
            _timers.Add(timer);
        return timer;
    }

    private void Remove(TestTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    private sealed class TestTimer : ITimer
    {
        private readonly TestTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;
        private bool _fired;

        public TestTimer(TestTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset dueAt)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _dueAt = dueAt;
        }

        public void MaybeFire(DateTimeOffset now)
        {
            if (_fired || _dueAt is null || now < _dueAt) return;
            _fired = true;
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
            _fired = false;
            return true;
        }

        public void Dispose() => _owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
