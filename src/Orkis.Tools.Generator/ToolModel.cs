using System.Collections.Immutable;

namespace Orkis.Tools.Generator;

internal enum ParamKind
{
    String,
    Bool,
    Int32,
    Int64,
    Double,
    Single,
    Decimal,
    Enum,
    Json,
    CancellationToken,
}

internal enum ReturnKind
{
    Void,
    TaskVoid,
    String,
    TaskString,
    ValueTaskString,
    Other,
    TaskOther,
    ValueTaskOther,
}

internal sealed record ParameterModel(
    string Name,
    string TypeFullName,
    ParamKind Kind,
    bool IsOptional,
    string? DefaultLiteral,
    string? Description,
    ImmutableArray<string> EnumValues
);

internal sealed record ToolModel(
    string? Namespace,
    string TypeName,
    bool TypeIsPartial,
    bool TypeIsStatic,
    bool TypeIsNested,
    string MethodName,
    bool MethodIsStatic,
    string ToolName,
    string Description,
    int Risk,
    ReturnKind ReturnKind,
    string? ReturnPayloadType,
    ImmutableArray<ParameterModel> Parameters
);
