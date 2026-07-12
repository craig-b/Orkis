namespace Orkis.Sandboxing;

/// <summary>
/// An exclusive lease on one of the pre-provisioned TAP devices
/// (<c>scripts/setup-firecracker-network.sh</c>). A rw TAP can carry one VM at a
/// time; exclusivity is a cross-process file lock that the OS releases when the
/// holder exits — including by crash — so a dead VM never wedges the pool.
/// </summary>
internal sealed class TapLease : IDisposable
{
    private readonly FileStream _lock;

    private TapLease(string deviceName, int index, FileStream fileLock)
    {
        DeviceName = deviceName;
        Index = index;
        _lock = fileLock;
    }

    /// <summary>The TAP device name, e.g. "orkis-tap3".</summary>
    public string DeviceName { get; }

    /// <summary>Index in the pool; determines the guest's static address and MAC.</summary>
    public int Index { get; }

    /// <summary>
    /// Acquires the first free provisioned TAP device, or <see langword="null"/> when
    /// none exists or all are in use.
    /// </summary>
    public static TapLease? TryAcquire(string prefix, int poolSize, Func<string, bool>? deviceExists = null)
    {
        deviceExists ??= static name => Directory.Exists("/sys/class/net/" + name);

        var lockDirectory = Path.Combine(Path.GetTempPath(), "orkis-taps");
        Directory.CreateDirectory(lockDirectory);

        for (var index = 0; index < poolSize; index++)
        {
            var name = prefix + index;
            if (!deviceExists(name))
            {
                continue;
            }

            try
            {
                var fileLock = new FileStream(
                    Path.Combine(lockDirectory, name + ".lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
                return new TapLease(name, index, fileLock);
            }
            catch (IOException)
            {
                // Held by another VM; try the next device.
            }
        }

        return null;
    }

    public void Dispose() => _lock.Dispose();
}
