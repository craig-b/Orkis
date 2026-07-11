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

    [Fact]
    public void NonePolicyIsAccepted()
    {
        var sandbox = Create(NetworkPolicy.None);
        Assert.Equal(SandboxLevel.Strict, sandbox.Level);
    }

    [Theory]
    [InlineData(NetworkMode.RestrictedEgress)]
    [InlineData(NetworkMode.Allowlist)]
    public void UnimplementedPoliciesFailFast(NetworkMode mode)
    {
        var policy = new NetworkPolicy { Mode = mode, AllowedDomains = ["example.com"] };

        var exception = Assert.Throws<NotSupportedException>(() => Create(policy));
        Assert.Contains(mode.ToString(), exception.Message, StringComparison.Ordinal);
    }
}
