using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Retrieval;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the PDF document parser.</summary>
public static class PdfParserServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="PdfDocumentParser"/> alongside any other registered
    /// <see cref="IDocumentParser"/>s, extending ingestion to PDF content.
    /// </summary>
    public static IServiceCollection AddOrkisPdfParser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentParser, PdfDocumentParser>());
        return services;
    }
}
