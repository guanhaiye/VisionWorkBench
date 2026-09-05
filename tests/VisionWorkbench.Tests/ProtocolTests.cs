using System.Text.Json;
using VisionWorkbench.Contracts.Protocol;
using VisionWorkbench.Contracts.Plugins;
using VisionWorkbench.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace VisionWorkbench.Tests;

/// <summary>协议与清单：序列化往返、未知字段容忍（PLG-014）、清单校验（PLG-002/003）。</summary>
public sealed class ProtocolTests
{
    [Fact]
    public void Envelope_Roundtrip_Preserves_Case_Conventions()
    {
        var msg = ProtocolMessage.Create(MessageType.Submit, "m1",
            new { inputId = "i1", imagePath = "x.png" }, correlationId: "c1");
        var json = ProtocolMessage.Serialize(msg);
        Assert.Contains("\"messageId\"", json);
        Assert.Contains("\"correlationId\"", json);
        Assert.Contains("\"protocolVersion\"", json);

        var parsed = ProtocolMessage.Parse(json);
        Assert.NotNull(parsed);
        Assert.Equal(MessageType.Submit, parsed!.Type);
        Assert.Equal("m1", parsed.MessageId);
        Assert.Equal("c1", parsed.CorrelationId);
    }

    [Fact]
    public void Parse_Tolerates_Unknown_Fields()
    {
        // PLG-014：新增字段不得导致旧宿主崩溃
        var json = """
            {"type":"hello","messageId":"m1","payload":{"pluginId":"p","workerVersion":"1","protocolVersion":"1.0","futureField":{"nested":true}},"extraTop":123}
            """;
        var msg = ProtocolMessage.Parse(json);
        Assert.NotNull(msg);
        var hello = ProtocolMessage.DeserializePayload<HelloPayload>(msg!.Payload);
        Assert.NotNull(hello);
        Assert.Equal("p", hello!.PluginId);
    }

    [Fact]
    public void DeserializePayload_Truncates_Garbage_To_Null()
    {
        Assert.Throws<JsonException>(() =>
            ProtocolMessage.DeserializePayload<HelloPayload>(JsonDocument.Parse("\"just a string\"").RootElement));
    }

    [Fact]
    public void Manifest_Validate_Passes_For_SampleCounter()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.SampleCounterPlugin, "plugin.json"));
        var manifest = PluginManifest.TryParse(json);
        Assert.NotNull(manifest);
        Assert.Empty(manifest!.Validate());
    }

    [Fact]
    public void Manifest_Missing_Entry_Is_Invalid()
    {
        // Runtime 默认 Type=python 但 Entry 为空 → 报 entry 缺失
        var manifest = new PluginManifest { Id = "x", Name = "x", Version = "1", ProtocolVersion = "1.0" };
        var errors = manifest.Validate();
        Assert.Contains(errors, e => e.Contains("runtime.entry"));
        Assert.Contains(errors, e => e.Contains("manifestVersion"));
    }

    [Fact]
    public void Manifest_Incompatible_Protocol_Is_Reported()
    {
        var manifest = new PluginManifest
        {
            ManifestVersion = "1.0",
            Id = "x",
            Name = "x",
            Version = "1",
            ProtocolVersion = "9.9",
            Runtime = new PluginRuntime { Type = "python", Entry = "worker.py" },
        };
        Assert.Contains(manifest.Validate(), e => e.Contains("不兼容"));
    }

    [Fact]
    public void Manifest_Runtime_Path_Traversal_Is_Rejected()
    {
        var manifest = new PluginManifest
        {
            ManifestVersion = "1.0",
            Id = "x",
            Name = "x",
            Version = "1",
            ProtocolVersion = "1.0",
            Runtime = new PluginRuntime { Type = "python", Entry = "../worker.py" },
        };

        Assert.Contains(manifest.Validate(), e => e.Contains("SEC-001"));
    }

    [Fact]
    public void Manifest_Invalid_File_Hash_Is_Rejected()
    {
        var manifest = new PluginManifest
        {
            ManifestVersion = "1.0",
            Id = "x",
            Name = "x",
            Version = "1",
            ProtocolVersion = "1.0",
            Runtime = new PluginRuntime { Type = "python", Entry = "worker.py" },
            FileHashes = new Dictionary<string, string> { ["worker.py"] = "not-a-sha256" },
        };

        Assert.Contains(manifest.Validate(), e => e.Contains("SEC-002"));
    }

    [Fact]
    public void Scanner_Reports_Missing_Manifest_Directory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "broken-plugin"));
        try
        {
            var plugins = new PluginScanner(NullLogger.Instance).Scan(root);
            var broken = Assert.Single(plugins);
            Assert.Equal(PluginStatus.InvalidManifest, broken.Status);
            Assert.Contains(broken.Errors, e => e.Contains("PLG-002"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AlgorithmOutput_GetCount_Falls_Back_To_Detections()
    {
        var output = new Contracts.Results.AlgorithmOutput
        {
            OutputId = "o1",
            InputId = "i1",
            Detections =
            [
                new() { ClassId = "a", Box = new() { Width = 0.1, Height = 0.1 }, Confidence = 0.9 },
                new() { ClassId = "a", Box = new() { Width = 0.1, Height = 0.1 }, Confidence = 0.8 },
                new() { ClassId = "b", Box = new() { Width = 0.1, Height = 0.1 }, Confidence = 0.7 },
            ],
        };
        Assert.Equal(3, output.GetCount());
        Assert.Equal(2, output.GetCount("a"));
        Assert.Equal(1, output.GetCount("b"));
    }

    [Fact]
    public void AlgorithmOutput_Deserializes_SegmentationContourPoints()
    {
        var json = """
            {
              "outputId": "o1",
              "inputId": "i1",
              "segmentations": [
                {
                  "mode": "instance",
                  "classId": "1",
                  "contours": [[{"x":0.1,"y":0.2},{"x":0.3,"y":0.2},{"x":0.3,"y":0.4}]],
                  "areaRatio": 0.02
                }
              ]
            }
            """;

        var output = JsonSerializer.Deserialize<Contracts.Results.AlgorithmOutput>(
            json, ProtocolMessage.JsonOptions);

        Assert.NotNull(output);
        var contour = Assert.Single(Assert.Single(output!.Segmentations).Contours);
        var point = contour[0];
        Assert.Equal(0.1, point.X);
        Assert.Equal(0.2, point.Y);
    }
}
