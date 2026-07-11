using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Retrieval;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the HTML document parser.</summary>
public static class HtmlParserServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="HtmlDocumentParser"/> alongside any other registered
    /// <see cref="IDocumentParser"/>s, extending ingestion to HTML content.
    /// </summary>
    public static IServiceCollection AddOrkisHtmlParser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentParser, HtmlDocumentParser>());
        return services;
    }
}
