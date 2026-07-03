namespace ShellSearchResultsParentRetentionRepro;

internal static class MemorySampler
{
	public static async Task ForceFullCollectionAsync()
	{
		for (var i = 0; i < 5; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			await Task.Delay(80);
		}
	}
}
