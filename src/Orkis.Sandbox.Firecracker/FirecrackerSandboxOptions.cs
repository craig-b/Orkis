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
    /// Network access granted to the micro-VM. <see cref="NetworkPolicy.None"/> (the
    /// default) and <see cref="NetworkPolicy.RestrictedEgress"/> are supported;
    /// restricted egress requires the host plumbing provisioned once by
    /// <c>scripts/setup-firecracker-network.sh</c>. <see cref="NetworkMode.Allowlist"/>
    /// fails fast.
    /// </summary>
    public NetworkPolicy Network { get; set; } = NetworkPolicy.None;

    /// <summary>Name prefix of the pre-provisioned TAP device pool.</summary>
    public string TapDevicePrefix { get; set; } = "orkis-tap";

    /// <summary>Size of the TAP pool — the maximum number of concurrently networked VMs.</summary>
    public int TapPoolSize { get; set; } = 8;

    /// <summary>
    /// First three octets of the guest /24 network; must match the subnet the setup
    /// script provisioned. The gateway is .1 and a VM on TAP index N is .(N+2).
    /// </summary>
    public string GuestSubnetPrefix { get; set; } = "172.30.0";

    /// <summary>Timeout applied when a request does not specify one.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Reuse one warm micro-VM per workspace for successive commands (requires a rootfs
    /// with the guest agent; falls back to boot-per-command automatically when the
    /// agent never comes up). Applies only to executions with a workspace key.
    /// </summary>
    public bool EnableWarmVms { get; set; } = true;

    /// <summary>How long a warm VM may sit idle before it is shut down.</summary>
    public TimeSpan WarmVmIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait after boot for the guest agent to accept a vsock connection
    /// before concluding the rootfs has no agent and falling back to cold execution.
    /// </summary>
    public TimeSpan AgentReadyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum characters captured per output stream.</summary>
    public int MaxOutputLength { get; set; } = 64 * 1024;

    /// <summary>
    /// Environment variables passed through from the host into the guest command.
    /// </summary>
    public IList<string> EnvironmentAllowlist { get; } = ["TERM"];
}
