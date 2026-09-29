using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryDiagnostics;

/// <summary>
/// Logs memory usage periodically and writes a dump before the kernel OOM-kills the container.
/// OOMKilled is a SIGKILL: nothing runs after it, so the dump has to be taken while there is still headroom.
/// </summary>
public sealed class MemoryMonitor(IOptions<MemoryDiagnosticsOptions> options, ILogger<MemoryMonitor> logger)
    : BackgroundService
{
    private readonly MemoryDiagnosticsOptions _options = options.Value;
    private bool _dumpTaken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureWritable(_options.DumpDirectory, "MemoryDiagnostics:DumpDirectory");
        // createdump does not create the target folder of DOTNET_DbgMiniDumpName.
        var crashDumpDirectory = Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName"));
        if (!string.IsNullOrEmpty(crashDumpDirectory))
            EnsureWritable(crashDumpDirectory, "DOTNET_DbgMiniDumpName");

        var (limitBytes, limitSource) = CgroupMemory.GetLimit(_options.FallbackLimitMb);
        logger.LogInformation(
            "MemoryMonitor started: limit {LimitMb} MB ({LimitSource}), interval {Interval}s, dump at {Threshold}%",
            limitBytes / Mb, limitSource, _options.IntervalSeconds, _options.DumpThresholdPercent);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalSeconds));
        do
        {
            try
            {
                Check(limitBytes);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MemoryMonitor check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Check(long limitBytes)
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();

        // Same value Kubernetes compares against the limit; falls back to RSS outside a container.
        var usedBytes = CgroupMemory.GetWorkingSet() ?? process.WorkingSet64;
        var percent = (int)(usedBytes * 100 / limitBytes);

        logger.LogInformation(
            "Memory {Percent}% | used {UsedMb} MB / {LimitMb} MB | RSS {RssMb} MB | GC heap {HeapMb} MB | " +
            "GC committed {CommittedMb} MB | threads {Threads} | gen0/1/2 {Gen0}/{Gen1}/{Gen2}",
            percent, usedBytes / Mb, limitBytes / Mb, process.WorkingSet64 / Mb, gc.HeapSizeBytes / Mb,
            gc.TotalCommittedBytes / Mb, process.Threads.Count,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        if (_options.DumpThresholdPercent <= 0)
            return;

        if (percent >= _options.DumpThresholdPercent && !_dumpTaken)
        {
            _dumpTaken = true;
            WriteDump(percent);
        }
        else if (percent < _options.DumpThresholdPercent - 10)
        {
            // Re-arm once memory has clearly dropped, so a later spike can be captured too.
            _dumpTaken = false;
        }
    }

    // Diagnostics must never take the app down: a missing volume only produces a warning.
    private void EnsureWritable(string directory, string setting)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Dump folder {Directory} ({Setting}) is not writable, dumps will fail ({Error}). " +
                "Mount a volume there (e.g. emptyDir on /dumps).",
                Path.GetFullPath(directory), setting, ex.Message);
        }
    }

    private void WriteDump(int percent)
    {
        var path = Path.GetFullPath(Path.Combine(
            _options.DumpDirectory,
            $"memory_{percent}pct_{Environment.ProcessId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.dmp"));

        logger.LogWarning("Memory at {Percent}% of limit, writing {DumpType} dump to {Path}",
            percent, _options.DumpType, path);

        var stopwatch = Stopwatch.StartNew();
        new DiagnosticsClient(Environment.ProcessId).WriteDump(_options.DumpType, path);

        logger.LogWarning("Dump written in {Elapsed} ms: {Path} ({SizeMb} MB)",
            stopwatch.ElapsedMilliseconds, path, new FileInfo(path).Length / Mb);
    }

    private const long Mb = 1024 * 1024;
}
