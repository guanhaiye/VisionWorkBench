namespace VisionWorkbench.Application;

public sealed record SessionSnapshot(
    bool IsAuthenticated,
    string? UserName,
    string? Role,
    bool IsLocked,
    DateTime? LastActivityUtc);

/// <summary>Central session policy. UI state is not an authorization boundary; services can query this object.</summary>
public sealed class SessionService(TimeSpan? inactivityTimeout = null)
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout = inactivityTimeout ?? TimeSpan.FromMinutes(30);
    private string? _userName;
    private string? _role;
    private bool _isAdmin;
    private DateTime _lastActivityUtc;
    private bool _locked;

    public SessionSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                EvaluateTimeout();
                return new SessionSnapshot(_userName is not null && !_locked, _userName, _role, _locked, _lastActivityUtc);
            }
        }
    }

    public void Begin(string userName, string role, bool isAdmin = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        lock (_gate)
        {
            _userName = userName.Trim();
            _role = string.IsNullOrWhiteSpace(role) ? "operator" : role.Trim();
            _isAdmin = isAdmin;
            _lastActivityUtc = DateTime.UtcNow;
            _locked = false;
        }
    }

    public bool Touch()
    {
        lock (_gate)
        {
            EvaluateTimeout();
            if (_userName is null || _locked) return false;
            _lastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    public bool Unlock() { lock (_gate) { if (_userName is null) return false; _locked = false; _lastActivityUtc = DateTime.UtcNow; return true; } }

    public void End()
    {
        lock (_gate)
        {
            _userName = null;
            _role = null;
            _isAdmin = false;
            _locked = false;
            _lastActivityUtc = default;
        }
    }

    public bool HasPermission(Func<string, bool> rolePermission)
    {
        ArgumentNullException.ThrowIfNull(rolePermission);
        lock (_gate)
        {
            EvaluateTimeout();
            return _userName is not null && !_locked && (_isAdmin || (_role is not null && rolePermission(_role)));
        }
    }

    private void EvaluateTimeout()
    {
        if (_userName is not null && !_locked && DateTime.UtcNow - _lastActivityUtc >= _timeout)
            _locked = true;
    }
}
