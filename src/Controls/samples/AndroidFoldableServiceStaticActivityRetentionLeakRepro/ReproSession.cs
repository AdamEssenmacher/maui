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
	string? CurrentServiceActivityType,
	bool HasDefaultHingeSensor,
	string? HingeSensorServiceType,
	string? HingeSensorServiceActivityType);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats DualScreenInfoRoot,
	RunStats HingeSensorRoot,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveActivities == 0 &&
		Control.AlivePayloads == 0 &&
		DualScreenInfoRoot.AliveActivities == 1 &&
		DualScreenInfoRoot.AlivePayloads == 1 &&
		DualScreenInfoRoot.CurrentServiceType == FoldableReflection.FoldableServiceType.FullName &&
		DualScreenInfoRoot.CurrentServiceActivityType == typeof(ProbeActivity).FullName &&
		HingeSensorRoot.AliveActivities == 1 &&
		HingeSensorRoot.AlivePayloads == 1 &&
		HingeSensorRoot.HasDefaultHingeSensor &&
		HingeSensorRoot.HingeSensorServiceType == FoldableReflection.FoldableServiceType.FullName &&
		HingeSensorRoot.HingeSensorServiceActivityType == typeof(ProbeActivity).FullName;

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
			Format(DualScreenInfoRoot),
			string.Empty,
			Format(HingeSensorRoot),
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
			$"  DualScreenInfo service _mainActivity: {stats.CurrentServiceActivityType ?? "<null>"}",
			$"  DefaultHingeSensor set: {stats.HasDefaultHingeSensor}",
			$"  DefaultHingeSensor event service: {stats.HingeSensorServiceType ?? "<null>"}",
			$"  DefaultHingeSensor service _mainActivity: {stats.HingeSensorServiceActivityType ?? "<null>"}",
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

		FoldableReflection.ClearStaticRoots();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear DualScreenInfo and DefaultHingeSensor roots after Activity teardown",
			RootMode.ClearBothStaticRoots);

		var dualScreenInfoRoot = await RunScenarioAsync(
			"current: DualScreenInfo.Current keeps last FoldableService with _mainActivity",
			RootMode.DualScreenInfoCurrent);

		var hingeSensorRoot = await RunScenarioAsync(
			"current: static DefaultHingeSensor event keeps last FoldableService with _mainActivity",
			RootMode.DefaultHingeSensorEvent);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, dualScreenInfoRoot, hingeSensorRoot, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, RootMode mode)
	{
		FoldableReflection.ClearStaticRoots();
		ForceFullGc();

		var activityRefs = new List<WeakReference<ProbeActivity>>(Attempts);
		var payloadRefs = new List<WeakReference<ActivityPayload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDestroyedActivityService(
				mode,
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
		var currentService = FoldableReflection.GetCurrentFoldableService();
		var hingeSensorService = FoldableReflection.GetDefaultHingeSensorService();
		var currentServiceActivityType = FoldableReflection.GetServiceMainActivity(currentService)?.GetType().FullName;
		var hingeSensorServiceActivityType = FoldableReflection.GetServiceMainActivity(hingeSensorService)?.GetType().FullName;

		if (mode == RootMode.ClearBothStaticRoots)
			FoldableReflection.ClearStaticRoots();

		return new RunStats(
			name,
			Attempts,
			aliveActivities,
			alivePayloads,
			(long)alivePayloads * PayloadBytes,
			currentService?.GetType().FullName,
			currentServiceActivityType,
			FoldableReflection.GetDefaultHingeSensor() != null,
			hingeSensorService?.GetType().FullName,
			hingeSensorServiceActivityType);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDestroyedActivityService(
		RootMode mode,
		List<WeakReference<ProbeActivity>> activityRefs,
		List<WeakReference<ActivityPayload>> payloadRefs,
		int attempt)
	{
		var payload = new ActivityPayload(attempt, PayloadBytes);
		var activity = new ProbeActivity(payload);
		var service = FoldableReflection.CreateFoldableService();

		FoldableReflection.SetServiceMainActivity(service, activity);

		if (mode is RootMode.ClearBothStaticRoots or RootMode.DualScreenInfoCurrent)
			FoldableReflection.SetCurrentFoldableService(service);

		if (mode is RootMode.ClearBothStaticRoots or RootMode.DefaultHingeSensorEvent)
			FoldableReflection.SetDefaultHingeSensorRoot(service);

		activityRefs.Add(new WeakReference<ProbeActivity>(activity));
		payloadRefs.Add(new WeakReference<ActivityPayload>(payload));

		if (mode == RootMode.ClearBothStaticRoots)
			FoldableReflection.ClearStaticRoots();

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

	enum RootMode
	{
		ClearBothStaticRoots,
		DualScreenInfoCurrent,
		DefaultHingeSensorEvent
	}
}

internal static class FoldableReflection
{
	public static readonly Type FoldableServiceType =
		typeof(DualScreenInfo).Assembly.GetType("Microsoft.Maui.Foldable.FoldableService", throwOnError: true)
		?? throw new TypeLoadException("Microsoft.Maui.Foldable.FoldableService");

	static readonly Type HingeSensorType =
		typeof(DualScreenInfo).Assembly.GetType("Microsoft.Maui.Foldable.HingeSensor", throwOnError: true)
		?? throw new TypeLoadException("Microsoft.Maui.Foldable.HingeSensor");

	static readonly FieldInfo ServiceMainActivityField =
		FoldableServiceType.GetField("_mainActivity", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(FoldableServiceType.FullName, "_mainActivity");

	static readonly FieldInfo ServiceDefaultHingeSensorField =
		FoldableServiceType.GetField("DefaultHingeSensor", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(FoldableServiceType.FullName, "DefaultHingeSensor");

	static readonly FieldInfo ServiceHingeAngleChangedField =
		FoldableServiceType.GetField("_hingeAngleChanged", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(FoldableServiceType.FullName, "_hingeAngleChanged");

	static readonly FieldInfo ServiceSubscriberCountField =
		FoldableServiceType.GetField("subscriberCount", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(FoldableServiceType.FullName, "subscriberCount");

	static readonly MethodInfo ServiceDefaultHingeSensorOnSensorChanged =
		FoldableServiceType.GetMethod("DefaultHingeSensorOnSensorChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(FoldableServiceType.FullName, "DefaultHingeSensorOnSensorChanged");

	static readonly EventInfo HingeSensorOnSensorChangedEvent =
		HingeSensorType.GetEvent("OnSensorChanged", BindingFlags.Instance | BindingFlags.Public)
		?? throw new MissingMemberException(HingeSensorType.FullName, "OnSensorChanged");

	static readonly FieldInfo HingeSensorOnSensorChangedField =
		HingeSensorType.GetField("OnSensorChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(HingeSensorType.FullName, "OnSensorChanged");

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

	public static void SetDefaultHingeSensorRoot(object service)
	{
		var context = Application.Context
			?? throw new InvalidOperationException("No Android application context was available.");

		var sensor = Activator.CreateInstance(HingeSensorType, context)
			?? throw new InvalidOperationException("Could not create HingeSensor.");

		var handlerType = HingeSensorOnSensorChangedEvent.EventHandlerType
			?? throw new InvalidOperationException("Could not determine HingeSensor event handler type.");

		var handler = Delegate.CreateDelegate(handlerType, service, ServiceDefaultHingeSensorOnSensorChanged);
		HingeSensorOnSensorChangedEvent.AddEventHandler(sensor, handler);
		ServiceDefaultHingeSensorField.SetValue(null, sensor);
	}

	public static object? GetDefaultHingeSensor()
	{
		return ServiceDefaultHingeSensorField.GetValue(null);
	}

	public static object? GetDefaultHingeSensorService()
	{
		var sensor = GetDefaultHingeSensor();
		if (sensor == null)
			return null;

		var handler = HingeSensorOnSensorChangedField.GetValue(sensor) as Delegate;
		return handler?
			.GetInvocationList()
			.Select(static d => d.Target)
			.FirstOrDefault(target => target?.GetType() == FoldableServiceType);
	}

	public static void ClearStaticRoots()
	{
		SetCurrentFoldableService(null);
		ServiceDefaultHingeSensorField.SetValue(null, null);
		ServiceHingeAngleChangedField.SetValue(null, null);
		ServiceSubscriberCountField.SetValue(null, 0);
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
