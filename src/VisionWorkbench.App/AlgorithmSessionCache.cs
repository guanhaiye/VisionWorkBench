using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VisionWorkbench.Algorithms;

namespace VisionWorkbench.App;

/// <summary>
/// Holds initialized algorithm sessions for the lifetime of the application. Both the
/// live page and the TCP fallback use this cache so switching execution paths does not
/// start another Worker or load the same model again.
/// </summary>
public sealed class AlgorithmSessionCache(AlgorithmManager manager)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IAlgorithmSession>>> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _useGates = new(StringComparer.Ordinal);

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
        string pluginId,
        AlgorithmInitialization initialization,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(pluginId, initialization);
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
        var session = await manager.CreateSessionAsync(pluginId, CancellationToken.None);
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

    private static string BuildKey(string pluginId, AlgorithmInitialization initialization)
    {
        var payload = string.Join("\n",
            pluginId.Trim(),
            initialization.ExecutionProvider.Trim().ToLowerInvariant(),
            initialization.Settings?.GetRawText() ?? "null");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
