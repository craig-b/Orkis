namespace Orkis.Runs;

/// <summary>Options for <see cref="FileScheduleStore"/>.</summary>
public sealed class FileScheduleStoreOptions
{
    /// <summary>Root directory schedules are stored under.</summary>
    public string RootPath { get; set; } = "";
}
