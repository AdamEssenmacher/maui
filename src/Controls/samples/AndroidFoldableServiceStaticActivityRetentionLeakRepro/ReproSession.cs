#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;
using Android.App;
using Microsoft.Maui.Foldable;

namespace AndroidFoldableServiceStaticActivityRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveActivities,
	int AlivePayloads,
	long RetainedPayloadBytes,
	string? CurrentServiceType,
	string? CurrentServiceActivityType);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveActivities == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveActivities == 1 &&
		Current.AlivePayloads == 1 &&
		Current.CurrentServiceType == FoldableReflection.FoldableServiceType.FullName &&
		Current.CurrentServiceActivityType == typeof(ProbeActivity).FullName;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidFoldableServiceStaticActivityRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per destroyed Activity: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		var payloadBudget = (long)PayloadBytes * stats.Attempts;
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  DualScreenInfo.Current service: {stats.CurrentServiceType ?? "<null>"}",
			$"  service _mainActivity: {stats.CurrentServiceActivityType ?? "<null>"}",
			$"  destroyed activity instances alive after full GC: {stats.AliveActivities}/{stats.Attempts}",
			$"  activity payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / payloadBudget:0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

internal static class ReproSession
{
	const int Attempts = 2;
	const int PayloadBytes = 80 * 1024 * 1024;

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		FoldableReflection.ClearCurrentFoldableService();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear DualScreenInfo.Current foldable service after Activity teardown",
			clearStaticServiceAfterInit: true);

		var current = await RunScenarioAsync(
			"current: DualScreenInfo.Current keeps last FoldableService with _mainActivity",
			clearStaticServiceAfterInit: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool clearStaticServiceAfterInit)
	{
		FoldableReflection.ClearCurrentFoldableService();
		ForceFullGc();

		var activityRefs = new List<WeakReference<ProbeActivity>>(Attempts);
		var payloadRefs = new List<WeakReference<ActivityPayload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDestroyedActivityService(
				clearStaticServiceAfterInit,
				activityRefs,
				payloadRefs,
				i);

			if (i % 2 == 0)
				await Task.Yield();
		}

		await Task.Yield();
		ForceFullGc();

		var aliveActivities = activityRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var service = FoldableReflection.GetCurrentFoldableService();
		var serviceActivityType = FoldableReflection.GetServiceMainActivity(service)?.GetType().FullName;

		if (clearStaticServiceAfterInit)
			FoldableReflection.ClearCurrentFoldableService();

		return new RunStats(
			name,
			Attempts,
			aliveActivities,
			alivePayloads,
			(long)alivePayloads * PayloadBytes,
			service?.GetType().FullName,
			serviceActivityType);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDestroyedActivityService(
		bool clearStaticServiceAfterInit,
		List<WeakReference<ProbeActivity>> activityRefs,
		List<WeakReference<ActivityPayload>> payloadRefs,
		int attempt)
	{
		var payload = new ActivityPayload(attempt, PayloadBytes);
		var activity = new ProbeActivity(payload);
		var service = FoldableReflection.CreateFoldableService();

		FoldableReflection.SetServiceMainActivity(service, activity);
		FoldableReflection.SetCurrentFoldableService(service);

		activityRefs.Add(new WeakReference<ProbeActivity>(activity));
		payloadRefs.Add(new WeakReference<ActivityPayload>(payload));

		if (clearStaticServiceAfterInit)
			FoldableReflection.ClearCurrentFoldableService();

		activity.Dispose();
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
}

internal static class FoldableReflection
{
	public static readonly Type FoldableServiceType =
		typeof(DualScreenInfo).Assembly.GetType("Microsoft.Maui.Foldable.FoldableService", throwOnError: true)
		?? throw new TypeLoadException("Microsoft.Maui.Foldable.FoldableService");

	static readonly FieldInfo ServiceMainActivityField =
		FoldableServiceType.GetField("_mainActivity", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(FoldableServiceType.FullName, "_mainActivity");

	static readonly MethodInfo DualScreenInfoSetFoldableService =
		typeof(DualScreenInfo).GetMethod("SetFoldableService", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(DualScreenInfo).FullName, "SetFoldableService");

	static readonly FieldInfo DualScreenInfoServiceField =
		typeof(DualScreenInfo).GetField("_dualScreenService", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(DualScreenInfo).FullName, "_dualScreenService");

	public static object CreateFoldableService()
	{
		return Activator.CreateInstance(FoldableServiceType, nonPublic: true)
			?? throw new InvalidOperationException("Could not create FoldableService.");
	}

	public static void SetServiceMainActivity(object service, Activity activity)
	{
		ServiceMainActivityField.SetValue(service, activity);
	}

	public static Activity? GetServiceMainActivity(object? service)
	{
		return service == null ? null : ServiceMainActivityField.GetValue(service) as Activity;
	}

	public static void SetCurrentFoldableService(object? service)
	{
		DualScreenInfoSetFoldableService.Invoke(DualScreenInfo.Current, new[] { service });
	}

	public static object? GetCurrentFoldableService()
	{
		return DualScreenInfoServiceField.GetValue(DualScreenInfo.Current);
	}

	public static void ClearCurrentFoldableService()
	{
		SetCurrentFoldableService(null);
	}
}

public sealed class ProbeActivity : Activity
{
	readonly ActivityPayload _payload;

	public ProbeActivity(ActivityPayload payload)
	{
		_payload = payload;
	}
}

public sealed class ActivityPayload
{
	public ActivityPayload(int attempt, int byteCount)
	{
		Name = $"Destroyed foldable Activity payload {attempt:00}";
		Bytes = new byte[byteCount];
		Bytes[0] = (byte)(attempt % 251);
		Bytes[^1] = (byte)((attempt + 1) % 251);
	}

	public string Name { get; }

	public byte[] Bytes { get; }
}
