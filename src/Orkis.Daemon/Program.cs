using Orkis.Daemon;
using Orkis.Sandboxing;

// The daemon is configured the same way as the CLI host — environment variables for
// paths and providers — so the two composition roots stay interchangeable.
var offline = args.Contains("--offline") || Environment.GetEnvironmentVariable("ORKIS_OFFLINE") == "1";

var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var provider = Environment.GetEnvironmentVariable("ORKIS_PROVIDER")?.ToLowerInvariant();
provider ??=
    !string.IsNullOrEmpty(anthropicKey) ? "anthropic"
    : !string.IsNullOrEmpty(openAiKey) ? "openai"
    : null;

var selectedKey = provider switch
{
    "anthropic" => anthropicKey,
    "openai" => openAiKey,
    _ => null,
};
if (!offline && string.IsNullOrEmpty(selectedKey))
{
    Console.Error.WriteLine("No API key found. Set ANTHROPIC_API_KEY or OPENAI_API_KEY for live runs");
    Console.Error.WriteLine("(ORKIS_PROVIDER=anthropic|openai picks explicitly), or pass --offline.");
    return 1;
}

var model =
    Environment.GetEnvironmentVariable("ORKIS_MODEL") ?? (provider == "openai" ? "gpt-5-mini" : "claude-sonnet-5");

var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis");

// Sandbox: strongest available wins, overridable with ORKIS_SANDBOX=firecracker|bubblewrap|process.
var firecrackerHome =
    Environment.GetEnvironmentVariable("ORKIS_FIRECRACKER_HOME") ?? Path.Combine(dataRoot, "firecracker");
var firecrackerKernel = Path.Combine(firecrackerHome, "vmlinux");
var firecrackerRootfs = Path.Combine(firecrackerHome, "rootfs.ext4");
var firecrackerReady =
    FirecrackerSandbox.IsSupported() && File.Exists(firecrackerKernel) && File.Exists(firecrackerRootfs);

var sandbox =
    Environment.GetEnvironmentVariable("ORKIS_SANDBOX")?.ToLowerInvariant()
    ?? (
        firecrackerReady ? "firecracker"
        : await BubblewrapSandbox.IsSupportedAsync() ? "bubblewrap"
        : "process"
    );

if (sandbox == "firecracker" && !firecrackerReady)
{
    Console.Error.WriteLine("Firecracker is not ready (KVM, binary, or guest images missing).");
    Console.Error.WriteLine("Run scripts/setup-firecracker.sh, or choose another ORKIS_SANDBOX.");
    return 1;
}

var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
var socketPath =
    Environment.GetEnvironmentVariable("ORKIS_SOCKET")
    ?? Path.Combine(string.IsNullOrEmpty(runtimeDir) ? dataRoot : Path.Combine(runtimeDir, "orkis"), "orkis.sock");

var settings = new DaemonSettings
{
    SocketPath = socketPath,
    CheckpointDirectory =
        Environment.GetEnvironmentVariable("ORKIS_CHECKPOINT_DIR") ?? Path.Combine(dataRoot, "checkpoints"),
    EventDirectory = Environment.GetEnvironmentVariable("ORKIS_EVENT_DIR") ?? Path.Combine(dataRoot, "events"),
    ApprovalDirectory = Environment.GetEnvironmentVariable("ORKIS_APPROVAL_DIR") ?? Path.Combine(dataRoot, "approvals"),
    ArtifactDirectory = Environment.GetEnvironmentVariable("ORKIS_ARTIFACT_DIR") ?? Path.Combine(dataRoot, "artifacts"),
    WorkspaceKey = Environment.GetEnvironmentVariable("ORKIS_WORKSPACE") ?? "default",
    Offline = offline,
    Provider = provider,
    ApiKey = selectedKey,
    Model = offline ? null : model,
    Sandbox = sandbox,
    FirecrackerKernelPath = firecrackerKernel,
    FirecrackerRootfsPath = firecrackerRootfs,
    FirecrackerEgress = Environment.GetEnvironmentVariable("ORKIS_NETWORK")?.ToLowerInvariant() == "egress",
};

var app = DaemonApplication.Create(settings);

Console.WriteLine($"orkis daemon | listening on unix:{settings.SocketPath}");
Console.WriteLine($"mode: {(offline ? "offline (scripted model)" : $"live ({provider}: {model})")}");
Console.WriteLine($"sandbox: {sandbox} isolation + host execution (granted per approval)");
Console.WriteLine($"supervision: queue (default) | yolo{(offline ? "" : " | ai")}");
Console.WriteLine($"data: {dataRoot}");

await app.RunAsync();
return 0;
