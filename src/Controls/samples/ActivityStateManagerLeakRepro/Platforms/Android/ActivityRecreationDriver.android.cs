namespace ActivityStateManagerLeakRepro;

internal sealed record RecreateProgress(
	int CompletedRecreates,
	int TotalRecreates,
	long CurrentActivityInstanceId);

internal static class ActivityRecreationDriver
{
	public static async Task RunAsync(
		LeakRunOptions options,
		IProgress<RecreateProgress> progress,
		CancellationToken token)
	{
		for (var i = 0; i < options.RecreateCount; i++)
		{
			token.ThrowIfCancellationRequested();

			var activity = await WaitForCurrentActivityAsync(token);
			var previousInstanceId = MainActivity.CurrentInstanceId;
			using var recreateTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			recreateTimeout.CancelAfter(TimeSpan.FromSeconds(30));

			var resumed = MainActivity.WaitForNextResumeAsync(previousInstanceId, recreateTimeout.Token);
			activity.RunOnUiThread(() => activity.Recreate());
			await resumed;

			progress.Report(new RecreateProgress(i + 1, options.RecreateCount, MainActivity.CurrentInstanceId));

			if (options.DelayMilliseconds > 0)
				await Task.Delay(options.DelayMilliseconds, token);
		}
	}

	static async Task<MainActivity> WaitForCurrentActivityAsync(CancellationToken token)
	{
		for (var i = 0; i < 200; i++)
		{
			token.ThrowIfCancellationRequested();

			if (MainActivity.Current is { IsFinishing: false } activity)
				return activity;

			await Task.Delay(25, token);
		}

		throw new InvalidOperationException("Timed out waiting for the current MainActivity.");
	}
}
