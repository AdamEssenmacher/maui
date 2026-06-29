#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using MapKit;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Maps.Handlers;
using ObjCRuntime;

namespace IosMapPinAnnotationTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 512;
	internal const int PayloadKiBPerAnnotationString = 32;
	internal const int AnnotationStringSlotsPerCycle = 2;

	const long PayloadBytesPerAnnotationString = PayloadKiBPerAnnotationString * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly IntPtr TitleSelector = Selector.GetHandle("title");
	static readonly IntPtr SubtitleSelector = Selector.GetHandle("subtitle");
	static readonly List<IReadOnlyList<RetainedNativeAnnotation>> RetainedNativeAnnotations = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-mappin-annotation-text-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS MapPin annotation text retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native MKPointAnnotation title/subtitle before handler disconnect",
			clearNativeTextBeforeDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: MapPinHandler disconnect leaves native title/subtitle assigned",
			clearNativeTextBeforeDisconnect: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeAnnotations);

		return new ReproReport(
			Cycles,
			PayloadKiBPerAnnotationString,
			AnnotationStringSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		bool clearNativeTextBeforeDisconnect)
	{
		var tracking = RunScenarioCore(name, clearNativeTextBeforeDisconnect);
		RetainedNativeAnnotations.Add(tracking.NativeAnnotations);
		ForceFullGc();

		return ScenarioResult.From(name, tracking.NativeAnnotations, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(
		string name,
		bool clearNativeTextBeforeDisconnect)
	{
		var nativeAnnotations = new List<RetainedNativeAnnotation>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 64 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateDisconnectedPinCycle(i, nativeAnnotations, tracked, clearNativeTextBeforeDisconnect);
		}

		return new ScenarioTracking(nativeAnnotations, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisconnectedPinCycle(
		int cycle,
		List<RetainedNativeAnnotation> nativeAnnotations,
		List<TrackedCycle> tracked,
		bool clearNativeTextBeforeDisconnect)
	{
		var pin = new Pin
		{
			Label = CreateAnnotationPayload(cycle, "label"),
			Address = CreateAnnotationPayload(cycle, "address"),
			Location = new Location(47.6062 + cycle * 0.00001, -122.3321 - cycle * 0.00001),
			Type = PinType.Place
		};

		var handler = new MapPinHandler();
		handler.SetVirtualView(pin);

		if (handler.PlatformView is not MKPointAnnotation annotation)
			throw new InvalidOperationException($"Expected {nameof(MKPointAnnotation)} from {nameof(MapPinHandler)}.");

		if (CountPayloadTextSlots(annotation) != AnnotationStringSlotsPerCycle)
			throw new InvalidOperationException("MapPinHandler did not assign all expected native title/subtitle string payloads.");

		var retainedAnnotation = RetainNativeAnnotation(annotation);

		if (clearNativeTextBeforeDisconnect)
			ClearNativeText(annotation);

		((IElementHandler)handler).DisconnectHandler();

		nativeAnnotations.Add(retainedAnnotation);
		tracked.Add(TrackedCycle.Create(cycle, handler, pin));
	}

	static void ClearNativeText(MKPointAnnotation annotation)
	{
		annotation.Title = null;
		annotation.Subtitle = null;
	}

	static int CountPayloadTextSlots(MKPointAnnotation annotation)
	{
		var count = 0;

		if (EstimateStringBytes(annotation.Title) >= PayloadBytesPerAnnotationString * 0.95)
			count++;

		if (EstimateStringBytes(annotation.Subtitle) >= PayloadBytesPerAnnotationString * 0.95)
			count++;

		return count;
	}

	static NativeAnnotationSnapshot GetNativeAnnotationSnapshot(RetainedNativeAnnotation retainedAnnotation)
	{
		var title = GetNativeString(retainedAnnotation.Handle, TitleSelector);
		var subtitle = GetNativeString(retainedAnnotation.Handle, SubtitleSelector);
		var slots = 0;
		var bytes = 0L;

		AccumulatePayloadSlot(title, ref slots, ref bytes);
		AccumulatePayloadSlot(subtitle, ref slots, ref bytes);

		return new NativeAnnotationSnapshot(Alive: retainedAnnotation.Handle != IntPtr.Zero, PayloadSlots: slots, EstimatedBytes: bytes);
	}

	static void AccumulatePayloadSlot(string? value, ref int slots, ref long bytes)
	{
		var estimatedBytes = EstimateStringBytes(value);
		if (estimatedBytes < PayloadBytesPerAnnotationString * 0.95)
			return;

		slots++;
		bytes += Math.Min(estimatedBytes, PayloadBytesPerAnnotationString);
	}

	static string? GetNativeString(IntPtr nativeHandle, IntPtr selector)
	{
		if (nativeHandle == IntPtr.Zero)
			return null;

		var valueHandle = IntPtr_objc_msgSend(nativeHandle, selector);
		if (valueHandle == IntPtr.Zero)
			return null;

		return Runtime.GetNSObject<NSString>(valueHandle)?.ToString();
	}

	static long EstimateStringBytes(string? value) =>
		string.IsNullOrEmpty(value) ? 0 : value.Length * 2L;

	static string CreateAnnotationPayload(int cycle, string slot)
	{
		var header = $"cycle-{cycle:0000}-mappin-{slot}-";
		var sentence = "regional-dispatch-customer-site-instructions-route-window-offline-work-order-building-access-note-";
		var targetChars = (int)(PayloadBytesPerAnnotationString / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static RetainedNativeAnnotation RetainNativeAnnotation(MKPointAnnotation annotation)
	{
		var handle = annotation.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native annotation with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeAnnotation(retained);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	internal sealed record ScenarioTracking(
		IReadOnlyList<RetainedNativeAnnotation> NativeAnnotations,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed record NativeAnnotationSnapshot(bool Alive, int PayloadSlots, long EstimatedBytes);

	internal sealed record RetainedNativeAnnotation(IntPtr Handle);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<MapPinHandler> Handler,
		WeakReference<Pin> Pin)
	{
		public static TrackedCycle Create(
			int cycle,
			MapPinHandler handler,
			Pin pin)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MapPinHandler>(handler),
				new WeakReference<Pin>(pin));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeAnnotations,
		int NativeAnnotationsWithPayloadTextSlots,
		long EstimatedNativeTextBytes,
		int AliveHandlers,
		int AlivePins)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeAnnotation> nativeAnnotations,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeAnnotations = 0;
			var nativeAnnotationsWithPayloadTextSlots = 0;
			long estimatedNativeTextBytes = 0;

			foreach (var nativeAnnotation in nativeAnnotations)
			{
				var snapshot = GetNativeAnnotationSnapshot(nativeAnnotation);
				if (!snapshot.Alive)
					continue;

				retainedNativeAnnotations++;
				nativeAnnotationsWithPayloadTextSlots += snapshot.PayloadSlots;
				estimatedNativeTextBytes += snapshot.EstimatedBytes;
			}

			var aliveHandlers = 0;
			var alivePins = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.Pin.TryGetTarget(out _))
					alivePins++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeAnnotations,
				nativeAnnotationsWithPayloadTextSlots,
				estimatedNativeTextBytes,
				aliveHandlers,
				alivePins);
		}
	}

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerAnnotationString,
	int AnnotationStringSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int ExpectedTextSlots => Cycles * AnnotationStringSlotsPerCycle;

	public bool LeakProved =>
		Control.RetainedNativeAnnotations == Cycles &&
		Control.NativeAnnotationsWithPayloadTextSlots == 0 &&
		Control.AlivePins == 0 &&
		Current.RetainedNativeAnnotations == Cycles &&
		Current.NativeAnnotationsWithPayloadTextSlots == ExpectedTextSlots &&
		Current.EstimatedNativeTextBytes >= ExpectedTextSlots * PayloadKiBPerAnnotationString * 1024L * 0.95 &&
		Current.AlivePins == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeTextBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosMapPinAnnotationTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per annotation string: {PayloadKiBPerAnnotationString} KiB",
			$"Annotation text slots per cycle: {AnnotationStringSlotsPerCycle}",
			$"Expected payload annotation text slots: {ExpectedTextSlots}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native annotation text payload: {controlMiB:N1} MiB",
			$"Current estimated retained native annotation text payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	string Format(ReproSession.ScenarioResult result)
	{
		var nativeTextMiB = result.EstimatedNativeTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native annotation peers: {result.RetainedNativeAnnotations}/{result.TrackedCycles}",
			$"  payload-sized native title/subtitle slots: {result.NativeAnnotationsWithPayloadTextSlots}/{ExpectedTextSlots}",
			$"  estimated retained native text bytes: {result.EstimatedNativeTextBytes:N0}",
			$"  estimated retained native text MiB: {nativeTextMiB:N1}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive pins: {result.AlivePins}/{result.TrackedCycles}");
	}
}
