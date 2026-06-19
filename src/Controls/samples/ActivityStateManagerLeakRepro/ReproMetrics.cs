namespace ActivityStateManagerLeakRepro;

internal static class ReproMetrics
{
	const string ActivityStateManagerListenerName = "Microsoft.Maui.ApplicationModel.ActivityLifecycleContextListener";

	static readonly object Sync = new();
	static readonly List<WeakReference> ActivityStateManagerListenerReferences = new();
	static long _totalCallbackRegistrations;
	static long _totalCallbackUnregistrations;
	static long _activityStateManagerRegistrations;
	static long _activityStateManagerUnregistrations;
	static long _actualLifecycleCallbackEvents;

	public static void RecordCallbackRegistration(object? callback)
	{
		Interlocked.Increment(ref _totalCallbackRegistrations);

		if (!IsActivityStateManagerListener(callback))
			return;

		Interlocked.Increment(ref _activityStateManagerRegistrations);

		lock (Sync)
			ActivityStateManagerListenerReferences.Add(new WeakReference(callback));
	}

	public static void RecordCallbackUnregistration(object? callback)
	{
		Interlocked.Increment(ref _totalCallbackUnregistrations);

		if (IsActivityStateManagerListener(callback))
			Interlocked.Increment(ref _activityStateManagerUnregistrations);
	}

	public static void RecordActualLifecycleCallbackEvent() =>
		Interlocked.Increment(ref _actualLifecycleCallbackEvents);

	public static async Task<ReproSnapshot> TakeSnapshotAfterCollectionAsync()
	{
		await Task.Delay(100);
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		await Task.Delay(100);

		return new ReproSnapshot(
			Interlocked.Read(ref _totalCallbackRegistrations),
			Interlocked.Read(ref _totalCallbackUnregistrations),
			Interlocked.Read(ref _activityStateManagerRegistrations),
			Interlocked.Read(ref _activityStateManagerUnregistrations),
			CountAliveActivityStateManagerListeners(),
			Interlocked.Read(ref _actualLifecycleCallbackEvents),
			GC.GetTotalMemory(forceFullCollection: true),
			TryGetWorkingSet());
	}

	static bool IsActivityStateManagerListener(object? callback) =>
		string.Equals(callback?.GetType().FullName, ActivityStateManagerListenerName, StringComparison.Ordinal);

	static int CountAliveActivityStateManagerListeners()
	{
		lock (Sync)
		{
			var alive = 0;

			foreach (var reference in ActivityStateManagerListenerReferences)
			{
				if (reference.IsAlive)
					alive++;
			}

			return alive;
		}
	}

	static long TryGetWorkingSet()
	{
		try
		{
			return Environment.WorkingSet;
		}
		catch
		{
			return 0;
		}
	}

	public static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : string.Empty;
		var value = Math.Abs(bytes);

		if (value >= 1024L * 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GB";

		if (value >= 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d:0.0} MB";

		if (value >= 1024L)
			return $"{sign}{value / 1024d:0.0} KB";

		return $"{sign}{value} B";
	}
}

internal sealed record ReproSnapshot(
	long TotalCallbackRegistrations,
	long TotalCallbackUnregistrations,
	long ActivityStateManagerRegistrations,
	long ActivityStateManagerUnregistrations,
	int ActivityStateManagerListenersAlive,
	long ActualLifecycleCallbackEvents,
	long ManagedBytes,
	long WorkingSetBytes)
{
	public long ActivityStateManagerRegistrationsSince(ReproSnapshot baseline) =>
		ActivityStateManagerRegistrations - baseline.ActivityStateManagerRegistrations;

	public long ActivityStateManagerUnregistrationsSince(ReproSnapshot baseline) =>
		ActivityStateManagerUnregistrations - baseline.ActivityStateManagerUnregistrations;

	public long ActualLifecycleCallbackEventsSince(ReproSnapshot baseline) =>
		ActualLifecycleCallbackEvents - baseline.ActualLifecycleCallbackEvents;
}

internal sealed record ReproReport(
	LeakRunOptions Options,
	ReproSnapshot Baseline,
	ReproSnapshot Final,
	long SubscriberInvocations,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var listenerRegistrationsDuringRun = Final.ActivityStateManagerRegistrationsSince(Baseline);
		var listenerUnregistrationsDuringRun = Final.ActivityStateManagerUnregistrationsSince(Baseline);
		var actualLifecycleEvents = Final.ActualLifecycleCallbackEventsSince(Baseline);
		var expectedSubscriberInvocations = Options.ExpectedSubscriberInvocationsWithoutLeak(actualLifecycleEvents);
		var avoidableSubscriberInvocations = Math.Max(0, SubscriberInvocations - expectedSubscriberInvocations);
		var estimatedAvoidableWork = Options.EstimatedAvoidableWorkMilliseconds(avoidableSubscriberInvocations);
		var eventFanOut = actualLifecycleEvents == 0 ? 0 : SubscriberInvocations / (double)Options.SubscriberCount / actualLifecycleEvents;

		return string.Join(Environment.NewLine,
			"ActivityStateManager lifecycle callback leak repro",
			$"Activity recreations: {Options.RecreateCount} in {Elapsed:mm\\:ss}",
			$"Realistic app subscribers: {Options.SubscriberCount}",
			$"Estimated work per subscriber notification: {Options.EstimatedWorkMillisecondsPerSubscriber} ms",
			string.Empty,
			"Leak evidence:",
			$"  ActivityStateManager listener registrations during run: {listenerRegistrationsDuringRun}",
			$"  ActivityStateManager listener unregisters during run: {listenerUnregistrationsDuringRun}",
			$"  ActivityStateManager listeners alive after full GC: {Final.ActivityStateManagerListenersAlive}",
			$"  Total ActivityStateManager listener registrations since process start: {Final.ActivityStateManagerRegistrations}",
			string.Empty,
			"Lifecycle fan-out:",
			$"  Actual Android lifecycle callback events: {actualLifecycleEvents}",
			$"  Platform.ActivityStateChanged subscriber invocations: {SubscriberInvocations}",
			$"  Expected subscriber invocations without leaked listeners: {expectedSubscriberInvocations}",
			$"  Avoidable subscriber invocations: {avoidableSubscriberInvocations}",
			$"  Observed Platform.ActivityStateChanged event multiplier: {eventFanOut:0.0}x",
			$"  Estimated avoidable app work: {FormatDuration(estimatedAvoidableWork)}",
			string.Empty,
			"Memory after full GC:",
			$"  Managed heap delta: {ReproMetrics.FormatBytes(Final.ManagedBytes - Baseline.ManagedBytes)}",
			$"  Working set delta: {ReproMetrics.FormatBytes(Final.WorkingSetBytes - Baseline.WorkingSetBytes)}");
	}

	static string FormatDuration(long milliseconds)
	{
		if (milliseconds >= 60 * 60 * 1000)
			return $"{milliseconds / 1000d / 60d / 60d:0.0} hours";

		if (milliseconds >= 60 * 1000)
			return $"{milliseconds / 1000d / 60d:0.0} minutes";

		if (milliseconds >= 1000)
			return $"{milliseconds / 1000d:0.0} seconds";

		return $"{milliseconds} ms";
	}
}
