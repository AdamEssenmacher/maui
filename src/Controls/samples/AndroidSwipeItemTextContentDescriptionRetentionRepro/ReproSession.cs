#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using MauiSwipeItem = Microsoft.Maui.Controls.SwipeItem;

namespace AndroidSwipeItemTextContentDescriptionRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 512;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * 2;
	const int SlotsPerCycle = 2;

	static readonly PropertyMapper<ISwipeItemMenuItem, ISwipeItemMenuItemHandler> EmptyMapper = new();
	static readonly List<TextView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native Text and ContentDescription before disconnect",
			context,
			clearNativeStringSlots: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native Text and ContentDescription assigned",
			context,
			clearNativeStringSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			SlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeStringSlots)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeStringSlots);

			if (i % 64 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeStringSlots)
	{
		var item = new MauiSwipeItem
		{
			Text = CreatePayload("text", cycle),
			AutomationId = CreatePayload("automation", cycle),
			BackgroundColor = Colors.DarkBlue
		};
		var handler = new TestSwipeItemMenuItemHandler(EmptyMapper);

		AttachHandler(item, handler, context);
		SwipeItemMenuItemHandler.MapText(handler, item);

		var platformView = (TextView)handler.PlatformView;

		if (clearNativeStringSlots)
		{
			platformView.Text = null;
			platformView.ContentDescription = null;
		}

		((IElementHandler)handler).DisconnectHandler();
		item.Handler = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(cycle, platformView, item, handler));
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
	}

	static string CreatePayload(string kind, int cycle)
	{
		var prefix = $"android-swipeitem-{kind}-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerSlot - prefix.Length);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	sealed class TestSwipeItemMenuItemHandler : SwipeItemMenuItemHandler
	{
		public TestSwipeItemMenuItemHandler(IPropertyMapper mapper)
			: base(mapper)
		{
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TextView> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler)
	{
		public static TrackedCycle Create(
			int cycle,
			TextView platformView,
			object virtualView,
			IElementHandler handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TextView>(platformView),
				new WeakReference<object>(virtualView),
				new WeakReference<IElementHandler>(handler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AssignedTextSlots,
		int PayloadTextSlots,
		int AssignedContentDescriptionSlots,
		int PayloadContentDescriptionSlots,
		long RetainedNativeStringBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var assignedTextSlots = 0;
			var payloadTextSlots = 0;
			var assignedContentDescriptionSlots = 0;
			var payloadContentDescriptionSlots = 0;
			long retainedNativeStringBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out var nativePeer))
				{
					aliveNativePeers++;
					var textLength = nativePeer.Text?.Length ?? 0;
					var contentDescriptionLength = nativePeer.ContentDescription?.Length ?? 0;

					if (textLength > 0)
						assignedTextSlots++;
					if (textLength >= PayloadCharsPerSlot)
						payloadTextSlots++;

					if (contentDescriptionLength > 0)
						assignedContentDescriptionSlots++;
					if (contentDescriptionLength >= PayloadCharsPerSlot)
						payloadContentDescriptionSlots++;

					retainedNativeStringBytes += ((long)textLength + contentDescriptionLength) * 2;
				}

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativePeers,
				aliveVirtualViews,
				aliveHandlers,
				assignedTextSlots,
				payloadTextSlots,
				assignedContentDescriptionSlots,
				payloadContentDescriptionSlots,
				retainedNativeStringBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	int SlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativePeers == Cycles &&
		Current.AliveNativePeers == Cycles &&
		Control.PayloadTextSlots == 0 &&
		Control.PayloadContentDescriptionSlots == 0 &&
		Current.PayloadTextSlots == Cycles &&
		Current.PayloadContentDescriptionSlots == Cycles &&
		Current.AliveVirtualViews == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidSwipeItemTextContentDescriptionRetentionRepro",
			$"Cycles: {Cycles}",
			$"Native string slots per cycle: {SlotsPerCycle}",
			$"Payload chars per native string slot: {PayloadCharsPerSlot}",
			$"Payload bytes per native string slot: {PayloadBytesPerSlot}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native string payload: {FormatBytes(Control.RetainedNativeStringBytes)}",
			$"Current retained native string payload: {FormatBytes(Current.RetainedNativeStringBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native Text slots: {result.AssignedTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native Text slots: {result.PayloadTextSlots}/{result.TrackedCycles}",
			$"  assigned native ContentDescription slots: {result.AssignedContentDescriptionSlots}/{result.TrackedCycles}",
			$"  payload-sized native ContentDescription slots: {result.PayloadContentDescriptionSlots}/{result.TrackedCycles}",
			$"  retained native string bytes: {result.RetainedNativeStringBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
