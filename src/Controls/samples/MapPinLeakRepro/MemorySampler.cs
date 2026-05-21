using System.Diagnostics;

namespace MapPinLeakRepro;

internal sealed record MemorySnapshot(long ManagedBytes, long GcHeapBytes, long ResidentBytes, long WorkingSetBytes)
{
	public static MemorySnapshot Empty { get; } = new(0, 0, 0, 0);
}

internal static class MemorySampler
{
	public static async Task ForceFullCollectionAsync()
	{
		await Task.Yield();

		for (var i = 0; i < 3; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			await Task.Delay(25);
		}
	}

	public static async Task<MemorySnapshot> TakeAfterCollectionAsync()
	{
		await ForceFullCollectionAsync();

		return new MemorySnapshot(
			GC.GetTotalMemory(forceFullCollection: false),
			GC.GetGCMemoryInfo().HeapSizeBytes,
			GetResidentMemoryBytes(),
			GetWorkingSetBytes());
	}

	static long GetWorkingSetBytes()
	{
		try
		{
			return Process.GetCurrentProcess().WorkingSet64;
		}
		catch
		{
			return 0;
		}
	}

	static long GetResidentMemoryBytes()
	{
#if ANDROID
		try
		{
			var memoryInfo = new Android.OS.Debug.MemoryInfo();
			Android.OS.Debug.GetMemoryInfo(memoryInfo);
			return memoryInfo.TotalPss * 1024L;
		}
		catch
		{
		}
#endif

		return GetWorkingSetBytes();
	}
}
