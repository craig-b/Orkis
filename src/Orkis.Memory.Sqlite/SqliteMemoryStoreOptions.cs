namespace Orkis.Memory;

/// <summary>Configuration for <see cref="SqliteMemoryStore"/>.</summary>
public sealed class SqliteMemoryStoreOptions
{
    /// <summary>
    /// Path of the SQLite database file; created (with its directory) on first use.
    /// Defaults to an "orkis/memory.db" file under the local application data path.
    /// </summary>
    public string DatabasePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "memory.db");
}
