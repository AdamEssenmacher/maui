#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;
using Android.App;
using Android.OS;
using Android.Runtime;
using Microsoft.Maui.Controls;
using MapsFormsMaps = Microsoft.Maui.Controls.FormsMaps;

namespace AndroidMapsStaticBundleRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int PayloadCount,
	int AliveBundles,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes,
	string? StaticBundleType,
	int StaticBundleKeyCount);

public sealed record ReproReport(
	int PayloadCount,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveBundles == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveBundles == 1 &&
		Current.AlivePayloads == PayloadCount &&
		Current.AlivePayloadByteArrays == PayloadCount &&
		Current.StaticBundleType == typeof(Bundle).FullName &&
		Current.StaticBundleKeyCount == PayloadCount;

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
			Format(Current),
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
			$"  static MapRenderer.s_bundle: {stats.StaticBundleType ?? "<null>"}",
			$"  static Bundle key count: {stats.StaticBundleKeyCount}",
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

		MapsReflection.ResetFormsMapsState();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear MapRenderer.s_bundle after FormsMaps.Init",
			clearStaticBundleAfterInit: true);

		var current = await RunScenarioAsync(
			activity,
			"current: FormsMaps.Init stores launch Bundle in static MapRenderer.s_bundle",
			clearStaticBundleAfterInit: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(PayloadCount, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(Activity activity, string name, bool clearStaticBundleAfterInit)
	{
		MapsReflection.ResetFormsMapsState();
		ForceFullGc();

		var payloadRefs = new List<WeakReference<SavedStatePayload>>(PayloadCount);
		var payloadByteRefs = new List<WeakReference<byte[]>>(PayloadCount);
		var bundleRef = CreateAndInitializeMaps(activity, payloadRefs, payloadByteRefs);

		if (clearStaticBundleAfterInit)
			MapsReflection.ResetFormsMapsState();

		await Task.Yield();
		ForceFullGc();

		var staticBundle = MapsReflection.GetStaticBundle();
		var aliveBundles = bundleRef.TryGetTarget(out _) ? 1 : 0;
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadByteRefs.Count(static wr => wr.TryGetTarget(out _));

		if (clearStaticBundleAfterInit)
			MapsReflection.ResetFormsMapsState();

		return new RunStats(
			name,
			PayloadCount,
			aliveBundles,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes,
			staticBundle?.GetType().FullName,
			staticBundle?.KeySet()?.Count ?? 0);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static WeakReference<Bundle> CreateAndInitializeMaps(
		Activity activity,
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

		MapsFormsMaps.Init(activity, bundle);
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
}

internal static class MapsReflection
{
	static readonly Type MapRendererType =
		typeof(MapsFormsMaps).Assembly.GetType("Microsoft.Maui.Controls.Compatibility.Maps.Android.MapRenderer", throwOnError: true)
		?? throw new TypeLoadException("Microsoft.Maui.Controls.Compatibility.Maps.Android.MapRenderer");

	static readonly FieldInfo StaticBundleField =
		MapRendererType.GetField("s_bundle", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(MapRendererType.FullName, "s_bundle");

	static readonly FieldInfo FormsMapsInitializedField =
		typeof(MapsFormsMaps).GetField("<IsInitialized>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(MapsFormsMaps).FullName, "<IsInitialized>k__BackingField");

	public static Bundle? GetStaticBundle()
	{
		return StaticBundleField.GetValue(null) as Bundle;
	}

	public static void ResetFormsMapsState()
	{
		StaticBundleField.SetValue(null, null);
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
