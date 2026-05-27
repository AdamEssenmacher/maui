#if ANDROID || MONOANDROID
using AndroidRuntime = Java.Lang.Runtime;
#endif

namespace MapGeopathAppendRepro;

internal sealed record RuntimeSnapshot(
	long ManagedAllocatedBytes,
	long ManagedHeapBytes,
	long? AndroidJavaHeapBytes);

internal sealed record RuntimeImpact(
	TimeSpan InitialRenderElapsed,
	TimeSpan OffMapMutationElapsed,
	TimeSpan ReAddElapsed,
	long ManagedAllocatedBytesDelta,
	long ManagedHeapBytesDelta,
	long? AndroidJavaHeapBytesDelta)
{
	public static RuntimeImpact Empty { get; } = new(
		TimeSpan.Zero,
		TimeSpan.Zero,
		TimeSpan.Zero,
		0,
		0,
		null);

	public TimeSpan MeasuredElapsed => InitialRenderElapsed + OffMapMutationElapsed + ReAddElapsed;

	public static RuntimeImpact Create(
		RuntimeSnapshot before,
		RuntimeSnapshot after,
		TimeSpan initialRenderElapsed,
		TimeSpan offMapMutationElapsed,
		TimeSpan reAddElapsed)
	{
		return new RuntimeImpact(
			initialRenderElapsed,
			offMapMutationElapsed,
			reAddElapsed,
			after.ManagedAllocatedBytes - before.ManagedAllocatedBytes,
			after.ManagedHeapBytes - before.ManagedHeapBytes,
			SubtractNullable(after.AndroidJavaHeapBytes, before.AndroidJavaHeapBytes));
	}

	static long? SubtractNullable(long? after, long? before)
	{
		if (after is null || before is null)
			return null;

		return after.Value - before.Value;
	}
}

internal static class RuntimeMetrics
{
	public static RuntimeSnapshot Capture()
	{
		return new RuntimeSnapshot(
			GC.GetTotalAllocatedBytes(precise: true),
			GC.GetTotalMemory(forceFullCollection: false),
			GetAndroidJavaHeapBytes());
	}

	static long? GetAndroidJavaHeapBytes()
	{
#if ANDROID || MONOANDROID
		var runtime = AndroidRuntime.GetRuntime();
		if (runtime is null)
			return null;

		return runtime.TotalMemory() - runtime.FreeMemory();
#else
		return null;
#endif
	}
}
