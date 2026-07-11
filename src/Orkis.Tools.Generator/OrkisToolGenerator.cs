using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orkis.Tools.Generator;

/// <summary>
/// Emits an <c>ITool</c> implementation — JSON schema, argument binding, invocation —
/// for every method annotated with <c>[OrkisTool]</c>.
/// </summary>
[Generator]
public sealed class OrkisToolGenerator : IIncrementalGenerator
{
    private const string ToolAttributeName = "Orkis.Tools.OrkisToolAttribute";
    private const string ParameterAttributeName = "Orkis.Tools.OrkisToolParameterAttribute";
    private const string CancellationTokenFullName = "global::System.Threading.CancellationToken";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var tools = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ToolAttributeName,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, _) => CreateModel(ctx)
            )
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        context.RegisterSourceOutput(tools.Collect(), static (spc, models) => Emitter.Emit(spc, models));
    }

    private static ToolModel? CreateModel(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method || method.ContainingType is not { } type)
        {
            return null;
        }

        var typeIsPartial =
            context.TargetNode.Parent is TypeDeclarationSyntax typeSyntax
            && typeSyntax.Modifiers.Any(SyntaxKind.PartialKeyword);

        string? name = null;
        string? description = null;
        var risk = 1; // ToolRisk.Mutating — matches the runtime default.
        foreach (var argument in context.Attributes[0].NamedArguments)
        {
            switch (argument.Key)
            {
                case "Name":
                    name = argument.Value.Value as string;
                    break;
                case "Description":
                    description = argument.Value.Value as string;
                    break;
                case "Risk":
                    risk = argument.Value.Value is int value ? value : risk;
                    break;
            }
        }

        var parameters = ImmutableArray.CreateBuilder<ParameterModel>(method.Parameters.Length);
        foreach (var parameter in method.Parameters)
        {
            parameters.Add(CreateParameter(parameter));
        }

        var (returnKind, returnPayloadType) = ClassifyReturn(method);

        return new ToolModel(
            Namespace: type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            TypeName: type.Name,
            TypeIsPartial: typeIsPartial,
            TypeIsStatic: type.IsStatic,
            TypeIsNested: type.ContainingType is not null,
            MethodName: method.Name,
            MethodIsStatic: method.IsStatic,
            ToolName: name ?? ToSnakeCase(TrimAsyncSuffix(method.Name)),
            Description: description ?? "",
            Risk: risk,
            ReturnKind: returnKind,
            ReturnPayloadType: returnPayloadType,
            Parameters: parameters.ToImmutable()
        );
    }

    private static ParameterModel CreateParameter(IParameterSymbol parameter)
    {
        var type = parameter.Type;
        var typeFullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var kind = typeFullName == CancellationTokenFullName ? ParamKind.CancellationToken : Classify(type);

        string? parameterDescription = null;
        foreach (var attribute in parameter.GetAttributes())
        {
            if (
                attribute.AttributeClass?.ToDisplayString() == ParameterAttributeName
                && attribute.ConstructorArguments.Length == 1
            )
            {
                parameterDescription = attribute.ConstructorArguments[0].Value as string;
            }
        }

        var enumValues =
            kind == ParamKind.Enum
                ? type.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(static field => field.HasConstantValue)
                    .Select(static field => field.Name)
                    .ToImmutableArray()
                : ImmutableArray<string>.Empty;

        return new ParameterModel(
            Name: parameter.Name,
            TypeFullName: typeFullName,
            Kind: kind,
            IsOptional: parameter.HasExplicitDefaultValue,
            DefaultLiteral: FormatDefault(parameter),
            Description: parameterDescription,
            EnumValues: enumValues
        );
    }

    private static ParamKind Classify(ITypeSymbol type) =>
        type.SpecialType switch
        {
            SpecialType.System_String => ParamKind.String,
            SpecialType.System_Boolean => ParamKind.Bool,
            SpecialType.System_Int32 => ParamKind.Int32,
            SpecialType.System_Int64 => ParamKind.Int64,
            SpecialType.System_Double => ParamKind.Double,
            SpecialType.System_Single => ParamKind.Single,
            SpecialType.System_Decimal => ParamKind.Decimal,
            _ => type.TypeKind == TypeKind.Enum ? ParamKind.Enum : ParamKind.Json,
        };

    private static (ReturnKind Kind, string? PayloadType) ClassifyReturn(IMethodSymbol method)
    {
        if (method.ReturnsVoid)
        {
            return (ReturnKind.Void, null);
        }

        var type = method.ReturnType;
        if (type.SpecialType == SpecialType.System_String)
        {
            return (ReturnKind.String, null);
        }

        if (type is INamedTypeSymbol named)
        {
            switch (named.OriginalDefinition.ToDisplayString())
            {
                case "System.Threading.Tasks.Task":
                case "System.Threading.Tasks.ValueTask":
                    return (ReturnKind.TaskVoid, null);
                case "System.Threading.Tasks.Task<TResult>":
                    return ClassifyPayload(named.TypeArguments[0], ReturnKind.TaskString, ReturnKind.TaskOther);
                case "System.Threading.Tasks.ValueTask<TResult>":
                    return ClassifyPayload(
                        named.TypeArguments[0],
                        ReturnKind.ValueTaskString,
                        ReturnKind.ValueTaskOther
                    );
            }
        }

        return (ReturnKind.Other, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        static (ReturnKind, string?) ClassifyPayload(ITypeSymbol payload, ReturnKind ifString, ReturnKind ifOther) =>
            payload.SpecialType == SpecialType.System_String
                ? (ifString, null)
                : (ifOther, payload.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    private static string? FormatDefault(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
        {
            return null;
        }

        var value = parameter.ExplicitDefaultValue;
        if (value is null)
        {
            return "default!";
        }

        if (parameter.Type.TypeKind == TypeKind.Enum)
        {
            var enumType = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var underlying = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return "(" + enumType + ")(" + underlying.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return value switch
        {
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            bool b => b ? "true" : "false",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default!",
        };
    }

    private static string TrimAsyncSuffix(string methodName) =>
        methodName.Length > 5 && methodName.EndsWith("Async", StringComparison.Ordinal)
            ? methodName.Substring(0, methodName.Length - 5)
            : methodName;

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
