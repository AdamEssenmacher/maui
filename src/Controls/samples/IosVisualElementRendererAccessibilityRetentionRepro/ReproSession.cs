#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using ObjCRuntime;

namespace IosVisualElementRendererAccessibilityRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerAccessibilityString = 128;
	internal const int AccessibilitySlotsPerCycle = 3;

	const long PayloadBytesPerAccessibilityString = PayloadKiBPerAccessibilityString * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly IntPtr AccessibilityIdentifierSelector = Selector.GetHandle("accessibilityIdentifier");
	static readonly IntPtr AccessibilityLabelSelector = Selector.GetHandle("accessibilityLabel");
	static readonly IntPtr AccessibilityHintSelector = Selector.GetHandle("accessibilityHint");
	static readonly List<IReadOnlyList<RetainedNativePeer>> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-visualelementrenderer-accessibility-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS VisualElementRenderer accessibility retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native UIView accessibility slots before renderer disposal",
			clearNativeAccessibilityBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: VisualElementRenderer dispose leaves native accessibility slots assigned",
			clearNativeAccessibilityBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerAccessibilityString,
			AccessibilitySlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		bool clearNativeAccessibilityBeforeDispose)
	{
		var tracking = RunScenarioCore(name, clearNativeAccessibilityBeforeDispose);
		RetainedNativePeers.Add(tracking.NativePeers);
		ForceFullGc();

		return ScenarioResult.From(name, tracking.NativePeers, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(
		string name,
		bool clearNativeAccessibilityBeforeDispose)
	{
		var nativePeers = new List<RetainedNativePeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateDisposedRendererCycle(i, nativePeers, tracked, clearNativeAccessibilityBeforeDispose);
		}

		return new ScenarioTracking(nativePeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRendererCycle(
		int cycle,
		List<RetainedNativePeer> nativePeers,
		List<TrackedCycle> tracked,
		bool clearNativeAccessibilityBeforeDispose)
	{
		var boxView = new AccessibilityBoxView(cycle);
		var renderer = new BoxRenderer();

		renderer.SetElement(boxView);

		if (CountPayloadAccessibilitySlots(renderer) != AccessibilitySlotsPerCycle)
			throw new InvalidOperationException("BoxRenderer did not assign all expected native accessibility string payloads.");

		var retainedPeer = RetainNativePeer(renderer);

		if (clearNativeAccessibilityBeforeDispose)
			ClearNativeAccessibility(renderer);

		renderer.Dispose();
		((IElementController)boxView).EffectControlProvider = null;
		boxView.Handler = null;

		nativePeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create(cycle, renderer, boxView));
	}

	static void ClearNativeAccessibility(BoxRenderer renderer)
	{
		renderer.AccessibilityIdentifier = null;
		renderer.AccessibilityLabel = null;
		renderer.AccessibilityHint = null;
	}

	static int CountPayloadAccessibilitySlots(BoxRenderer renderer)
	{
		var count = 0;

		if (EstimateAccessibilityStringBytes(renderer.AccessibilityIdentifier) >= PayloadBytesPerAccessibilityString * 0.95)
			count++;

		if (EstimateAccessibilityStringBytes(renderer.AccessibilityLabel) >= PayloadBytesPerAccessibilityString * 0.95)
			count++;

		if (EstimateAccessibilityStringBytes(renderer.AccessibilityHint) >= PayloadBytesPerAccessibilityString * 0.95)
			count++;

		return count;
	}

	static NativeAccessibilitySnapshot GetNativeAccessibilitySnapshot(RetainedNativePeer retainedPeer)
	{
		var identifier = GetNativeString(retainedPeer.Handle, AccessibilityIdentifierSelector);
		var label = GetNativeString(retainedPeer.Handle, AccessibilityLabelSelector);
		var hint = GetNativeString(retainedPeer.Handle, AccessibilityHintSelector);
		var slots = 0;
		var bytes = 0L;

		AccumulatePayloadSlot(identifier, ref slots, ref bytes);
		AccumulatePayloadSlot(label, ref slots, ref bytes);
		AccumulatePayloadSlot(hint, ref slots, ref bytes);

		return new NativeAccessibilitySnapshot(Alive: retainedPeer.Handle != IntPtr.Zero, PayloadSlots: slots, EstimatedBytes: bytes);
	}

	static void AccumulatePayloadSlot(string? value, ref int slots, ref long bytes)
	{
		var estimatedBytes = EstimateAccessibilityStringBytes(value);
		if (estimatedBytes < PayloadBytesPerAccessibilityString * 0.95)
			return;

		slots++;
		bytes += Math.Min(estimatedBytes, PayloadBytesPerAccessibilityString);
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

	static long EstimateAccessibilityStringBytes(string? value) =>
		string.IsNullOrEmpty(value) ? 0 : value.Length * 2L;

	static string CreateAccessibilityPayload(int cycle, string slot)
	{
		var header = $"cycle-{cycle:0000}-legacy-visualelementrenderer-{slot}-";
		var sentence = "generated-accessibility-audit-regional-workspace-diagnostic-route-offline-review-policy-exception-";
		var targetChars = (int)(PayloadBytesPerAccessibilityString / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static RetainedNativePeer RetainNativePeer(BoxRenderer renderer)
	{
		var handle = renderer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native renderer peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativePeer(retained);
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
		IReadOnlyList<RetainedNativePeer> NativePeers,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed record NativeAccessibilitySnapshot(bool Alive, int PayloadSlots, long EstimatedBytes);

	internal sealed record RetainedNativePeer(IntPtr Handle);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<BoxRenderer> Renderer,
		WeakReference<BoxView> BoxView)
	{
		public static TrackedCycle Create(
			int cycle,
			BoxRenderer renderer,
			BoxView boxView)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<BoxRenderer>(renderer),
				new WeakReference<BoxView>(boxView));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int NativePeersWithPayloadAccessibilitySlots,
		long EstimatedNativeAccessibilityBytes,
		int AliveRenderers,
		int AliveBoxViews)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativePeer> nativePeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativePeers = 0;
			var nativePeersWithPayloadAccessibilitySlots = 0;
			long estimatedNativeAccessibilityBytes = 0;

			foreach (var nativePeer in nativePeers)
			{
				var snapshot = GetNativeAccessibilitySnapshot(nativePeer);
				if (!snapshot.Alive)
					continue;

				retainedNativePeers++;
				nativePeersWithPayloadAccessibilitySlots += snapshot.PayloadSlots;
				estimatedNativeAccessibilityBytes += snapshot.EstimatedBytes;
			}

			var aliveRenderers = 0;
			var aliveBoxViews = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.BoxView.TryGetTarget(out _))
					aliveBoxViews++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativePeers,
				nativePeersWithPayloadAccessibilitySlots,
				estimatedNativeAccessibilityBytes,
				aliveRenderers,
				aliveBoxViews);
		}
	}

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	sealed class AccessibilityBoxView : BoxView
	{
		public AccessibilityBoxView(int cycle)
		{
			AutomationId = CreateAccessibilityPayload(cycle, "automation-id");
			AutomationProperties.SetName(this, CreateAccessibilityPayload(cycle, "name"));
			AutomationProperties.SetHelpText(this, CreateAccessibilityPayload(cycle, "help-text"));
			WidthRequest = 44;
			HeightRequest = 44;
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerAccessibilityString,
	int AccessibilitySlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int ExpectedAccessibilitySlots => Cycles * AccessibilitySlotsPerCycle;

	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithPayloadAccessibilitySlots == 0 &&
		Control.AliveBoxViews == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithPayloadAccessibilitySlots == ExpectedAccessibilitySlots &&
		Current.EstimatedNativeAccessibilityBytes >= ExpectedAccessibilitySlots * PayloadKiBPerAccessibilityString * 1024L * 0.95 &&
		Current.AliveBoxViews == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeAccessibilityBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeAccessibilityBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosVisualElementRendererAccessibilityRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per accessibility string: {PayloadKiBPerAccessibilityString} KiB",
			$"Accessibility slots per cycle: {AccessibilitySlotsPerCycle}",
			$"Expected payload accessibility slots: {ExpectedAccessibilitySlots}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native accessibility payload: {controlMiB:N1} MiB",
			$"Current estimated retained native accessibility payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	string Format(ReproSession.ScenarioResult result)
	{
		var nativeAccessibilityMiB = result.EstimatedNativeAccessibilityBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native renderer peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  payload-sized native accessibility slots: {result.NativePeersWithPayloadAccessibilitySlots}/{ExpectedAccessibilitySlots}",
			$"  estimated retained native accessibility bytes: {result.EstimatedNativeAccessibilityBytes:N0}",
			$"  estimated retained native accessibility MiB: {nativeAccessibilityMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive BoxViews: {result.AliveBoxViews}/{result.TrackedCycles}");
	}
}
