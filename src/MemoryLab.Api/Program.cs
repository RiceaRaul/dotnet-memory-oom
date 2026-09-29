using System.Diagnostics;
using System.Runtime;
using MemoryLab.Api;
using MemoryDiagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMemoryDiagnostics(builder.Configuration);

var app = builder.Build();

app.Logger.LogInformation(
    "PID {Pid} | ServerGC {ServerGc} | GC heap limit {Limit} MB | dumps -> {DumpName}",
    Environment.ProcessId,
    GCSettings.IsServerGC,
    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024,
    Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName") ?? "(auto-dump off)");

app.MapGet("/memory", () =>
{
    var info = GC.GetGCMemoryInfo();
    using var process = Process.GetCurrentProcess();
    return new
    {
        heapLimitMb = info.TotalAvailableMemoryBytes / 1024 / 1024,
        heapSizeMb = info.HeapSizeBytes / 1024 / 1024,
        committedMb = info.TotalCommittedBytes / 1024 / 1024,
        workingSetMb = process.WorkingSet64 / 1024 / 1024,
        leakedItems = LeakStore.Items.Count,
        gen0 = GC.CollectionCount(0),
        gen1 = GC.CollectionCount(1),
        gen2 = GC.CollectionCount(2)
    };
});

// Simulates a real leak: objects added to a static collection are never released.
app.MapPost("/leak/managed", (int mb) =>
{
    for (var i = 0; i < mb; i++)
        LeakStore.Items.Add(new LeakedPayload(Guid.NewGuid(), DateTime.UtcNow, new byte[1024 * 1024]));

    return Results.Ok(new { added = mb, total = LeakStore.Items.Count });
});

// Simulates a native leak: invisible to the GC, so no OutOfMemoryException, only OOMKilled.
app.MapPost("/leak/native", (int mb) =>
{
    for (var i = 0; i < mb; i++)
        LeakStore.AllocateNative(1024 * 1024);

    return Results.Ok(new { addedMb = mb, totalNativeMb = LeakStore.NativeBlocks.Count });
});

app.MapPost("/reset", () =>
{
    LeakStore.Items.Clear();
    LeakStore.FreeNative();
    GC.Collect();
    return Results.Ok();
});

app.Run();
