namespace Orkis.Supervision;

/// <summary>Configuration for <see cref="FileApprovalInbox"/>.</summary>
public sealed class FileApprovalInboxOptions
{
    /// <summary>
    /// Root directory for the approval inbox; one subdirectory is created per run.
    /// Defaults to an "orkis/approvals" directory under the local application data path.
    /// </summary>
    public string RootPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "approvals");
}
