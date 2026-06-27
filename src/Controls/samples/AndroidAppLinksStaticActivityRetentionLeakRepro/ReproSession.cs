#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Android.App;
using Microsoft.Maui.Controls.Compatibility.Platform.Android.AppLinks;

namespace AndroidAppLinksStaticActivityRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveActivities,
	int AlivePayloads,
	long RetainedPayloadBytes,
	string? StaticContextType);

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
		Current.StaticContextType == typeof(ProbeActivity).FullName;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidAppLinksStaticActivityRetentionLeakRepro",
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
			$"  AndroidAppLinks.Context: {stats.StaticContextType ?? "<null>"}",
			$"  destroyed activities alive after full GC: {stats.AliveActivities}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
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

	static readonly FieldInfo IsInitializedField =
		typeof(AndroidAppLinks).GetField("<IsInitialized>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(AndroidAppLinks), "<IsInitialized>k__BackingField");

	static readonly FieldInfo ContextField =
		typeof(AndroidAppLinks).GetField("<Context>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(AndroidAppLinks), "<Context>k__BackingField");

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		ResetAndroidAppLinks();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear AndroidAppLinks static Activity context after Init",
			clearStaticContextAfterInit: true);

		var current = await RunScenarioAsync(
			"current: AndroidAppLinks.Init keeps the first Activity in static Context",
			clearStaticContextAfterInit: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool clearStaticContextAfterInit)
	{
		ResetAndroidAppLinks();

		var activityRefs = new List<WeakReference<ProbeActivity>>(Attempts);
		var payloadRefs = new List<WeakReference<ActivityPayload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDestroyedActivity(
				clearStaticContextAfterInit,
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
		var staticContextType = AndroidAppLinks.Context?.GetType().FullName;

		if (clearStaticContextAfterInit)
			ResetAndroidAppLinks();

		return new RunStats(
			name,
			Attempts,
			aliveActivities,
			alivePayloads,
			(long)alivePayloads * PayloadBytes,
			staticContextType);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDestroyedActivity(
		bool clearStaticContextAfterInit,
		List<WeakReference<ProbeActivity>> activityRefs,
		List<WeakReference<ActivityPayload>> payloadRefs,
		int attempt)
	{
		var payload = new ActivityPayload(attempt, PayloadBytes);
		var activity = new ProbeActivity(payload);

		activityRefs.Add(new WeakReference<ProbeActivity>(activity));
		payloadRefs.Add(new WeakReference<ActivityPayload>(payload));

		AndroidAppLinks.Init(activity);

		if (clearStaticContextAfterInit)
			ResetAndroidAppLinks();

		activity.Dispose();
	}

	static void ResetAndroidAppLinks()
	{
		ContextField.SetValue(null, null);
		IsInitializedField.SetValue(null, false);
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
		Name = $"Destroyed Activity payload {attempt:00}";
		Bytes = new byte[byteCount];
		Bytes[0] = (byte)(attempt % 251);
		Bytes[^1] = (byte)((attempt + 1) % 251);
	}

	public string Name { get; }

	public byte[] Bytes { get; }
}
