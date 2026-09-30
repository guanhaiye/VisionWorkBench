using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VisionWorkbench.Algorithms;

namespace VisionWorkbench.App;

/// <summary>Application-lifetime pool of task-isolated initialized algorithm Worker sessions.</summary>
public sealed class AlgorithmSessionCache : IAsyncDisposable
{
    public const int MaxSessionPoolSize = 8;

    private sealed class SessionSlot
    {
        private readonly object _sync = new();
        private Task<IAlgorithmSession>? _sessionTask;

        public SemaphoreSlim UseGate { get; } = new(1, 1);

        public Task<IAlgorithmSession> GetOrCreateAsync(Func<Task<IAlgorithmSession>> create)
        {
            lock (_sync) return _sessionTask ??= create();
        }

        public bool Reset(Task<IAlgorithmSession> expected)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_sessionTask, expected)) return false;
                _sessionTask = null;
                return true;
            }
        }

        public Task<IAlgorithmSession>? CurrentTask
        {
            get { lock (_sync) return _sessionTask; }
        }
    }

    private sealed class SessionPool
    {
        private readonly object _sync = new();
        private readonly List<SessionSlot> _slots = [new()];
        private readonly SemaphoreSlim _slotReleased = new(0, MaxSessionPoolSize);

        public SessionSlot GetSlot(int index)
        {
            lock (_sync)
            {
                while (_slots.Count <= index)
                    _slots.Add(new SessionSlot());
                return _slots[index];
            }
        }

        public async Task<SessionSlot> AcquireSlotAsync(int capacity, CancellationToken cancellationToken)
        {
            capacity = Math.Clamp(capacity, 1, MaxSessionPoolSize);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var index = 0; index < capacity; index++)
                {
                    var slot = GetSlot(index);
                    if (slot.UseGate.Wait(0)) return slot;
                }
                await _slotReleased.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseSlot(SessionSlot slot)
        {
            slot.UseGate.Release();
            try { _slotReleased.Release(); }
            catch (SemaphoreFullException) { }
        }

        public SessionSlot[] GetSlots()
        {
            lock (_sync) return _slots.ToArray();
        }
    }

    private sealed record SessionSlotOwner(SessionPool Pool, SessionSlot Slot);

    public sealed class SessionUseLease : IAsyncDisposable
    {
        private readonly Func<ValueTask> _release;
        private int _released;

        internal SessionUseLease(IAlgorithmSession session, Func<ValueTask> release)
        {
            Session = session;
            _release = release;
        }

        public IAlgorithmSession Session { get; }

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0
            ? _release()
            : ValueTask.CompletedTask;
    }

    private readonly Func<string, CancellationToken, Task<IAlgorithmSession>> _createSession;
    private readonly ConcurrentDictionary<string, SessionPool> _pools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionSlotOwner> _sessionOwners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _unmanagedUseGates = new(StringComparer.Ordinal);
    private int _disposed;

    public AlgorithmSessionCache(AlgorithmManager manager)
        : this(async (pluginId, cancellationToken) => (IAlgorithmSession)await manager.CreateSessionAsync(pluginId, cancellationToken))
    {
    }

    public AlgorithmSessionCache(Func<string, CancellationToken, Task<IAlgorithmSession>> createSession)
        => _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));

    /// <summary>Acquire an initialized model slot from a bounded per-task pool.</summary>
    public async Task<SessionUseLease> AcquireSessionUseAsync(
        long taskId,
        string pluginId,
        AlgorithmInitialization initialization,
        int maxConcurrentSessions,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var pool = GetPool(taskId, pluginId, initialization);
        var capacity = Math.Clamp(maxConcurrentSessions, 1, MaxSessionPoolSize);
        while (true)
        {
            var slot = await pool.AcquireSlotAsync(capacity, cancellationToken);
            Task<IAlgorithmSession>? sessionTask = null;
            try
            {
                sessionTask = slot.GetOrCreateAsync(() => CreateAsync(pluginId, initialization));
                var session = await sessionTask.WaitAsync(cancellationToken);
                if (session.State is AlgorithmSessionState.Faulted or AlgorithmSessionState.Stopped)
                {
                    await ResetSlotAsync(sessionTask, session);
                    pool.ReleaseSlot(slot);
                    continue;
                }

                RegisterOwner(session, pool, slot);
                return new SessionUseLease(session, async () =>
                {
                    try
                    {
                        if (session.State == AlgorithmSessionState.Running)
                            await session.StopAsync(CancellationToken.None);
                        if (session.State is AlgorithmSessionState.Faulted or AlgorithmSessionState.Stopped)
                            await ResetSlotAsync(sessionTask, session);
                    }
                    finally
                    {
                        pool.ReleaseSlot(slot);
                    }
                });
            }
            catch
            {
                if (sessionTask is { IsFaulted: true } || sessionTask is { IsCanceled: true })
                    slot.Reset(sessionTask);
                pool.ReleaseSlot(slot);
                throw;
            }
        }
    }

    public async Task<SessionUseLease> AcquireUseAsync(
        IAlgorithmSession session, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_sessionOwners.TryGetValue(session.SessionId, out var owner))
        {
            var gate = _unmanagedUseGates.GetOrAdd(session.SessionId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            return new SessionUseLease(session, () =>
            {
                gate.Release();
                return ValueTask.CompletedTask;
            });
        }

        await owner.Slot.UseGate.WaitAsync(cancellationToken);
        return new SessionUseLease(session, async () =>
        {
            try
            {
                if (session.State == AlgorithmSessionState.Running)
                    await session.StopAsync(CancellationToken.None);
                if (session.State is AlgorithmSessionState.Faulted or AlgorithmSessionState.Stopped)
                {
                    var current = owner.Slot.CurrentTask;
                    if (current is { IsCompletedSuccessfully: true } && ReferenceEquals(current.Result, session))
                        await ResetSlotAsync(current, session);
                }
            }
            finally
            {
                owner.Pool.ReleaseSlot(owner.Slot);
            }
        });
    }

    public async Task<IAlgorithmSession> GetOrInitializeAsync(
        long taskId,
        string pluginId,
        AlgorithmInitialization initialization,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var pool = GetPool(taskId, pluginId, initialization);
        var slot = pool.GetSlot(0);
        var key = BuildKey(taskId, pluginId, initialization);
        while (true)
        {
            var sessionTask = slot.GetOrCreateAsync(() => CreateAsync(pluginId, initialization));
            IAlgorithmSession session;
            try
            {
                session = await sessionTask.WaitAsync(cancellationToken);
            }
            catch
            {
                if (sessionTask.IsFaulted || sessionTask.IsCanceled)
                    slot.Reset(sessionTask);
                throw;
            }

            if (session.State is AlgorithmSessionState.Ready or AlgorithmSessionState.Running)
            {
                RegisterOwner(session, pool, slot);
                return session;
            }

            await ResetSlotAsync(sessionTask, session);
        }
    }

    private SessionPool GetPool(long taskId, string pluginId, AlgorithmInitialization initialization)
    {
        var key = BuildKey(taskId, pluginId, initialization);
        return _pools.GetOrAdd(key, _ => new SessionPool());
    }

    private void RegisterOwner(IAlgorithmSession session, SessionPool pool, SessionSlot slot)
        => _sessionOwners[session.SessionId] = new SessionSlotOwner(pool, slot);

    private async Task ResetSlotAsync(Task<IAlgorithmSession> sessionTask, IAlgorithmSession session)
    {
        foreach (var owner in _sessionOwners)
        {
            if (string.Equals(owner.Key, session.SessionId, StringComparison.Ordinal))
                _sessionOwners.TryRemove(owner);
        }
        var reset = false;
        foreach (var pool in _pools.Values)
        {
            foreach (var slot in pool.GetSlots())
            {
                if (slot.Reset(sessionTask)) reset = true;
            }
        }
        if (reset)
        {
            try { await session.DisposeAsync(); }
            catch { /* A faulted worker is best-effort disposed before its slot is rebuilt. */ }
        }
    }

    private async Task<IAlgorithmSession> CreateAsync(
        string pluginId, AlgorithmInitialization initialization)
    {
        var session = await _createSession(pluginId, CancellationToken.None);
        try
        {
            await session.InitializeAsync(initialization, CancellationToken.None);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static string BuildKey(long taskId, string pluginId, AlgorithmInitialization initialization)
    {
        var payload = string.Join("\n",
            taskId.ToString(CultureInfo.InvariantCulture),
            pluginId.Trim(),
            initialization.ExecutionProvider.Trim().ToLowerInvariant(),
            initialization.Settings?.GetRawText() ?? "null");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var pool in _pools.Values)
        {
            foreach (var slot in pool.GetSlots())
            {
                await slot.UseGate.WaitAsync();
                try
                {
                    var task = slot.CurrentTask;
                    if (task is { IsCompletedSuccessfully: true })
                    {
                        try { await task.Result.DisposeAsync(); }
                        catch { /* Continue releasing other model slots on application exit. */ }
                    }
                }
                finally
                {
                    slot.UseGate.Release();
                    slot.UseGate.Dispose();
                }
            }
        }
        foreach (var gate in _unmanagedUseGates.Values) gate.Dispose();
        _sessionOwners.Clear();
        _pools.Clear();
    }
}