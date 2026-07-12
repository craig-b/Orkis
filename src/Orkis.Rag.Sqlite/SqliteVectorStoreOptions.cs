namespace Orkis.Retrieval;

/// <summary>Configuration for <see cref="SqliteVectorStore"/>.</summary>
public sealed class SqliteVectorStoreOptions
{
    /// <summary>
    /// Path of the SQLite database file; created (with its directory) on first use.
    /// Defaults to an "orkis/rag.db" file under the local application data path.
    /// </summary>
    public string DatabasePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "rag.db");
}
