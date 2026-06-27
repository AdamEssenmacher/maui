using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using PhotosUI;
using UIKit;

namespace MediaPickerPickerRefExceptionRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 4;
	const int PayloadMegabytesPerCycle = 48;

	static readonly Type MediaPickerImplementationType =
		typeof(MediaPicker).Assembly.GetType("Microsoft.Maui.Media.MediaPickerImplementation")
		?? throw new InvalidOperationException("Could not find MediaPickerImplementation.");

	static readonly Type PhotoPickerDelegateType =
		typeof(MediaPicker).Assembly.GetType("Microsoft.Maui.Media.PhotoPickerDelegate")
		?? throw new InvalidOperationException("Could not find PhotoPickerDelegate.");

	static readonly FieldInfo PickerRefField =
		MediaPickerImplementationType.GetField("PickerRef", BindingFlags.NonPublic | BindingFlags.Static)
		?? throw new InvalidOperationException("Could not find MediaPickerImplementation.PickerRef.");

	static readonly PropertyInfo CompletedHandlerProperty =
		PhotoPickerDelegateType.GetProperty("CompletedHandler", BindingFlags.Public | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find PhotoPickerDelegate.CompletedHandler.");

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "mediapicker-pickerref-exception-retention-results.txt");

	public static ReproReport Run()
	{
		ClearPickerRef();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario("control: clear PickerRef after the failure", clearPickerRefAfterFault: true);
		var leak = RunScenario("current: failure prevents PickerRef cleanup", clearPickerRefAfterFault: false);

		ClearPickerRef();
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

	static ScenarioResult RunScenario(string name, bool clearPickerRefAfterFault)
	{
		var tracking = RunScenarioCore(clearPickerRefAfterFault);

		ForceFullGc();
		var pickerRefAssigned = PickerRefField.GetValue(null) is not null;

		if (clearPickerRefAfterFault)
			ClearPickerRef();

		return ScenarioResult.From(name, tracking.TrackedCycles, tracking.CallbackExceptions, pickerRefAssigned);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(bool clearPickerRefAfterFault)
	{
		var tracked = new List<TrackedCycle>();
		var thrownCallbacks = 0;

		for (var i = 0; i < Cycles; i++)
		{
			if (CreateFaultedPickerCycle(i, tracked, clearPickerRefAfterFault))
				thrownCallbacks++;
		}

		return new ScenarioTracking(tracked, thrownCallbacks);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static bool CreateFaultedPickerCycle(
		int cycle,
		List<TrackedCycle> tracked,
		bool clearPickerRefAfterFault)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var options = new PayloadMediaPickerOptions(cycle, payload)
		{
			Title = $"Offline field inspection package {cycle + 1}",
			MaximumWidth = 4096,
			MaximumHeight = 4096,
			CompressionQuality = 80,
			RotateImage = true
		};
		var tcs = new TaskCompletionSource<List<FileResult>>();
		var picker = new PHPickerViewController(new PHPickerConfiguration());
		var pickerDelegate = CreatePhotoPickerDelegate(_ =>
		{
			GC.KeepAlive(tcs);
			GC.KeepAlive(options.Payload);
			throw new InvalidOperationException("Simulated media result processing failure.");
		});

		picker.Delegate = pickerDelegate;
		PickerRefField.SetValue(null, picker);

		tracked.Add(TrackedCycle.Create(cycle, picker, pickerDelegate, tcs, options, payload));

		// Simulates PhotosAsync's async result-processing callback failing before it
		// completes the TCS. The awaited MediaPicker operation never reaches its trailing
		// PickerRef cleanup because that cleanup is not in a finally block.
		var threw = InvokeCompletionCallback(pickerDelegate);

		if (clearPickerRefAfterFault)
			ClearPickerRef();

		return threw && !tcs.Task.IsCompleted;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static bool InvokeCompletionCallback(PHPickerViewControllerDelegate pickerDelegate)
	{
		try
		{
			((Action<PHPickerResult[]>)CompletedHandlerProperty.GetValue(pickerDelegate)!).Invoke([]);
			return false;
		}
		catch
		{
			return true;
		}
	}

	static PHPickerViewControllerDelegate CreatePhotoPickerDelegate(Action<PHPickerResult[]> completedHandler)
	{
		var pickerDelegate = Activator.CreateInstance(PhotoPickerDelegateType)
			?? throw new InvalidOperationException("Could not create PhotoPickerDelegate.");

		CompletedHandlerProperty.SetValue(pickerDelegate, completedHandler);

		return (PHPickerViewControllerDelegate)pickerDelegate;
	}

	static void ClearPickerRef()
	{
		if (PickerRefField.GetValue(null) is UIViewController picker)
			picker.Dispose();

		PickerRefField.SetValue(null, null);
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

internal sealed class PayloadMediaPickerOptions : MediaPickerOptions
{
	public PayloadMediaPickerOptions(int cycle, LeakPayload payload)
	{
		Cycle = cycle;
		Payload = payload;
	}

	public int Cycle { get; }

	public LeakPayload Payload { get; }
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		PreviewBytes = new byte[payloadBytes];

		for (var i = 0; i < PreviewBytes.Length; i += 4096)
			PreviewBytes[i] = (byte)(cycle + i);

		AssetMetadata = Enumerable.Range(1, 20)
			.Select(index => new CapturedAssetMetadata(
				$"ASSET-{cycle + 1:000}-{index:000}",
				$"Site walkthrough image {index}",
				"Pending sync"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] PreviewBytes { get; }

	public IReadOnlyList<CapturedAssetMetadata> AssetMetadata { get; }
}

internal sealed record CapturedAssetMetadata(string Id, string Caption, string Status);

internal sealed record ScenarioTracking(IReadOnlyList<TrackedCycle> TrackedCycles, int CallbackExceptions);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference Picker,
	WeakReference PickerDelegate,
	WeakReference TaskCompletionSource,
	WeakReference Options,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		UIViewController picker,
		PHPickerViewControllerDelegate pickerDelegate,
		TaskCompletionSource<List<FileResult>> tcs,
		PayloadMediaPickerOptions options,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(picker),
			new WeakReference(pickerDelegate),
			new WeakReference(tcs),
			new WeakReference(options),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int CallbackExceptions,
	bool PickerRefAssignedAfterGc,
	int AlivePickers,
	int AliveDelegates,
	int AliveTaskCompletionSources,
	int AliveOptions,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(
		string name,
		IReadOnlyList<TrackedCycle> cycles,
		int callbackExceptions,
		bool pickerRefAssignedAfterGc)
	{
		var alivePickers = 0;
		var aliveDelegates = 0;
		var aliveTaskCompletionSources = 0;
		var aliveOptions = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.Picker.IsAlive)
				alivePickers++;
			if (cycle.PickerDelegate.IsAlive)
				aliveDelegates++;
			if (cycle.TaskCompletionSource.IsAlive)
				aliveTaskCompletionSources++;
			if (cycle.Options.IsAlive)
				aliveOptions++;
			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			callbackExceptions,
			pickerRefAssignedAfterGc,
			alivePickers,
			aliveDelegates,
			aliveTaskCompletionSources,
			aliveOptions,
			alivePayloads,
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
	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"MediaPicker PickerRef exception-retention repro",
			$"Cycles: {Cycles}",
			$"Payload per cycle: {PayloadMegabytesPerCycle} MiB",
			$"Baseline managed heap: {FormatBytes(BaselineManagedBytes)}",
			$"Final managed heap after cleanup: {FormatBytes(FinalManagedBytes)}",
			"",
			FormatScenario(Control),
			"",
			FormatScenario(Leak),
			"",
			$"Retained payload delta: {FormatBytes(Leak.RetainedPayloadBytes - Control.RetainedPayloadBytes)}");
	}

	static string FormatScenario(ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			result.Name,
			$"  callback exceptions before task completion: {result.CallbackExceptions}/{result.TrackedCycles}",
			$"  PickerRef assigned after GC: {result.PickerRefAssignedAfterGc}",
			$"  alive pickers: {result.AlivePickers}/{result.TrackedCycles}",
			$"  alive delegates: {result.AliveDelegates}/{result.TrackedCycles}",
			$"  alive TCS objects: {result.AliveTaskCompletionSources}/{result.TrackedCycles}",
			$"  alive options: {result.AliveOptions}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)}");
	}

	static string FormatBytes(long bytes)
	{
		return bytes >= 1024 * 1024
			? $"{bytes / 1024d / 1024d:0.0} MiB"
			: $"{bytes:N0} B";
	}
}
