using Microsoft.Extensions.Options;
using Orkis.Sandboxing;

namespace Orkis.Sandbox.Firecracker.Tests;

// Pure configuration tests — no VM boot, so they run everywhere.
public sealed class NetworkPolicyTests
{
    private static FirecrackerSandbox Create(NetworkPolicy network) =>
        new(
            Options.Create(
                new FirecrackerSandboxOptions
                {
                    KernelImagePath = "/k",
                    RootfsImagePath = "/r",
                    Network = network,
                }
            )
        );

    [Fact]
    public void DefaultsToNoNetwork() => Assert.Equal(NetworkMode.None, new FirecrackerSandboxOptions().Network.Mode);

    [Theory]
    [InlineData(NetworkMode.None)]
    [InlineData(NetworkMode.RestrictedEgress)]
    public void ImplementedPoliciesAreAccepted(NetworkMode mode)
    {
        var sandbox = Create(new NetworkPolicy { Mode = mode });
        Assert.Equal(SandboxLevel.Strict, sandbox.Level);
    }

    [Fact]
    public void AllowlistFailsFast()
    {
        var policy = new NetworkPolicy { Mode = NetworkMode.Allowlist, AllowedDomains = ["example.com"] };

        var exception = Assert.Throws<NotSupportedException>(() => Create(policy));
        Assert.Contains("Allowlist", exception.Message, StringComparison.Ordinal);
    }
}

public sealed class TapLeaseTests
{
    // A distinct prefix so these tests never contend with real orkis-tap locks.
    private static readonly string Prefix = $"orkistest{Guid.NewGuid():n}"[..12] + "-tap";

    [Fact]
    public void ReturnsNullWhenNoDevicesAreProvisioned()
    {
        Assert.Null(TapLease.TryAcquire(Prefix, 4, static _ => false));
    }

    [Fact]
    public void AcquiresDistinctDevicesAndReleasesOnDispose()
    {
        using var first = TapLease.TryAcquire(Prefix, 2, static _ => true);
        var second = TapLease.TryAcquire(Prefix, 2, static _ => true);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(0, first.Index);
        Assert.Equal(1, second.Index);
        Assert.Equal(Prefix + "0", first.DeviceName);

        // Pool exhausted while both are held.
        Assert.Null(TapLease.TryAcquire(Prefix, 2, static _ => true));

        // Releasing one frees exactly that device.
        second.Dispose();
        using var reacquired = TapLease.TryAcquire(Prefix, 2, static _ => true);
        Assert.NotNull(reacquired);
        Assert.Equal(1, reacquired.Index);
    }
}
