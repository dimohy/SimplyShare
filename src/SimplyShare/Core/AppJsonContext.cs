using System.Text.Json.Serialization;
using SimplyShare.Models;

namespace SimplyShare.Core;

/// <summary>
/// Native AOT 호환 JSON 직렬화 컨텍스트 (Source Generator 기반)
/// </summary>
[JsonSerializable(typeof(DiscoveryMessage))]
[JsonSerializable(typeof(TransferMessage))]
[JsonSerializable(typeof(TransferRequest))]
[JsonSerializable(typeof(FileTransferInfo))]
[JsonSerializable(typeof(InputEvent))]
[JsonSerializable(typeof(ChatConfig))]
[JsonSerializable(typeof(SharePreferences))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(DeviceInfo))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class AppJsonContext : JsonSerializerContext;
