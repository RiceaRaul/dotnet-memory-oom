namespace MemoryDiagnostics;

/// <summary>
/// Reads the container memory limit and usage from cgroup v2, or cgroup v1 on older nodes
/// (e.g. OpenShift clusters upgraded from 4.13 or earlier). The files are world-readable, no root needed.
/// </summary>
public static class CgroupMemory
{
    private const string V2 = "/sys/fs/cgroup";
    private const string V1 = "/sys/fs/cgroup/memory";

    // cgroup v1 reports "no limit" as a huge number (0x7FFFFFFFFFFFF000) instead of "max".
    private const long V1Unlimited = long.MaxValue / 2;

    public static (long Bytes, string Source) GetLimit(int fallbackMb)
    {
        var v2 = ReadText(V2, "memory.max");
        if (v2 is not null && v2 != "max" && long.TryParse(v2, out var limitV2))
            return (limitV2, "cgroup v2 memory.max");

        if (long.TryParse(ReadText(V1, "memory.limit_in_bytes"), out var limitV1) && limitV1 < V1Unlimited)
            return (limitV1, "cgroup v1 memory.limit_in_bytes");

        return (fallbackMb * 1024L * 1024, "fallback config");
    }

    /// <summary>
    /// Usage minus inactive file cache: the "working set" that kubelet reports and the OOM killer acts on.
    /// Null when not running inside a container.
    /// </summary>
    public static long? GetWorkingSet()
    {
        if (long.TryParse(ReadText(V2, "memory.current"), out var currentV2))
            return currentV2 - ReadStat(V2, "inactive_file");

        if (long.TryParse(ReadText(V1, "memory.usage_in_bytes"), out var usageV1))
            return usageV1 - ReadStat(V1, "total_inactive_file");

        return null;
    }

    private static long ReadStat(string root, string key) =>
        ReadText(root, "memory.stat")?
            .Split('\n')
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length == 2 && parts[0] == key)
            .Select(parts => long.Parse(parts[1]))
            .FirstOrDefault() ?? 0;

    private static string? ReadText(string root, string file)
    {
        try
        {
            return File.ReadAllText(Path.Combine(root, file)).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
