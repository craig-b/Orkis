namespace Orkis.Sandboxing;

/// <summary>Configuration for <see cref="FirecrackerSandbox"/>.</summary>
public sealed class FirecrackerSandboxOptions
{
    /// <summary>Path to the firecracker binary.</summary>
    public string FirecrackerPath { get; set; } = "firecracker";

    /// <summary>Path to the mkfs.ext4 binary used to build per-execution scratch images.</summary>
    public string MkfsPath { get; set; } = "mkfs.ext4";

    /// <summary>
    /// Path to the debugfs binary (e2fsprogs), used to inject each execution's command
    /// script into a persistent workspace image without mounting it.
    /// </summary>
    public string DebugfsPath { get; set; } = "debugfs";

    /// <summary>
    /// Path to the e2fsck binary (e2fsprogs). A guest that halts without a clean
    /// unmount leaves the workspace image's journal dirty; e2fsck replays it before
    /// any debugfs access, since debugfs reads (and writes) stale metadata otherwise
    /// and the kernel's own replay at next mount would clobber debugfs's changes.
    /// </summary>
    public string E2fsckPath { get; set; } = "e2fsck";

    /// <summary>
    /// Path to the uncompressed guest kernel (vmlinux). Required.
    /// scripts/setup-firecracker.sh downloads a suitable one.
    /// </summary>
    public string KernelImagePath { get; set; } = "";

    /// <summary>
    /// Path to the root filesystem image. Required. The rootfs is mounted read-only
    /// and must contain an /init honoring the Orkis contract (mount /dev/vdb at /work,
    /// run /work/.orkis/command.sh, emit the ORKIS output markers on the console);
    /// scripts/setup-firecracker.sh builds a busybox-based one.
    /// </summary>
    public string RootfsImagePath { get; set; } = "";

    /// <summary>Directory for per-execution staging and image files.</summary>
    public string WorkingRoot { get; set; } = Path.Combine(Path.GetTempPath(), "orkis-firecracker");

    /// <summary>Virtual CPUs per micro-VM.</summary>
    public int VcpuCount { get; set; } = 1;

    /// <summary>Guest memory per micro-VM, in MiB. The Alpine + Python guest wants headroom.</summary>
    public int MemorySizeMib { get; set; } = 256;

    /// <summary>Size of the writable /work scratch image, in MiB.</summary>
    public int ScratchSizeMib { get; set; } = 64;

    /// <summary>
    /// Network access granted to the micro-VM. Only <see cref="NetworkPolicy.None"/> is
    /// implemented; configuring any other mode fails fast. See <see cref="NetworkPolicy"/>.
    /// </summary>
    public NetworkPolicy Network { get; set; } = NetworkPolicy.None;

    /// <summary>Timeout applied when a request does not specify one.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum characters captured per output stream.</summary>
    public int MaxOutputLength { get; set; } = 64 * 1024;

    /// <summary>
    /// Environment variables passed through from the host into the guest command.
    /// </summary>
    public IList<string> EnvironmentAllowlist { get; } = ["TERM"];
}
