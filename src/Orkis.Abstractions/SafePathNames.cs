using System.Security.Cryptography;
using System.Text;

namespace Orkis;

/// <summary>
/// Maps arbitrary identifiers to file-system-safe names: a readable sanitized prefix
/// plus a hash of the full identifier, so ids containing path separators or other
/// special characters can neither escape a root directory nor collide after
/// sanitization. Components that key on-disk state by caller-supplied ids (checkpoint
/// stores, approval inboxes, sandbox workspaces) share this mapping.
/// </summary>
public static class SafePathNames
{
    /// <summary>Sanitized identifier prefix length kept for readability.</summary>
    private const int PrefixLength = 48;

    /// <summary>Returns the file-system-safe name for <paramref name="id"/>.</summary>
    public static string For(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];

        Span<char> prefix = stackalloc char[Math.Min(id.Length, PrefixLength)];
        for (var i = 0; i < prefix.Length; i++)
        {
            var c = id[i];
            prefix[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }

        return $"{prefix}-{hash}";
    }
}
