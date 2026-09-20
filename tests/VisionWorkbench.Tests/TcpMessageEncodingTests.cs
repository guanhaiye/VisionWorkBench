using System.Text;
using System.Text.Json;
using VisionWorkbench.Application.Communication;
using Xunit;

namespace VisionWorkbench.Tests;

public class TcpMessageEncodingTests
{
    [Fact]
    public void Gb18030_Encoding_IsRegistered_And_RoundTrips_ChineseJson()
    {
        var config = new ProjectCommunicationConfig { Encoding = "gb18030" };
        var encoding = TcpMessageCodec.GetEncoding(config);
        Assert.Equal(54936, encoding.CodePage);

        const string taskName = "语义分割";
        var json = JsonSerializer.Serialize(
            new { ok = true, code = "accepted", task = taskName },
            TcpMessageCodec.TextJsonOptions);
        Assert.DoesNotContain(@"\u", json);
        Assert.Contains(taskName, json);

        var bytes = TcpMessageCodec.Encode(json, config);
        var decoded = encoding.GetString(bytes).TrimEnd('\r', '\n');
        Assert.Equal(json, decoded);
        Assert.Contains(taskName, decoded);
    }

    [Fact]
    public void Gbk_Alias_Maps_To_Gb18030()
    {
        var encoding = TcpMessageCodec.GetEncoding(new ProjectCommunicationConfig { Encoding = "gbk" });
        Assert.Equal(54936, encoding.CodePage);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("gb18030")]
    public void Encode_Decode_ChineseJson_RoundTrips(string encodingName)
    {
        var config = new ProjectCommunicationConfig { Encoding = encodingName };
        var json = JsonSerializer.Serialize(
            new { ok = true, code = "completed", task = "目标检测", count = 1 },
            TcpMessageCodec.TextJsonOptions);
        var bytes = TcpMessageCodec.Encode(json, config);
        var decoded = TcpMessageCodec.GetEncoding(config).GetString(bytes).TrimEnd('\r', '\n');
        Assert.Equal(json, decoded);
        Assert.Contains("目标检测", decoded);
    }
}
