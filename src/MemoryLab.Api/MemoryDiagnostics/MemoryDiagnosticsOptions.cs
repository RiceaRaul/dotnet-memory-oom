using Microsoft.Diagnostics.NETCore.Client;

namespace MemoryDiagnostics;

/// <summary>
/// Bound from the "MemoryDiagnostics" configuration section
/// (env vars: MemoryDiagnostics__IntervalSeconds, MemoryDiagnostics__DumpThresholdPercent, ...).
/// The on/off switch is MemoryDiagnostics__Enabled=1, checked in <see cref="MemoryDiagnosticsExtensions"/>.
/// </summary>
public sealed class MemoryDiagnosticsOptions
{
    public const string SectionName = "MemoryDiagnostics";

    public int IntervalSeconds { get; set; } = 15;

    /// <summary>Write a dump when usage reaches this % of the container limit. 0 = never.</summary>
    public int DumpThresholdPercent { get; set; } = 80;

    public string DumpDirectory { get; set; } = "dumps";

    /// <summary>WithHeap is enough for managed heap analysis and smaller than Full.</summary>
    public DumpType DumpType { get; set; } = DumpType.WithHeap;

    /// <summary>Used when no cgroup limit is found (e.g. running locally without Docker).</summary>
    public int FallbackLimitMb { get; set; } = 512;

    /// <summary>
    /// Terminate the process on the first OutOfMemoryException so createdump (DOTNET_DbgEnableMiniDump) writes
    /// a crash dump. Without it Kestrel turns the exception into a 500 and the process keeps running.
    /// </summary>
    public bool FailFastOnOutOfMemory { get; set; }
}
