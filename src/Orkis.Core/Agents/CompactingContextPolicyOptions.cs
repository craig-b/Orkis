namespace Orkis.Agents;

/// <summary>Configuration for <see cref="CompactingContextPolicy"/>.</summary>
public sealed class CompactingContextPolicyOptions
{
    /// <summary>Estimated prompt tokens above which compaction kicks in.</summary>
    public long TriggerTokens { get; set; } = 60_000;

    /// <summary>Most recent messages that are never stubbed or summarized.</summary>
    public int KeepRecentMessages { get; set; } = 8;

    /// <summary>
    /// Characters kept of a tool result once it has aged out of the recent tail;
    /// longer outputs are stubbed. Old tool output is the fat of a transcript — vital
    /// for one turn, dead weight forty turns later.
    /// </summary>
    public int MaxAgedToolResultChars { get; set; } = 400;
}
