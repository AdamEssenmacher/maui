using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Devices.Sensors;

namespace AccelerometerEventRegressionRepro;

internal sealed class AccelerometerProbe
{
	const int ScreenStatePayloadBytes = 1_048_576;

	public ProbeReport Run()
	{
		if (Accelerometer.IsMonitoring)
			throw new InvalidOperationException("Stop the accelerometer before running this deterministic probe.");

		return new ProbeReport(
			Accelerometer.Default.GetType().FullName ?? Accelerometer.Default.GetType().Name,
			ProbeExactCompositeRemovalRetention());
	}

	static ExactCompositeRemovalProbeResult ProbeExactCompositeRemovalRetention()
	{
		var scenario = AddTwoCompositesAndRemoveFirst();
		ForceFullCollection();

		var removedScreenAlive = IsAlive(scenario.RemovedScreen);
		var removedPayloadAlive = IsAlive(scenario.RemovedPayload);

		// Main still contains the second composite. The affected weak source instead
		// contains the first one. Both have the same final target and method, so this
		// operand removes whichever composite remains and leaves the source clean.
		EventHandler<AccelerometerChangedEventArgs> cleanup = scenario.ActiveScreen.OnReadingChanged;
		cleanup += scenario.AppScopedService.OnReadingChanged;
		Accelerometer.ReadingChanged -= cleanup;

		ForceFullCollection();
		var removedScreenCollectedAfterCleanup = !IsAlive(scenario.RemovedScreen);
		var removedPayloadCollectedAfterCleanup = !IsAlive(scenario.RemovedPayload);

		GC.KeepAlive(scenario.ActiveScreen);
		GC.KeepAlive(scenario.AppScopedService);

		return new ExactCompositeRemovalProbeResult(
			removedScreenAlive,
			removedPayloadAlive,
			removedScreenCollectedAfterCleanup,
			removedPayloadCollectedAfterCleanup,
			scenario.PayloadBytes);
	}

	[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
	static ExactCompositeRemovalScenario AddTwoCompositesAndRemoveFirst()
	{
		var removedScreen = new SensorSubscriber(new ScreenState(ScreenStatePayloadBytes));
		var activeScreen = new SensorSubscriber();
		var appScopedService = new SensorSubscriber();

		EventHandler<AccelerometerChangedEventArgs> firstComposite = removedScreen.OnReadingChanged;
		firstComposite += appScopedService.OnReadingChanged;

		EventHandler<AccelerometerChangedEventArgs> secondComposite = activeScreen.OnReadingChanged;
		secondComposite += appScopedService.OnReadingChanged;

		Accelerometer.ReadingChanged += firstComposite;
		Accelerometer.ReadingChanged += secondComposite;
		Accelerometer.ReadingChanged -= firstComposite;

		return new ExactCompositeRemovalScenario(
			new WeakReference<SensorSubscriber>(removedScreen),
			new WeakReference<ScreenState>(removedScreen.ScreenState!),
			removedScreen.ScreenState!.PayloadBytes,
			activeScreen,
			appScopedService);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static bool IsAlive<T>(WeakReference<T> reference)
		where T : class =>
		reference.TryGetTarget(out _);

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void ForceFullCollection()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			GC.WaitForPendingFinalizers();
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			Thread.Sleep(10);
		}
	}

	sealed class SensorSubscriber
	{
		public SensorSubscriber(ScreenState? screenState = null) =>
			ScreenState = screenState;

		public ScreenState? ScreenState { get; }

		public void OnReadingChanged(object? sender, AccelerometerChangedEventArgs args)
		{
		}
	}

	sealed class ScreenState
	{
		readonly byte[] _cachedPayload;

		public ScreenState(int payloadBytes)
		{
			_cachedPayload = GC.AllocateUninitializedArray<byte>(payloadBytes);
			_cachedPayload[0] = 1;
			_cachedPayload[^1] = 1;
		}

		public int PayloadBytes => _cachedPayload.Length;
	}

	sealed record ExactCompositeRemovalScenario(
		WeakReference<SensorSubscriber> RemovedScreen,
		WeakReference<ScreenState> RemovedPayload,
		int PayloadBytes,
		SensorSubscriber ActiveScreen,
		SensorSubscriber AppScopedService);
}

internal sealed record ExactCompositeRemovalProbeResult(
	bool RemovedScreenAliveAfterExactUnsubscribe,
	bool RemovedPayloadAliveAfterExactUnsubscribe,
	bool RemovedScreenCollectedAfterSourceCleanup,
	bool RemovedPayloadCollectedAfterSourceCleanup,
	int PayloadBytes);

internal sealed record ProbeReport(
	string ImplementationType,
	ExactCompositeRemovalProbeResult ExactCompositeRemoval)
{
	public bool RemovedGraphRetainedAfterExactUnsubscribe =>
		ExactCompositeRemoval.RemovedScreenAliveAfterExactUnsubscribe &&
		ExactCompositeRemoval.RemovedPayloadAliveAfterExactUnsubscribe;

	public bool RemovedGraphReleasedAfterSourceCleanup =>
		ExactCompositeRemoval.RemovedScreenCollectedAfterSourceCleanup &&
		ExactCompositeRemoval.RemovedPayloadCollectedAfterSourceCleanup;

	public bool AffectedImplementationConfirmed =>
		RemovedGraphRetainedAfterExactUnsubscribe &&
		RemovedGraphReleasedAfterSourceCleanup;

	public int ExitCode => AffectedImplementationConfirmed ? 0 : 2;

	public string ToText()
	{
		var result = AffectedImplementationConfirmed
			? "AFFECTED RETENTION REGRESSION CONFIRMED"
			: "AFFECTED RETENTION SIGNATURE NOT PRESENT";

		var builder = new StringBuilder();
		builder.AppendLine("Accelerometer exact-unsubscribe retention repro");
		builder.AppendLine($"UTC: {DateTime.UtcNow:O}");
		builder.AppendLine($"Implementation: {ImplementationType}");
		builder.AppendLine();
		builder.AppendLine($"RESULT: {result}");
		builder.AppendLine();
		builder.AppendLine("Scenario");
		builder.AppendLine("  added:   [removed screen, shared app-scoped service]");
		builder.AppendLine("  added:   [active screen,  shared app-scoped service]");
		builder.AppendLine("  removed: exact first composite");
		builder.AppendLine();
		builder.AppendLine("After exact -= and four full GCs");
		builder.AppendLine($"  removed screen alive: {ExactCompositeRemoval.RemovedScreenAliveAfterExactUnsubscribe}");
		builder.AppendLine($"  reachable {ExactCompositeRemoval.PayloadBytes:N0}-byte screen state alive: {ExactCompositeRemoval.RemovedPayloadAliveAfterExactUnsubscribe}");
		builder.AppendLine($"  removed graph persistently retained: {RemovedGraphRetainedAfterExactUnsubscribe}");
		builder.AppendLine();
		builder.AppendLine("After removing the remaining event-source entry and four more full GCs");
		builder.AppendLine($"  removed screen collected: {ExactCompositeRemoval.RemovedScreenCollectedAfterSourceCleanup}");
		builder.AppendLine($"  reachable screen state collected: {ExactCompositeRemoval.RemovedPayloadCollectedAfterSourceCleanup}");
		builder.AppendLine($"  event source confirmed as retaining root: {AffectedImplementationConfirmed}");
		builder.AppendLine();
		builder.AppendLine("Expected affected signature:");
		builder.AppendLine("  removed screen alive=True;");
		builder.AppendLine("  reachable screen state alive=True;");
		builder.AppendLine("  both collect after source cleanup=True.");

		return builder.ToString();
	}
}
