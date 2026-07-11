using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Supervision;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the file-system approval queue.</summary>
public static class FileApprovalInboxServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="FileApprovalInbox"/> as the <see cref="IApprovalInbox"/>
    /// implementation so pending approvals can be decided from another process and
    /// survive restarts. Replaces any existing registration (such as the in-memory
    /// default from <c>AddOrkis</c>), so call order relative to <c>AddOrkis</c> does
    /// not matter.
    /// </summary>
    public static IServiceCollection AddOrkisFileApprovalInbox(
        this IServiceCollection services,
        Action<FileApprovalInboxOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services
            .AddOptions<FileApprovalInboxOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.RootPath),
                $"{nameof(FileApprovalInboxOptions)}.{nameof(FileApprovalInboxOptions.RootPath)} must be set."
            )
            .ValidateOnStart();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.RemoveAll<IApprovalInbox>();
        services.AddSingleton<IApprovalInbox, FileApprovalInbox>();
        return services;
    }
}
