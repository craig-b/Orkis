using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Orkis.Sandboxing;

/// <summary>
/// Executes commands under bubblewrap (bwrap) with fresh user, PID, mount, and
/// network namespaces: a read-only view of system directories, a private /tmp,
/// no host network (by default), and a writable scratch directory at /work.
/// </summary>
/// <remarks>
/// This provides <see cref="SandboxLevel.Strict"/> isolation via the kernel's
/// unprivileged user namespaces — no daemon or root required. Linux only;
/// requires the bubblewrap binary and a kernel allowing unprivileged user
/// namespaces (probe with <see cref="IsSupportedAsync"/>).
/// </remarks>
public sealed class BubblewrapSandbox : ISandbox, IWorkspaceFileAccess
{
    private const string InnerWorkPath = "/work";

    private readonly BubblewrapSandboxOptions _options;
    private readonly TimeProvider _timeProvider;

    public BubblewrapSandbox(IOptions<BubblewrapSandboxOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public SandboxLevel Level => SandboxLevel.Strict;

    /// <inheritdoc />
    public Task<Stream?> ReadWorkspaceFileAsync(
        string workspaceKey,
        string relativePath,
        CancellationToken cancellationToken = default
    ) => SandboxScratch.OpenWorkspaceFileAsync(_options.WorkingRoot, workspaceKey, relativePath);

    /// <inheritdoc />
    public Task WriteWorkspaceFileAsync(
        string workspaceKey,
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default
    ) =>
        SandboxScratch.WriteWorkspaceFileAsync(
            _options.WorkingRoot,
            workspaceKey,
            relativePath,
            content,
            cancellationToken
        );

    /// <inheritdoc />
    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var scratchDirectory = SandboxScratch.Locate(_options.WorkingRoot, request.WorkspaceKey);
        var outsideWorkingDirectory = SandboxScratch.Resolve(scratchDirectory, request.WorkingDirectory);
        Directory.CreateDirectory(outsideWorkingDirectory);

        try
        {
            return await SandboxProcess
                .RunAsync(
                    BuildStartInfo(request, scratchDirectory, outsideWorkingDirectory),
                    request.Timeout ?? _options.DefaultTimeout,
                    _options.MaxOutputLength,
                    _timeProvider,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        finally
        {
            if (request.WorkspaceKey is null)
            {
                SandboxScratch.TryDelete(scratchDirectory);
            }
        }
    }

    private ProcessStartInfo BuildStartInfo(
        SandboxExecutionRequest request,
        string scratchDirectory,
        string outsideWorkingDirectory
    )
    {
        var startInfo = new ProcessStartInfo { FileName = _options.BubblewrapPath };
        var arguments = startInfo.ArgumentList;

        arguments.Add("--unshare-all");
        if (_options.AllowNetwork)
        {
            arguments.Add("--share-net");
        }

        arguments.Add("--die-with-parent");
        arguments.Add("--new-session");

        foreach (var path in _options.ReadOnlyPaths.Where(Path.Exists))
        {
            arguments.Add("--ro-bind");
            arguments.Add(path);
            arguments.Add(path);
        }

        arguments.Add("--proc");
        arguments.Add("/proc");
        arguments.Add("--dev");
        arguments.Add("/dev");
        arguments.Add("--tmpfs");
        arguments.Add("/tmp");

        arguments.Add("--bind");
        arguments.Add(scratchDirectory);
        arguments.Add(InnerWorkPath);
        arguments.Add("--chdir");
        arguments.Add(ToInnerPath(scratchDirectory, outsideWorkingDirectory));

        arguments.Add("--");
        arguments.Add(request.Executable);
        foreach (var argument in request.Arguments)
        {
            arguments.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var name in _options.EnvironmentAllowlist)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                startInfo.Environment[name] = value;
            }
        }

        startInfo.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        startInfo.Environment["HOME"] = InnerWorkPath;
        return startInfo;
    }

    private static string ToInnerPath(string scratchDirectory, string outsidePath)
    {
        var relative = Path.GetRelativePath(scratchDirectory, outsidePath);
        return relative == "." ? InnerWorkPath : $"{InnerWorkPath}/{relative.Replace('\\', '/')}";
    }

    /// <summary>
    /// Probes whether bubblewrap sandboxing works here: the binary exists and the
    /// kernel permits unprivileged user namespaces.
    /// </summary>
    public static async Task<bool> IsSupportedAsync(
        string bubblewrapPath = "bwrap",
        CancellationToken cancellationToken = default
    )
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo { FileName = bubblewrapPath };
            foreach (var argument in (string[])["--unshare-all", "--ro-bind", "/usr", "/usr", "/usr/bin/true"])
            {
                startInfo.ArgumentList.Add(argument);
            }

            var result = await SandboxProcess
                .RunAsync(startInfo, TimeSpan.FromSeconds(10), 1024, TimeProvider.System, cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // Binary not found.
        }
    }
}
