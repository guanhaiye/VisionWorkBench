using System.Text;
using System.Text.Json;
using VisionWorkbench.Application.Communication;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class TcpCommunicationTests
{
    [Fact]
    public void LineDecoder_HandlesPartialAndMultipleFrames()
    {
        var decoder = new TcpFrameDecoder(new ProjectCommunicationConfig { FrameMode = TcpFrameMode.Line });
        var first = decoder.Append(Encoding.UTF8.GetBytes("one\npar"));
        Assert.Equal(["one"], first.Select(Encoding.UTF8.GetString));
        var frames = decoder.Append(Encoding.UTF8.GetBytes("t\ntwo\r\n"));
        Assert.Equal(["part", "two"], frames.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public void LengthPrefixDecoder_HandlesLittleEndianAndPartialBody()
    {
        var config = new ProjectCommunicationConfig { FrameMode = TcpFrameMode.LengthPrefix, LengthPrefixBytes = 2, LengthPrefixBigEndian = false };
        var decoder = new TcpFrameDecoder(config);
        var packet = TcpMessageCodec.Encode("hello", config);
        Assert.Empty(decoder.Append(packet.AsSpan(0, 2)));
        var frames = decoder.Append(packet.AsSpan(2));
        Assert.Equal("hello", Encoding.UTF8.GetString(frames.Single()));
    }

    [Fact]
    public void Codec_UsesConfiguredTerminator()
    {
        var config = new ProjectCommunicationConfig { FrameMode = TcpFrameMode.Delimiter, MessageTerminator = "HEX:0D0A" };
        Assert.Equal("ping\r\n", Encoding.UTF8.GetString(TcpMessageCodec.Encode("ping", config)));
    }

    [Fact]
    public void LineDecoder_CanFlushBareCommand()
    {
        var decoder = new TcpFrameDecoder(new ProjectCommunicationConfig { FrameMode = TcpFrameMode.Line });
        Assert.Empty(decoder.Append(Encoding.UTF8.GetBytes("START_ST-001")));
        Assert.True(decoder.HasPendingData);
        Assert.Equal("START_ST-001", Encoding.UTF8.GetString(decoder.FlushPending().Single()));
        Assert.False(decoder.HasPendingData);
    }

    [Fact]
    public void ProfileStore_ClampsLegacyReconnectMaximumToTenSeconds()
    {
        var root = Path.Combine(Path.GetTempPath(), "vw-tcp-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "tcp-communication.json");
            var legacy = new Dictionary<string, ProjectCommunicationConfig>
            {
                ["tcp-001"] = new()
                {
                    ProjectCode = "tcp-001",
                    ReconnectIntervalMs = 3000,
                    MaxReconnectIntervalMs = 30000,
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(legacy));

            var loaded = new TcpCommunicationProfileStore(root).Load().Single();

            Assert.Equal(3000, loaded.ReconnectIntervalMs);
            Assert.Equal(10000, loaded.MaxReconnectIntervalMs);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
