using System.Text.Json;
using Microsoft.Extensions.AI;
using Orkis.Tools;

namespace Orkis.Core.Tools;

/// <summary>
/// Presents an Orkis tool to an <see cref="IChatClient"/> as a declaration only.
/// Invocation never goes through the chat client: the agent runner executes tools
/// itself so that every call passes supervision first.
/// </summary>
internal sealed class ToolDeclaration(ToolDescriptor descriptor) : AIFunction
{
    public override string Name => descriptor.Name;

    public override string Description => descriptor.Description;

    public override JsonElement JsonSchema => descriptor.ParametersSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    ) =>
        throw new NotSupportedException(
            "Orkis tools are invoked by the agent runner under supervision, not by the chat client."
        );
}
