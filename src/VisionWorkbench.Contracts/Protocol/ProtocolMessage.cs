using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionWorkbench.Contracts.Protocol;

/// <summary>宿主与算法 Worker 支持的协议版本（文档 §13）。</summary>
public static class ProtocolVersions
{
    public const string V1 = "1.0";

    /// <summary>宿主支持的协议版本列表，按优先级排序。</summary>
    public static readonly IReadOnlyList<string> Supported = [V1];

    public static bool IsSupported(string version) => Supported.Contains(version);
}

/// <summary>协议消息类型（文档 §13.3）。</summary>
public static class MessageType
{
    public const string Hello = "hello";
    public const string Initialize = "initialize";
    public const string Ready = "ready";
    public const string StartSession = "start_session";
    public const string Submit = "submit";
    public const string Result = "result";
    public const string Event = "event";
    public const string UpdateSettings = "update_settings";
    public const string CounterCommand = "counter_command";
    public const string Health = "health";
    public const string Cancel = "cancel";
    public const string Flush = "flush";
    public const string StopSession = "stop_session";
    public const string Shutdown = "shutdown";
    public const string Error = "error";
}

/// <summary>
/// JSON Lines 协议信封（文档 §13.2）。一行一条完整 JSON 消息。
/// 发送方用强类型负载构造 <see cref="ProtocolEnvelope{T}"/>；接收方反序列化为
/// <see cref="ProtocolEnvelope{JsonElement}"/> 后按需解析负载。
/// </summary>
public sealed record ProtocolEnvelope<T>
{
    public string ProtocolVersion { get; init; } = ProtocolVersions.V1;
    public string Type { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string? CorrelationId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public T? Payload { get; init; }
}

public static class ProtocolMessage
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(ProtocolEnvelope<T> message) =>
        JsonSerializer.Serialize(message, JsonOptions);

    /// <summary>解析一行协议消息；返回 null 表示不是合法 JSON（PLG-007）。</summary>
    public static ProtocolEnvelope<JsonElement>? Parse(string line)
    {
        try
        {
            var env = JsonSerializer.Deserialize<ProtocolEnvelope<JsonElement>>(line, JsonOptions);
            return string.IsNullOrEmpty(env?.Type) ? null : env;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static T? DeserializePayload<T>(JsonElement? payload)
    {
        if (payload is not { } element || element.ValueKind == JsonValueKind.Undefined)
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
    }

    public static ProtocolEnvelope<T> Create<T>(
        string type, string messageId, T payload, string? correlationId = null) => new()
    {
        Type = type,
        MessageId = messageId,
        CorrelationId = correlationId,
        Payload = payload,
    };
}
