using Microsoft.Extensions.Options;
using Orkis.Sandboxing;

namespace Orkis.Sandbox.Firecracker.Tests;

// These tests boot real micro-VMs with a network device and need the host plumbing
// from scripts/setup-firecracker-network.sh (run once, with sudo). They self-skip
// (pass vacuously) where KVM, the guest images, or the TAP pool are absent.
public sealed class RestrictedEgressTests(PatchedRootfsFixture fixture)
    : IClassFixture<PatchedRootfsFixture>,
        IAsyncLifetime
{
    private static bool NetworkProvisioned => Directory.Exists("/sys/class/net/orkis-tap0");

    private bool Available => WarmVmTests.BaseAssetsAvailable && fixture.RootfsPath is not null && NetworkProvisioned;

    private readonly string _workingRoot = Path.Combine(Path.GetTempPath(), $"orkis-egress-tests-{Guid.NewGuid():n}");
    private readonly List<FirecrackerSandbox> _sandboxes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var sandbox in _sandboxes)
        {
            await sandbox.DisposeAsync();
        }

        if (Directory.Exists(_workingRoot))
        {
            Directory.Delete(_workingRoot, recursive: true);
        }
    }

    private FirecrackerSandbox CreateSandbox()
    {
        var sandbox = new FirecrackerSandbox(
            Options.Create(
                new FirecrackerSandboxOptions
                {
                    KernelImagePath = WarmVmTests.KernelPath,
                    RootfsImagePath = fixture.RootfsPath!,
                    WorkingRoot = _workingRoot,
                    Network = NetworkPolicy.RestrictedEgress,
                }
            )
        );
        _sandboxes.Add(sandbox);
        return sandbox;
    }

    [Fact]
    public async Task PublicEgressWorksWhilePrivateRangesAndHostAreBlocked()
    {
        if (!Available)
        {
            return;
        }

        // One boot, four probes: public IP, DNS + public name, the gateway (host),
        // and a private LAN address. curl exits non-zero when blocked (timeout).
        var script = """
            pub=$(curl -s --max-time 20 -o /dev/null -w '%{http_code}' http://1.1.1.1); echo "pub=$pub"
            dns=$(curl -s --max-time 20 -o /dev/null -w '%{http_code}' http://example.com); echo "dns=$dns"
            curl -s --max-time 5 -o /dev/null http://172.30.0.1 && echo host=open || echo host=blocked
            curl -s --max-time 5 -o /dev/null http://192.168.1.1 && echo lan=open || echo lan=blocked
            """;

        var result = await CreateSandbox()
            .ExecuteAsync(
                new SandboxExecutionRequest
                {
                    Executable = "/bin/sh",
                    Arguments = ["-c", script],
                    Timeout = TimeSpan.FromSeconds(90),
                }
            );

        var lines = result.StandardOutput.Trim().Split('\n').Select(static l => l.Trim()).ToList();
        Assert.Contains(lines, static l => l.StartsWith("pub=", StringComparison.Ordinal) && l != "pub=000");
        Assert.Contains(lines, static l => l.StartsWith("dns=", StringComparison.Ordinal) && l != "dns=000");
        Assert.Contains("host=blocked", lines);
        Assert.Contains("lan=blocked", lines);
    }

    [Fact]
    public async Task PerRequestGrantOverridesTheConfiguredPolicy()
    {
        if (!Available)
        {
            return;
        }

        // The sandbox is configured with no network; the grant arrives per request —
        // the supervision-granted path.
        var sandbox = new FirecrackerSandbox(
            Options.Create(
                new FirecrackerSandboxOptions
                {
                    KernelImagePath = WarmVmTests.KernelPath,
                    RootfsImagePath = fixture.RootfsPath!,
                    WorkingRoot = _workingRoot,
                    Network = NetworkPolicy.None,
                }
            )
        );
        _sandboxes.Add(sandbox);

        var probe = "ls /sys/class/net | grep -c eth0";

        var ungranted = await sandbox.ExecuteAsync(
            new SandboxExecutionRequest { Executable = "/bin/sh", Arguments = ["-c", probe] }
        );
        Assert.Equal("0", ungranted.StandardOutput.Trim());

        var granted = await sandbox.ExecuteAsync(
            new SandboxExecutionRequest
            {
                Executable = "/bin/sh",
                Arguments = ["-c", probe],
                Network = NetworkMode.RestrictedEgress,
            }
        );
        Assert.Equal("1", granted.StandardOutput.Trim());
    }

    [Fact]
    public async Task ConcurrentNetworkedVmsGetDistinctTaps()
    {
        if (!Available)
        {
            return;
        }

        var sandbox = CreateSandbox();
        var probe = new SandboxExecutionRequest
        {
            Executable = "/bin/sh",
            Arguments = ["-c", "ip -4 -o addr show eth0 | tr -s ' ' | cut -d' ' -f4"],
            Timeout = TimeSpan.FromSeconds(60),
        };

        var results = await Task.WhenAll(sandbox.ExecuteAsync(probe), sandbox.ExecuteAsync(probe));

        Assert.All(results, static r => Assert.Equal(0, r.ExitCode));
        var addresses = results.Select(static r => r.StandardOutput.Trim()).ToList();
        Assert.Equal(2, addresses.Distinct(StringComparer.Ordinal).Count());
    }
}
