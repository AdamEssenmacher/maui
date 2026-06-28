#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using ObjCRuntime;
using UIKit;

namespace IosToolbarItemAccessibilityRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	internal const int Cycles = 1024;
	internal const int PayloadKiBPerAccessibilitySlot = 8;
	internal const int NativePeersPerCycle = 3;
	internal const int AccessibilitySlotsPerCycle = 7;

	const long PayloadBytesPerAccessibilitySlot = PayloadKiBPerAccessibilitySlot * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedNativePeer>> RetainedNativePeers = new();

	static readonly MethodInfo ToSecondarySubToolbarItemMethod =
		typeof(ToolbarItemExtensions).GetMethod(
			"ToSecondarySubToolbarItem",
			BindingFlags.Static | BindingFlags.NonPublic)!;

	static readonly PropertyInfo PlatformActionProperty =
		ToSecondarySubToolbarItemMethod.ReturnType.GetProperty(
			"PlatformAction",
			BindingFlags.Instance | BindingFlags.Public)!;

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-toolbaritem-accessibility-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS ToolbarItem accessibility retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native toolbar accessibility slots before disposal",
			clearNativeAccessibilityBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: toolbar item conversion leaves native accessibility slots assigned",
			clearNativeAccessibilityBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerAccessibilitySlot,
			NativePeersPerCycle,
			AccessibilitySlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearNativeAccessibilityBeforeDispose)
	{
		var retainedPeers = new List<RetainedNativePeer>(Cycles * NativePeersPerCycle);
		var tracked = new List<TrackedToolbarItems>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 128 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateCycle(i, retainedPeers, tracked, clearNativeAccessibilityBeforeDispose);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		int cycle,
		List<RetainedNativePeer> retainedPeers,
		List<TrackedToolbarItems> tracked,
		bool clearNativeAccessibilityBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var primary = CreateToolbarItem(cycle, "primary", ToolbarItemOrder.Primary);
		var secondaryCustom = CreateToolbarItem(cycle, "secondary-custom", ToolbarItemOrder.Secondary);
		var secondaryOverflow = CreateToolbarItem(cycle, "secondary-overflow", ToolbarItemOrder.Secondary);

		var primaryNative = primary.ToUIBarButtonItem(forceName: true);
		var secondaryCustomNative = secondaryCustom.ToUIBarButtonItem();
		var overflowNative = CreateSecondaryOverflowAction(secondaryOverflow);

		if (CountPayloadAccessibilitySlots(primaryNative) != 3 ||
			CountPayloadAccessibilitySlots(secondaryCustomNative) != 3 ||
			CountPayloadAccessibilitySlots(overflowNative) != 1)
		{
			throw new InvalidOperationException("Toolbar conversion did not assign the expected native accessibility payloads.");
		}

		var retainedPrimary = RetainNativePeer(primaryNative);
		var retainedSecondaryCustom = RetainNativePeer(secondaryCustomNative);
		var retainedOverflow = RetainNativePeer(overflowNative);

		if (clearNativeAccessibilityBeforeDispose)
		{
			ClearNativeAccessibility(primaryNative);
			ClearNativeAccessibility(secondaryCustomNative);
			ClearNativeAccessibility(overflowNative);
		}

		tracked.Add(TrackedToolbarItems.Create(cycle, primary, secondaryCustom, secondaryOverflow));

		primaryNative.Dispose();
		secondaryCustomNative.Dispose();
		overflowNative.Dispose();

		retainedPeers.Add(retainedPrimary);
		retainedPeers.Add(retainedSecondaryCustom);
		retainedPeers.Add(retainedOverflow);
	}

	static ToolbarItem CreateToolbarItem(int cycle, string role, ToolbarItemOrder order)
	{
		var item = new ToolbarItem
		{
			Order = order,
			Text = string.Empty,
			Command = new Command(() => { }),
			AutomationId = CreateAccessibilityPayload(cycle, role, "automation-id")
		};

		AutomationProperties.SetName(item, CreateAccessibilityPayload(cycle, role, "name"));
		AutomationProperties.SetHelpText(item, CreateAccessibilityPayload(cycle, role, "help-text"));

		return item;
	}

	static string CreateAccessibilityPayload(int cycle, string role, string slot)
	{
		var header = $"Cycle {cycle:0000} {role} {slot}. ";
		var sentence = "Generated toolbar command accessibility metadata for offline workflow review, compliance context, and action confirmation. ";
		var targetChars = (int)(PayloadBytesPerAccessibilitySlot / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static UIAction CreateSecondaryOverflowAction(ToolbarItem item)
	{
		var holder = ToSecondarySubToolbarItemMethod.Invoke(null, new object[] { item })
			?? throw new InvalidOperationException("Failed to create secondary toolbar action holder.");

		return (UIAction?)PlatformActionProperty.GetValue(holder)
			?? throw new InvalidOperationException("Failed to read secondary toolbar native UIAction.");
	}

	static void ClearNativeAccessibility(UIBarButtonItem item)
	{
		item.AccessibilityIdentifier = null;
		item.AccessibilityLabel = null;
		item.AccessibilityHint = null;
	}

	static void ClearNativeAccessibility(UIAction action)
	{
		action.AccessibilityIdentifier = null;
	}

	static RetainedNativePeer RetainNativePeer(NSObject nativePeer)
	{
		var handle = nativePeer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException($"Cannot retain a native {nativePeer.GetType().Name} peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativePeer(nativePeer.GetType(), retained);
	}

	static NativeAccessibilitySnapshot GetNativeAccessibilitySnapshot(RetainedNativePeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		if (peer is null)
			return new NativeAccessibilitySnapshot(Alive: false, PayloadSizedSlots: 0, EstimatedAccessibilityBytes: 0);

		var payloadSizedSlots = CountPayloadAccessibilitySlots(peer);
		var estimatedBytes = EstimateNativeAccessibilityBytes(peer);
		return new NativeAccessibilitySnapshot(true, payloadSizedSlots, estimatedBytes);
	}

	static int CountPayloadAccessibilitySlots(NSObject nativePeer) =>
		GetNativeAccessibilityTexts(nativePeer).Count(text => EstimateTextBytes(text) >= PayloadBytesPerAccessibilitySlot * 0.95);

	static long EstimateNativeAccessibilityBytes(NSObject nativePeer)
	{
		long total = 0;
		foreach (var text in GetNativeAccessibilityTexts(nativePeer))
		{
			var bytes = EstimateTextBytes(text);
			if (bytes >= PayloadBytesPerAccessibilitySlot * 0.95)
				total += Math.Min(bytes, PayloadBytesPerAccessibilitySlot);
		}

		return total;
	}

	static IEnumerable<string?> GetNativeAccessibilityTexts(NSObject nativePeer)
	{
		if (nativePeer is UIBarButtonItem barButtonItem)
		{
			yield return barButtonItem.AccessibilityIdentifier;
			yield return barButtonItem.AccessibilityLabel;
			yield return barButtonItem.AccessibilityHint;
		}
		else if (nativePeer is UIAction action)
		{
			yield return action.AccessibilityIdentifier;
		}
	}

	static long EstimateTextBytes(string? text) =>
		string.IsNullOrEmpty(text) ? 0 : text.Length * 2L;

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
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

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed record NativeAccessibilitySnapshot(bool Alive, int PayloadSizedSlots, long EstimatedAccessibilityBytes);

	internal sealed class RetainedNativePeer
	{
		public RetainedNativePeer(Type peerType, IntPtr handle)
		{
			PeerType = peerType;
			Handle = handle;
		}

		public Type PeerType { get; }

		public IntPtr Handle { get; }

		public NSObject? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				if (PeerType == typeof(UIAction))
					return Runtime.GetNSObject<UIAction>(Handle, false);

				return Runtime.GetNSObject<UIBarButtonItem>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedToolbarItems(
		int Cycle,
		WeakReference<ToolbarItem> Primary,
		WeakReference<ToolbarItem> SecondaryCustom,
		WeakReference<ToolbarItem> SecondaryOverflow)
	{
		public static TrackedToolbarItems Create(
			int cycle,
			ToolbarItem primary,
			ToolbarItem secondaryCustom,
			ToolbarItem secondaryOverflow)
		{
			return new TrackedToolbarItems(
				cycle,
				new WeakReference<ToolbarItem>(primary),
				new WeakReference<ToolbarItem>(secondaryCustom),
				new WeakReference<ToolbarItem>(secondaryOverflow));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ExpectedNativePeers,
		int ExpectedAccessibilitySlots,
		int RetainedNativePeers,
		int AssignedPayloadSizedAccessibilitySlots,
		long EstimatedNativeAccessibilityBytes,
		int AliveToolbarItems)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativePeer> retainedPeers,
			IReadOnlyList<TrackedToolbarItems> tracked)
		{
			var retainedNativePeers = 0;
			var assignedPayloadSizedAccessibilitySlots = 0;
			long estimatedNativeAccessibilityBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var snapshot = GetNativeAccessibilitySnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				retainedNativePeers++;
				assignedPayloadSizedAccessibilitySlots += snapshot.PayloadSizedSlots;
				estimatedNativeAccessibilityBytes += snapshot.EstimatedAccessibilityBytes;
			}

			var aliveToolbarItems = 0;
			foreach (var cycle in tracked)
			{
				if (cycle.Primary.TryGetTarget(out _))
					aliveToolbarItems++;

				if (cycle.SecondaryCustom.TryGetTarget(out _))
					aliveToolbarItems++;

				if (cycle.SecondaryOverflow.TryGetTarget(out _))
					aliveToolbarItems++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				tracked.Count * NativePeersPerCycle,
				tracked.Count * AccessibilitySlotsPerCycle,
				retainedNativePeers,
				assignedPayloadSizedAccessibilitySlots,
				estimatedNativeAccessibilityBytes,
				aliveToolbarItems);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerAccessibilitySlot,
	int NativePeersPerCycle,
	int AccessibilitySlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Control.ExpectedNativePeers &&
		Control.AssignedPayloadSizedAccessibilitySlots == 0 &&
		Control.AliveToolbarItems <= NativePeersPerCycle &&
		Current.RetainedNativePeers == Current.ExpectedNativePeers &&
		Current.AssignedPayloadSizedAccessibilitySlots == Current.ExpectedAccessibilitySlots &&
		Current.EstimatedNativeAccessibilityBytes >= Current.ExpectedAccessibilitySlots * PayloadKiBPerAccessibilitySlot * 1024L * 0.95 &&
		Current.AliveToolbarItems <= NativePeersPerCycle;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeAccessibilityBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeAccessibilityBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosToolbarItemAccessibilityRetentionRepro",
			$"ToolbarItem cycles per scenario: {Cycles}",
			$"Payload per native accessibility slot: {PayloadKiBPerAccessibilitySlot} KiB",
			$"Native peers per cycle: {NativePeersPerCycle}",
			$"Native accessibility slots per cycle: {AccessibilitySlotsPerCycle}",
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

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedNativeAccessibilityBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected native peers: {result.ExpectedNativePeers}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.ExpectedNativePeers}",
			$"  expected native accessibility slots: {result.ExpectedAccessibilitySlots}",
			$"  assigned payload-sized accessibility slots: {result.AssignedPayloadSizedAccessibilitySlots}/{result.ExpectedAccessibilitySlots}",
			$"  estimated retained native accessibility bytes: {result.EstimatedNativeAccessibilityBytes:N0}",
			$"  estimated retained native accessibility MiB: {retainedMiB:N1}",
			$"  alive ToolbarItems: {result.AliveToolbarItems}/{result.ExpectedNativePeers}");
	}
}
