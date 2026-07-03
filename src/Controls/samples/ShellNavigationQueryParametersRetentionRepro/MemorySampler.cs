namespace ShellNavigationQueryParametersRetentionRepro;

internal static class MemorySampler
{
	public static async Task ForceFullCollectionAsync()
	{
		await Task.Yield();

		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			await Task.Delay(30);
		}
	}
}
