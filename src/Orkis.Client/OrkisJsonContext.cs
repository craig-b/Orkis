using System.Text.Json;
using System.Text.Json.Serialization;
using Orkis.Artifacts;
using Orkis.Runs;

namespace Orkis.Client;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for every type the client puts
/// on or takes off the wire. Statically generated metadata is what keeps the client
/// trimmable: reflection-based serialization warns (IL2026) and can strip the very DTO
/// types it needs, whereas each <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/>
/// here is preserved by construction. The polymorphic <see cref="RunEvent"/> hierarchy
/// comes along via its <c>[JsonDerivedType]</c> attributes.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RunEvent))]
[JsonSerializable(typeof(StartRunRequest))]
[JsonSerializable(typeof(RunAcceptedResponse))]
[JsonSerializable(typeof(RunResponse))]
[JsonSerializable(typeof(IReadOnlyList<RunResponse>), TypeInfoPropertyName = "RunResponseList")]
[JsonSerializable(typeof(CapabilitiesResponse))]
[JsonSerializable(typeof(ApprovalResponse))]
[JsonSerializable(typeof(IReadOnlyList<ApprovalResponse>), TypeInfoPropertyName = "ApprovalResponseList")]
[JsonSerializable(typeof(CreateScheduleRequest))]
[JsonSerializable(typeof(UpdateScheduleRequest))]
[JsonSerializable(typeof(ScheduleResponse))]
[JsonSerializable(typeof(IReadOnlyList<ScheduleResponse>), TypeInfoPropertyName = "ScheduleResponseList")]
[JsonSerializable(typeof(ContinueRunRequest))]
[JsonSerializable(typeof(AddMcpServerRequest))]
[JsonSerializable(typeof(McpServerResponse))]
[JsonSerializable(typeof(IReadOnlyList<McpServerResponse>), TypeInfoPropertyName = "McpServerResponseList")]
[JsonSerializable(typeof(DecideApprovalRequest))]
[JsonSerializable(typeof(TranscriptMessage))]
[JsonSerializable(typeof(IReadOnlyList<TranscriptMessage>), TypeInfoPropertyName = "TranscriptMessageList")]
[JsonSerializable(typeof(ArtifactInfo))]
[JsonSerializable(typeof(IReadOnlyList<ArtifactInfo>), TypeInfoPropertyName = "ArtifactInfoList")]
internal sealed partial class OrkisJsonContext : JsonSerializerContext;

/// <summary>
/// The one configured context instance the client serializes through. Enums ride a
/// camelCase string converter to match the daemon's wire form exactly; that converter
/// is AOT-only-unsafe (fine under trimming), and is the single thing left to replace
/// if the client is ever taken to Native AOT.
/// </summary>
internal static class OrkisJson
{
    internal static readonly OrkisJsonContext Context = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        }
    );
}
