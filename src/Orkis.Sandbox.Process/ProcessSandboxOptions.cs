namespace Orkis.Sandboxing;

/// <summary>Configuration for <see cref="ProcessSandbox"/>.</summary>
public sealed class ProcessSandboxOptions
{
    /// <summary>
    /// Directory under which each execution gets its own scratch working directory.
    /// Defaults to an "orkis-sandbox" directory under the system temp path.
    /// </summary>
    public string WorkingRoot { get; set; } = Path.Combine(Path.GetTempPath(), "orkis-sandbox");

    /// <summary>Timeout applied when a request does not specify one.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum characters captured per output stream; output beyond this is drained
    /// (so the child process never blocks on a full pipe) but discarded.
    /// </summary>
    public int MaxOutputLength { get; set; } = 64 * 1024;

    /// <summary>
    /// Environment variables passed through from the host to the child process.
    /// Everything else is stripped.
    /// </summary>
    public IList<string> EnvironmentAllowlist { get; } = ["PATH", "HOME", "TMPDIR", "TEMP", "TMP", "SystemRoot"];
}
