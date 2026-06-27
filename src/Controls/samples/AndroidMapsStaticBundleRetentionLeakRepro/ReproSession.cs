#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;
using Android.App;
using Android.OS;
using Android.Runtime;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Maps.Handlers;
using MapsFormsMaps = Microsoft.Maui.Controls.FormsMaps;

namespace AndroidMapsStaticBundleRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int PayloadCount,
	int AliveBundles,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes,
	string? CompatibilityStaticBundleType,
	int CompatibilityStaticBundleKeyCount,
	string? CoreStaticBundleType,
	int CoreStaticBundleKeyCount);

public sealed record ReproReport(
	int PayloadCount,
	int PayloadBytes,
	RunStats Control,
	RunStats CompatibilityCurrent,
	RunStats CoreCurrent,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveBundles == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		CompatibilityCurrent.AliveBundles == 1 &&
		CompatibilityCurrent.AlivePayloads == PayloadCount &&
		CompatibilityCurrent.AlivePayloadByteArrays == PayloadCount &&
		CompatibilityCurrent.CompatibilityStaticBundleType == typeof(Bundle).FullName &&
		CompatibilityCurrent.CompatibilityStaticBundleKeyCount == PayloadCount &&
		CoreCurrent.AliveBundles == 1 &&
		CoreCurrent.AlivePayloads == PayloadCount &&
		CoreCurrent.AlivePayloadByteArrays == PayloadCount &&
		CoreCurrent.CoreStaticBundleType == typeof(Bundle).FullName &&
		CoreCurrent.CoreStaticBundleKeyCount == PayloadCount;

	public string ToText()
	{
		return string.Join(System.Environment.NewLine,
			"AndroidMapsStaticBundleRetentionLeakRepro",
			$"Payload entries in launch Bundle: {PayloadCount}",
			$"Payload bytes per entry: {FormatBytes(PayloadBytes)}",
			$"Total launch Bundle payload: {FormatBytes((long)PayloadBytes * PayloadCount)}",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(CompatibilityCurrent),
			string.Empty,
			Format(CoreCurrent),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		var payloadBudget = (long)PayloadBytes * PayloadCount;
		return string.Join(System.Environment.NewLine,
			$"Run: {stats.Name}",
			$"  compatibility MapRenderer.s_bundle: {stats.CompatibilityStaticBundleType ?? "<null>"}",
			$"  compatibility Bundle key count: {stats.CompatibilityStaticBundleKeyCount}",
			$"  core MapHandler.s_bundle: {stats.CoreStaticBundleType ?? "<null>"}",
			$"  core Bundle key count: {stats.CoreStaticBundleKeyCount}",
			$"  launch Bundles alive after full GC: {stats.AliveBundles}/1",
			$"  saved-state payload objects alive after full GC: {stats.AlivePayloads}/{stats.PayloadCount}",
			$"  saved-state payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.PayloadCount}",
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
	const int PayloadCount = 8;
	const int PayloadBytes = 10 * 1024 * 1024;

	public static async Task<ReproReport> RunAsync(Activity activity)
	{
		await Task.Yield();

		MapsReflection.ResetMapsState();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear both Android Maps static Bundle roots after assignment",
			RootMode.ClearBothStaticBundleRoots);

		var compatibilityCurrent = await RunScenarioAsync(
			activity,
			"current: FormsMaps.Init stores launch Bundle in compatibility MapRenderer.s_bundle",
			RootMode.CompatibilityFormsMapsInit);

		var coreCurrent = await RunScenarioAsync(
			activity,
			"current: UseMauiMaps OnCreate stores launch Bundle in core MapHandler.s_bundle",
			RootMode.CoreMapHandlerBundle);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(PayloadCount, PayloadBytes, control, compatibilityCurrent, coreCurrent, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(Activity activity, string name, RootMode mode)
	{
		MapsReflection.ResetMapsState();
		ForceFullGc();

		var payloadRefs = new List<WeakReference<SavedStatePayload>>(PayloadCount);
		var payloadByteRefs = new List<WeakReference<byte[]>>(PayloadCount);
		var bundleRef = CreateAndInitializeMaps(activity, mode, payloadRefs, payloadByteRefs);

		if (mode == RootMode.ClearBothStaticBundleRoots)
			MapsReflection.ResetMapsState();

		await Task.Yield();
		ForceFullGc();

		var compatibilityStaticBundle = MapsReflection.GetCompatibilityStaticBundle();
		var coreStaticBundle = MapsReflection.GetCoreStaticBundle();
		var aliveBundles = bundleRef.TryGetTarget(out _) ? 1 : 0;
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadByteRefs.Count(static wr => wr.TryGetTarget(out _));

		if (mode == RootMode.ClearBothStaticBundleRoots)
			MapsReflection.ResetMapsState();

		return new RunStats(
			name,
			PayloadCount,
			aliveBundles,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes,
			compatibilityStaticBundle?.GetType().FullName,
			compatibilityStaticBundle?.KeySet()?.Count ?? 0,
			coreStaticBundle?.GetType().FullName,
			coreStaticBundle?.KeySet()?.Count ?? 0);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static WeakReference<Bundle> CreateAndInitializeMaps(
		Activity activity,
		RootMode mode,
		List<WeakReference<SavedStatePayload>> payloadRefs,
		List<WeakReference<byte[]>> payloadByteRefs)
	{
		var bundle = new Bundle();
		for (var i = 0; i < PayloadCount; i++)
		{
			var payload = new SavedStatePayload(i, PayloadBytes);
			payloadRefs.Add(new WeakReference<SavedStatePayload>(payload));
			payloadByteRefs.Add(new WeakReference<byte[]>(payload.Bytes));
			bundle.PutParcelable($"saved-state-payload-{i:00}", payload);
		}

		if (mode is RootMode.ClearBothStaticBundleRoots or RootMode.CompatibilityFormsMapsInit)
			MapsFormsMaps.Init(activity, bundle);

		if (mode is RootMode.ClearBothStaticBundleRoots or RootMode.CoreMapHandlerBundle)
			MapHandler.Bundle = bundle;

		return new WeakReference<Bundle>(bundle);
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
		ClearBothStaticBundleRoots,
		CompatibilityFormsMapsInit,
		CoreMapHandlerBundle
	}
}

internal static class MapsReflection
{
	static readonly Type CompatibilityMapRendererType =
		typeof(MapsFormsMaps).Assembly.GetType("Microsoft.Maui.Controls.Compatibility.Maps.Android.MapRenderer", throwOnError: true)
		?? throw new TypeLoadException("Microsoft.Maui.Controls.Compatibility.Maps.Android.MapRenderer");

	static readonly FieldInfo CompatibilityStaticBundleField =
		CompatibilityMapRendererType.GetField("s_bundle", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(CompatibilityMapRendererType.FullName, "s_bundle");

	static readonly FieldInfo CoreStaticBundleField =
		typeof(MapHandler).GetField("s_bundle", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(MapHandler).FullName, "s_bundle");

	static readonly FieldInfo FormsMapsInitializedField =
		typeof(MapsFormsMaps).GetField("<IsInitialized>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(MapsFormsMaps).FullName, "<IsInitialized>k__BackingField");

	public static Bundle? GetCompatibilityStaticBundle()
	{
		return CompatibilityStaticBundleField.GetValue(null) as Bundle;
	}

	public static Bundle? GetCoreStaticBundle()
	{
		return CoreStaticBundleField.GetValue(null) as Bundle;
	}

	public static void ResetMapsState()
	{
		CompatibilityStaticBundleField.SetValue(null, null);
		CoreStaticBundleField.SetValue(null, null);
		FormsMapsInitializedField.SetValue(null, false);
	}
}

[Register("com.microsoft.maui.androidmapsstaticbundleretentionleakrepro.SavedStatePayload")]
public sealed class SavedStatePayload : Java.Lang.Object, IParcelable
{
	public SavedStatePayload(int index, int byteCount)
	{
		Name = $"Maps saved-state payload {index:00}";
		Bytes = new byte[byteCount];
		Bytes[0] = (byte)(index % 251);
		Bytes[^1] = (byte)((index + 1) % 251);
	}

	public string Name { get; }

	public byte[] Bytes { get; }

	public int DescribeContents()
	{
		return 0;
	}

	public void WriteToParcel(Parcel? dest, ParcelableWriteFlags flags)
	{
		dest?.WriteString(Name);
		dest?.WriteByteArray(Bytes);
	}
}
