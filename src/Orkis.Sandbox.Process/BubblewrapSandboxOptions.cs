namespace Orkis.Sandboxing;

/// <summary>Configuration for <see cref="BubblewrapSandbox"/>.</summary>
public sealed class BubblewrapSandboxOptions
{
    /// <summary>Path to the bubblewrap binary.</summary>
    public string BubblewrapPath { get; set; } = "bwrap";

    /// <summary>
    /// Directory under which each execution gets its own scratch directory, bound
    /// into the sandbox as /work.
    /// </summary>
    public string WorkingRoot { get; set; } = Path.Combine(Path.GetTempPath(), "orkis-sandbox");

    /// <summary>Timeout applied when a request does not specify one.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum characters captured per output stream.</summary>
    public int MaxOutputLength { get; set; } = 64 * 1024;

    /// <summary>
    /// Whether the sandbox shares the host's network. Off by default: the sandbox
    /// gets its own (empty) network namespace with only a loopback device.
    /// </summary>
    public bool AllowNetwork { get; set; }

    /// <summary>
    /// Host paths bound read-only into the sandbox. Paths that do not exist on the
    /// host are skipped, so the default list works across distro layouts.
    /// </summary>
    public IList<string> ReadOnlyPaths { get; } = ["/usr", "/bin", "/sbin", "/lib", "/lib64", "/etc"];

    /// <summary>
    /// Environment variables passed through from the host. PATH and HOME are always
    /// set to fixed in-sandbox values regardless of this list.
    /// </summary>
    public IList<string> EnvironmentAllowlist { get; } = ["TERM"];
}
