using System.Runtime.InteropServices;

namespace MemoryLab.Api;

public static class LeakStore
{
    // Static root: everything in here lives as long as the process does.
    public static readonly List<LeakedPayload> Items = [];

    public static readonly List<nint> NativeBlocks = [];

    public static unsafe void AllocateNative(int bytes)
    {
        var block = NativeMemory.Alloc((nuint)bytes);
        // Touch every byte so the pages are really committed (counted in RSS / cgroup).
        new Span<byte>(block, bytes).Fill(0xAB);
        NativeBlocks.Add((nint)block);
    }

    public static unsafe void FreeNative()
    {
        foreach (var block in NativeBlocks)
            NativeMemory.Free((void*)block);
        NativeBlocks.Clear();
    }
}

public sealed record LeakedPayload(Guid Id, DateTime CreatedAt, byte[] Data);
