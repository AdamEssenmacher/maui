#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using ObjCRuntime;
using UIKit;

namespace IosToolbarItemTitleRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	internal const int Cycles = 1024;
	internal const int PayloadKiBPerTitle = 2;
	internal const int TitleSlotsPerCycle = 3;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

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
		Path.Combine("/tmp", "ios-toolbaritem-title-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS ToolbarItem title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native toolbar title slots before disposal",
			clearNativeTitleBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: toolbar item conversion leaves native title slots assigned",
			clearNativeTitleBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerTitle,
			TitleSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearNativeTitleBeforeDispose)
	{
		var retainedPeers = new List<RetainedNativePeer>(Cycles * TitleSlotsPerCycle);
		var tracked = new List<TrackedToolbarItems>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 128 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateCycle(i, retainedPeers, tracked, clearNativeTitleBeforeDispose);
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
		bool clearNativeTitleBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var primary = CreateToolbarItem(cycle, "primary", ToolbarItemOrder.Primary);
		var secondaryCustom = CreateToolbarItem(cycle, "secondary-custom", ToolbarItemOrder.Secondary);
		var secondaryOverflow = CreateToolbarItem(cycle, "secondary-overflow", ToolbarItemOrder.Secondary);

		var primaryNative = primary.ToUIBarButtonItem(forceName: true);
		var secondaryCustomNative = secondaryCustom.ToUIBarButtonItem();
		var overflowNative = CreateSecondaryOverflowAction(secondaryOverflow);

		if (!NativePeerHasPayload(primaryNative) ||
			!NativePeerHasPayload(secondaryCustomNative) ||
			!NativePeerHasPayload(overflowNative))
		{
			throw new InvalidOperationException("Toolbar conversion did not assign the expected native title payload.");
		}

		var retainedPrimary = RetainNativePeer(primaryNative);
		var retainedSecondaryCustom = RetainNativePeer(secondaryCustomNative);
		var retainedOverflow = RetainNativePeer(overflowNative);

		if (clearNativeTitleBeforeDispose)
		{
			ClearNativeTitle(primaryNative);
			ClearNativeTitle(secondaryCustomNative);
			ClearNativeTitle(overflowNative);
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
		return new ToolbarItem
		{
			Order = order,
			Text = CreateOperationalTitle(cycle, role),
			Command = new Command(() => { })
		};
	}

	static string CreateOperationalTitle(int cycle, string role)
	{
		var header = $"Cycle {cycle:0000} {role} workflow action. ";
		var sentence = "Archive synced customer records, compliance notes, and fulfillment state for offline review. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
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

	static bool NativePeerHasPayload(NSObject nativePeer) =>
		EstimateNativeTitleBytes(nativePeer) >= PayloadBytesPerTitle * 0.95;

	static void ClearNativeTitle(UIBarButtonItem item)
	{
		item.Title = null;

		if (item.CustomView is UIView customView)
			ClearLabels(customView);
	}

	static void ClearNativeTitle(UIAction action)
	{
		action.Title = string.Empty;
	}

	static void ClearLabels(UIView root)
	{
		if (root is UILabel label)
		{
			label.Text = null;
			label.AttributedText = null;
		}

		foreach (var subview in root.Subviews)
			ClearLabels(subview);
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

	static NativeTitleSnapshot GetNativeTitleSnapshot(RetainedNativePeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		if (peer is null)
			return new NativeTitleSnapshot(Alive: false, EstimatedTitleBytes: 0);

		return new NativeTitleSnapshot(Alive: true, EstimateNativeTitleBytes(peer));
	}

	static long EstimateNativeTitleBytes(NSObject nativePeer)
	{
		return nativePeer switch
		{
			UIBarButtonItem barButtonItem => Math.Max(
				EstimateTextBytes(barButtonItem.Title, null),
				EstimateViewTextBytes(barButtonItem.CustomView)),
			UIAction action => EstimateTextBytes(action.Title, null),
			_ => 0
		};
	}

	static long EstimateViewTextBytes(UIView? root)
	{
		if (root is null)
			return 0;

		var max = root is UILabel label
			? EstimateTextBytes(label.Text, label.AttributedText?.Value)
			: 0;

		foreach (var subview in root.Subviews)
			max = Math.Max(max, EstimateViewTextBytes(subview));

		return max;
	}

	static long EstimateTextBytes(string? text, string? attributedText)
	{
		var retainedText = attributedText ?? text;
		return string.IsNullOrEmpty(retainedText) ? 0 : retainedText.Length * 2L;
	}

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

	internal sealed record NativeTitleSnapshot(bool Alive, long EstimatedTitleBytes);

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
		int RetainedNativePeers,
		int NativePeersWithTitle,
		long EstimatedNativeTitleBytes,
		int AliveToolbarItems)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativePeer> retainedPeers,
			IReadOnlyList<TrackedToolbarItems> tracked)
		{
			var retainedNativePeers = 0;
			var nativePeersWithTitle = 0;
			long estimatedNativeTitleBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var snapshot = GetNativeTitleSnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				retainedNativePeers++;
				if (snapshot.EstimatedTitleBytes > 0)
				{
					nativePeersWithTitle++;
					estimatedNativeTitleBytes += Math.Min(snapshot.EstimatedTitleBytes, PayloadBytesPerTitle);
				}
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
				tracked.Count * TitleSlotsPerCycle,
				retainedNativePeers,
				nativePeersWithTitle,
				estimatedNativeTitleBytes,
				aliveToolbarItems);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerTitle,
	int TitleSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Control.ExpectedNativePeers &&
		Control.NativePeersWithTitle == 0 &&
		Control.AliveToolbarItems <= TitleSlotsPerCycle &&
		Current.RetainedNativePeers == Current.ExpectedNativePeers &&
		Current.NativePeersWithTitle == Current.ExpectedNativePeers &&
		Current.EstimatedNativeTitleBytes >= Current.ExpectedNativePeers * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AliveToolbarItems <= TitleSlotsPerCycle;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeTitleBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosToolbarItemTitleRetentionRepro",
			$"ToolbarItem cycles per scenario: {Cycles}",
			$"Payload per native title slot: {PayloadKiBPerTitle} KiB",
			$"Native title slots per cycle: {TitleSlotsPerCycle}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native title payload: {controlMiB:N1} MiB",
			$"Current estimated retained native title payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedNativeTitleBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected native title peers: {result.ExpectedNativePeers}",
			$"  retained native title peers: {result.RetainedNativePeers}/{result.ExpectedNativePeers}",
			$"  native peers with assigned titles: {result.NativePeersWithTitle}/{result.ExpectedNativePeers}",
			$"  estimated retained native title bytes: {result.EstimatedNativeTitleBytes:N0}",
			$"  estimated retained native title MiB: {retainedMiB:N1}",
			$"  alive ToolbarItems: {result.AliveToolbarItems}/{result.ExpectedNativePeers}");
	}
}
