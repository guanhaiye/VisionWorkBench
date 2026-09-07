using System.Text.Json;
using VisionWorkbench.Application.Communication;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class TcpSecurityTests
{
    [Fact]
    public void Signature_IsAcceptedOnce_AndNonceReplayIsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var signature = TcpSecurityValidator.CreateSignature("secret", "client-a", now, "n-1", "r-1", "execute");
        using var doc = JsonDocument.Parse($"{{\"clientId\":\"client-a\",\"nonce\":\"n-1\",\"requestId\":\"r-1\",\"command\":\"execute\",\"timestamp\":\"{now:O}\",\"signature\":\"{signature}\"}}");
        var validator = new TcpSecurityValidator(TimeSpan.FromMinutes(2));
        Assert.True(validator.Validate(doc.RootElement, "secret", now, out _));
        Assert.False(validator.Validate(doc.RootElement, "secret", now, out var error));
        Assert.Equal("nonce_replay", error);
    }

    [Fact]
    public void ExpiredTimestamp_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.AddMinutes(-3);
        var signature = TcpSecurityValidator.CreateSignature("secret", "client-a", timestamp, "n-2", "r-2", "execute");
        using var doc = JsonDocument.Parse($"{{\"clientId\":\"client-a\",\"nonce\":\"n-2\",\"requestId\":\"r-2\",\"command\":\"execute\",\"timestamp\":\"{timestamp:O}\",\"signature\":\"{signature}\"}}");
        Assert.False(new TcpSecurityValidator(TimeSpan.FromMinutes(2)).Validate(doc.RootElement, "secret", now, out var error));
        Assert.Equal("timestamp_expired", error);
    }
}
