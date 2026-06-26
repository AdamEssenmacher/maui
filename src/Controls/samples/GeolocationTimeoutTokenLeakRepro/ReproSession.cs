using System.Reflection;
using System.Threading;
using Microsoft.Maui.ApplicationModel;

namespace GeolocationTimeoutTokenLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly MethodInfo TimeoutTokenMethod =
		typeof(MainThread).Assembly
			.GetType("Microsoft.Maui.ApplicationModel.Utils", throwOnError: true)!
			.GetMethod("TimeoutToken", BindingFlags.Static | BindingFlags.NonPublic)!;

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunControl();
		var leak = RunLeak();

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunControl()
	{
		var retainedCallerTokenSources = new List<CancellationTokenSource>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(retainedCallerTokenSources, tracked, i);

		ForceFullGc();
		var result = ScenarioResult.From("control: disposed linked token source and callback registration", retainedCallerTokenSources, tracked);
		GC.KeepAlive(retainedCallerTokenSources);
		return result;
	}

	static ScenarioResult RunLeak()
	{
		var retainedCallerTokenSources = new List<CancellationTokenSource>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(retainedCallerTokenSources, tracked, i);

		ForceFullGc();
		var result = ScenarioResult.From("leak: MAUI TimeoutToken linked source retained by caller token", retainedCallerTokenSources, tracked);
		GC.KeepAlive(retainedCallerTokenSources);
		return result;
	}

	static void CreateControlCycle(List<CancellationTokenSource> retainedCallerTokenSources, List<TrackedCycle> tracked, int cycle)
	{
		var callerTokenSource = new CancellationTokenSource();
		var payload = new LocationPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var request = new NativeLocationRequest(payload);
		var completion = new TaskCompletionSource<LocationSnapshot?>(request);

		using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(callerTokenSource.Token))
		using (linkedSource.Token.Register(Cancel))
		{
			request.Complete();
			completion.TrySetResult(request.BestLocation);
		}

		retainedCallerTokenSources.Add(callerTokenSource);
		tracked.Add(TrackedCycle.Create(cycle, request, payload, completion));

		void Cancel()
		{
			request.Stop();
			completion.TrySetResult(request.BestLocation);
		}
	}

	static void CreateLeakCycle(List<CancellationTokenSource> retainedCallerTokenSources, List<TrackedCycle> tracked, int cycle)
	{
		var callerTokenSource = new CancellationTokenSource();
		var payload = new LocationPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var request = new NativeLocationRequest(payload);
		var completion = new TaskCompletionSource<LocationSnapshot?>(request);

		var token = CreateMauiTimeoutToken(callerTokenSource.Token, TimeSpan.Zero);
		token.Register(Cancel);

		request.Complete();
		completion.TrySetResult(request.BestLocation);

		retainedCallerTokenSources.Add(callerTokenSource);
		tracked.Add(TrackedCycle.Create(cycle, request, payload, completion));

		void Cancel()
		{
			request.Stop();
			completion.TrySetResult(request.BestLocation);
		}
	}

	static CancellationToken CreateMauiTimeoutToken(CancellationToken callerToken, TimeSpan timeout)
	{
		return (CancellationToken)TimeoutTokenMethod.Invoke(null, new object[] { callerToken, timeout })!;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	internal sealed class NativeLocationRequest
	{
		readonly LocationPayload _payload;
		bool _isUpdating = true;

		public NativeLocationRequest(LocationPayload payload)
		{
			_payload = payload;
			BestLocation = new LocationSnapshot(
				47.6205 + payload.Cycle * 0.0001,
				-122.3493 - payload.Cycle * 0.0001,
				DateTimeOffset.UtcNow,
				payload.RouteCache.Count);
		}

		public LocationSnapshot BestLocation { get; }

		public void Complete()
		{
			_isUpdating = false;
		}

		public void Stop()
		{
			_isUpdating = false;
		}

		public bool IsUpdating => _isUpdating;
	}

	internal sealed class LocationPayload
	{
		public LocationPayload(int cycle, long payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			WorkspaceBytes = new byte[payloadBytes];

			for (var i = 0; i < WorkspaceBytes.Length; i += 4096)
				WorkspaceBytes[i] = (byte)(cycle + i);

			RouteCache = Enumerable.Range(1, 32)
				.Select(index => new RoutePoint(
					47.6205 + cycle * 0.0001 + index * 0.00001,
					-122.3493 - cycle * 0.0001 - index * 0.00001,
					$"active field visit {cycle + 1:000}-{index:000}"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] WorkspaceBytes { get; }

		public IReadOnlyList<RoutePoint> RouteCache { get; }
	}

	internal sealed record RoutePoint(double Latitude, double Longitude, string Label);

	internal sealed record LocationSnapshot(double Latitude, double Longitude, DateTimeOffset Timestamp, int CachedRoutePointCount);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference Request,
		WeakReference Payload,
		WeakReference Completion,
		long PayloadBytes)
	{
		public static TrackedCycle Create(int cycle, NativeLocationRequest request, LocationPayload payload, TaskCompletionSource<LocationSnapshot?> completion)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference(request),
				new WeakReference(payload),
				new WeakReference(completion),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedCallerTokenSources,
		int AliveRequests,
		int AlivePayloads,
		int AliveCompletions,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(
			string name,
			IReadOnlyList<CancellationTokenSource> retainedCallerTokenSources,
			IReadOnlyList<TrackedCycle> cycles)
		{
			var aliveRequests = 0;
			var alivePayloads = 0;
			var aliveCompletions = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.Request.IsAlive)
					aliveRequests++;

				if (cycle.Completion.IsAlive)
					aliveCompletions++;

				if (cycle.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				cycles.Count,
				retainedCallerTokenSources.Count,
				aliveRequests,
				alivePayloads,
				aliveCompletions,
				retainedPayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Cycles,
		int PayloadMegabytesPerCycle,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			Control.AliveRequests == 0 &&
			Control.AlivePayloads == 0 &&
			Control.AliveCompletions == 0 &&
			Leak.AliveRequests == Leak.TrackedCycles &&
			Leak.AlivePayloads == Leak.TrackedCycles &&
			Leak.AliveCompletions == Leak.TrackedCycles;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"GeolocationTimeoutTokenLeakRepro",
				$"Cycles: {Cycles}",
				$"Payload per cycle: {PayloadMegabytesPerCycle} MiB",
				$"GeolocationRequest timeout shape: default TimeSpan.Zero",
				$"Leak proved: {LeakProved}",
				string.Empty,
				FormatScenario(Control),
				string.Empty,
				FormatScenario(Leak),
				string.Empty,
				$"Managed heap baseline: {FormatBytes(BaselineManagedBytes)}",
				$"Managed heap final: {FormatBytes(FinalManagedBytes)}",
				$"Managed heap delta: {FormatBytes(FinalManagedBytes - BaselineManagedBytes)}");
		}

		static string FormatScenario(ScenarioResult result)
		{
			var expectedPayload = result.TrackedCycles == 0 ? 0 : result.TrackedCycles * 1024L * 1024L;
			var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

			return string.Join(Environment.NewLine,
				$"Run: {result.Name}",
				$"  tracked cycles: {result.TrackedCycles}",
				$"  retained caller token sources: {result.RetainedCallerTokenSources}",
				$"  native request objects alive after full GC: {result.AliveRequests}/{result.TrackedCycles}",
				$"  completion sources alive after full GC: {result.AliveCompletions}/{result.TrackedCycles}",
				$"  payloads alive after full GC: {result.AlivePayloads}/{result.TrackedCycles}",
				$"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
		}

		static string FormatBytes(long bytes)
		{
			var sign = bytes < 0 ? "-" : string.Empty;
			var value = Math.Abs(bytes);

			if (value >= 1024L * 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GiB";

			if (value >= 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d:0.0} MiB";

			if (value >= 1024L)
				return $"{sign}{value / 1024d:0.0} KiB";

			return $"{sign}{value} B";
		}
	}
}
