using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VisionWorkbench.Algorithms;

namespace VisionWorkbench.App;

/// <summary>Application-lifetime cache for task-isolated initialized algorithm Worker sessions.</summary>
public sealed class AlgorithmSessionCache
{
    private readonly Func<string, CancellationToken, Task<IAlgorithmSession>> _createSession;
    private readonly ConcurrentDictionary<string, Lazy<Task<IAlgorithmSession>>> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _useGates = new(StringComparer.Ordinal);

    public AlgorithmSessionCache(AlgorithmManager manager)
        : this(async (pluginId, cancellationToken) => (IAlgorithmSession)await manager.CreateSessionAsync(pluginId, cancellationToken))
    {
    }

    public AlgorithmSessionCache(Func<string, CancellationToken, Task<IAlgorithmSession>> createSession)
        => _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));

    public sealed class SessionUseLease(IAlgorithmSession session, SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;
        public IAlgorithmSession Session { get; } = session;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    public async Task<SessionUseLease> AcquireUseAsync(
        IAlgorithmSession session, CancellationToken cancellationToken = default)
    {
        var gate = _useGates.GetOrAdd(session.SessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new SessionUseLease(session, gate);
    }

    public async Task<IAlgorithmSession> GetOrInitializeAsync(
        long taskId,
        string pluginId,
        AlgorithmInitialization initialization,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(taskId, pluginId, initialization);
        while (true)
        {
            var lazy = _sessions.GetOrAdd(key, _ => new Lazy<Task<IAlgorithmSession>>(
                () => CreateAsync(pluginId, initialization), LazyThreadSafetyMode.ExecutionAndPublication));
            IAlgorithmSession session;
            try
            {
                session = await lazy.Value.WaitAsync(cancellationToken);
            }
            catch
            {
                if (lazy.IsValueCreated && (lazy.Value.IsFaulted || lazy.Value.IsCanceled))
                    _sessions.TryRemove(new KeyValuePair<string, Lazy<Task<IAlgorithmSession>>>(key, lazy));
                throw;
            }

            if (session.State is AlgorithmSessionState.Ready or AlgorithmSessionState.Running or AlgorithmSessionState.Stopping)
                return session;

            if (_sessions.TryRemove(new KeyValuePair<string, Lazy<Task<IAlgorithmSession>>>(key, lazy)))
                await session.DisposeAsync();
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
}
