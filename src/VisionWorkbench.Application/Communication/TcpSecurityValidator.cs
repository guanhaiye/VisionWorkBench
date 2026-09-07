using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisionWorkbench.Application.Communication;

/// <summary>工业 TCP JSON 请求的时间戳、客户端身份、签名和 nonce 重放保护。</summary>
public sealed class TcpSecurityValidator
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);
    private readonly TimeSpan _clockSkew;

    public TcpSecurityValidator(TimeSpan? clockSkew = null) => _clockSkew = clockSkew ?? TimeSpan.FromMinutes(2);

    public bool Validate(JsonElement root, string sharedSecret, DateTimeOffset? now, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(sharedSecret)) { error = "shared_secret_required"; return false; }
        var clientId = ReadString(root, "clientId");
        var nonce = ReadString(root, "nonce");
        var signature = ReadString(root, "signature");
        var requestId = ReadString(root, "requestId");
        var command = ReadString(root, "command");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(command))
        { error = "authentication_fields_required"; return false; }
        if (!TryReadTimestamp(root, out var timestamp)) { error = "timestamp_required"; return false; }
        var current = now ?? DateTimeOffset.UtcNow;
        if ((current - timestamp).Duration() > _clockSkew) { error = "timestamp_expired"; return false; }
        Purge(current);
        var nonceKey = clientId + ":" + nonce;
        if (!_nonces.TryAdd(nonceKey, current)) { error = "nonce_replay"; return false; }
        var expected = CreateSignature(sharedSecret, clientId, timestamp, nonce, requestId, command);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature)))
        { _nonces.TryRemove(nonceKey, out _); error = "invalid_signature"; return false; }
        return true;
    }

    public static string CreateSignature(string sharedSecret, string clientId, DateTimeOffset timestamp, string nonce, string requestId, string command)
    {
        var canonical = string.Join("|", clientId, timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), nonce, requestId, command);
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret), Encoding.UTF8.GetBytes(canonical)));
    }

    private void Purge(DateTimeOffset now)
    {
        foreach (var pair in _nonces)
            if ((now - pair.Value).Duration() > _clockSkew) _nonces.TryRemove(pair.Key, out _);
    }

    private static string? ReadString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty("timestamp", out var item)) return false;
        if (item.ValueKind == JsonValueKind.String) return DateTimeOffset.TryParse(item.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var unix)) { value = DateTimeOffset.FromUnixTimeSeconds(unix); return true; }
        return false;
    }
}
