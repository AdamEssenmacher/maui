#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Views;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;
using MauiButton = Microsoft.Maui.Controls.Button;
using MauiCheckBox = Microsoft.Maui.Controls.CheckBox;
using MauiEntry = Microsoft.Maui.Controls.Entry;
using MauiLabel = Microsoft.Maui.Controls.Label;

namespace AndroidToolTipTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadCharsPerSlot = 64 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);

	static readonly List<AView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26))
			throw new PlatformNotSupportedException("This repro requires Android API 26+ so native View.TooltipText is observable.");

		RetainedNativePeers.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native tooltip text before disconnect",
			context,
			clearNativeTooltip: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native tooltip text assigned",
			context,
			clearNativeTooltip: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeTooltip)
	{
		var tracked = new List<TrackedCycle>(Cycles * 5);

		for (var i = 0; i < Cycles; i++)
		{
			CreateLabelCycle(context, i, tracked, clearNativeTooltip);
			CreateButtonCycle(context, i, tracked, clearNativeTooltip);
			CreateEntryCycle(context, i, tracked, clearNativeTooltip);
			CreateCheckBoxCycle(context, i, tracked, clearNativeTooltip);
			CreateBoxViewCycle(context, i, tracked, clearNativeTooltip);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateLabelCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTooltip)
	{
		var tooltip = CreatePayload("Label", cycle);
		var label = new MauiLabel { Text = "Status" };
		var handler = new LabelHandler();

		MapTooltipAndDisconnect("Label", cycle, label, handler, tooltip, context, tracked, clearNativeTooltip);
	}

	static void CreateButtonCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTooltip)
	{
		var tooltip = CreatePayload("Button", cycle);
		var button = new MauiButton { Text = "Save" };
		var handler = new ButtonHandler();

		MapTooltipAndDisconnect("Button", cycle, button, handler, tooltip, context, tracked, clearNativeTooltip);
	}

	static void CreateEntryCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTooltip)
	{
		var tooltip = CreatePayload("Entry", cycle);
		var entry = new MauiEntry { Text = "query", Placeholder = "Search" };
		var handler = new EntryHandler();

		MapTooltipAndDisconnect("Entry", cycle, entry, handler, tooltip, context, tracked, clearNativeTooltip);
	}

	static void CreateCheckBoxCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTooltip)
	{
		var tooltip = CreatePayload("CheckBox", cycle);
		var checkBox = new MauiCheckBox { IsChecked = (cycle % 2) == 0 };
		var handler = new CheckBoxHandler();

		MapTooltipAndDisconnect("CheckBox", cycle, checkBox, handler, tooltip, context, tracked, clearNativeTooltip);
	}

	static void CreateBoxViewCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTooltip)
	{
		var tooltip = CreatePayload("BoxView", cycle);
		var boxView = new BoxView { WidthRequest = 24, HeightRequest = 24 };
		var handler = new BoxViewHandler();

		MapTooltipAndDisconnect("BoxView", cycle, boxView, handler, tooltip, context, tracked, clearNativeTooltip);
	}

	static void MapTooltipAndDisconnect(
		string controlType,
		int cycle,
		VisualElement view,
		IElementHandler handler,
		string tooltip,
		IMauiContext context,
		List<TrackedCycle> tracked,
		bool clearNativeTooltip)
	{
		ToolTipProperties.SetText(view, tooltip);
		AttachHandler(view, handler, context);

		var platformView = (AView?)handler.PlatformView
			?? throw new InvalidOperationException($"{controlType} handler did not create an Android platform view.");

		Microsoft.Maui.Handlers.ViewHandler.MapToolTip((IViewHandler)handler, view);

		var assignedLengthBeforeCleanup = platformView.TooltipText?.Length ?? 0;
		if (clearNativeTooltip)
			TooltipCompat.SetTooltipText(platformView, (string?)null);

		Disconnect(view, handler);
		view.ClearValue(ToolTipProperties.TextProperty);

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(
			controlType,
			cycle,
			platformView,
			view,
			handler,
			assignedLengthBeforeCleanup));
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
	}

	static void Disconnect(IElement view, IElementHandler handler)
	{
		((IElementHandler)handler).DisconnectHandler();
		view.Handler = null;
	}

	static string CreatePayload(string controlType, int cycle)
	{
		var prefix = $"tooltip:{controlType}:{cycle:D3}:";
		var fill = (char)('A' + ((cycle + controlType.Length) % 26));
		return prefix + new string(fill, PayloadCharsPerSlot - prefix.Length);
	}

	static TooltipSlotSnapshot CaptureTooltipSlot(AView view)
	{
		var value = view.TooltipText;
		var length = value?.Length ?? 0;
		var payloadSlots = length >= PayloadCharsPerSlot ? 1 : 0;
		return new TooltipSlotSnapshot(
			length > 0 ? 1 : 0,
			payloadSlots,
			(long)payloadSlots * PayloadBytesPerSlot,
			length);
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

	internal sealed record TrackedCycle(
		string ControlType,
		int Cycle,
		WeakReference<AView> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		int AssignedLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			string controlType,
			int cycle,
			AView platformView,
			object virtualView,
			IElementHandler handler,
			int assignedLengthBeforeCleanup)
		{
			return new TrackedCycle(
				controlType,
				cycle,
				new WeakReference<AView>(platformView),
				new WeakReference<object>(virtualView),
				new WeakReference<IElementHandler>(handler),
				assignedLengthBeforeCleanup);
		}
	}

	internal sealed record TooltipSlotSnapshot(
		int AssignedSlots,
		int PayloadSizedSlots,
		long RetainedPayloadBytes,
		int TooltipLength);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ExpectedPayloadSlots,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AssignedTooltipSlots,
		int PayloadSizedTooltipSlots,
		long RetainedPayloadBytes,
		int AssignedBeforeCleanup,
		IReadOnlyDictionary<string, TypeResult> ByControlType)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var expectedPayloadSlots = 0;
			var aliveNativePeers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var assignedTooltipSlots = 0;
			var payloadSizedTooltipSlots = 0;
			var assignedBeforeCleanup = 0;
			long retainedPayloadBytes = 0;
			var byType = new Dictionary<string, TypeCounter>(StringComparer.Ordinal);

			foreach (var cycle in tracked)
			{
				var counter = GetCounter(byType, cycle.ControlType);
				counter.Tracked++;
				counter.ExpectedPayloadSlots++;
				expectedPayloadSlots++;

				if (cycle.AssignedLengthBeforeCleanup >= PayloadCharsPerSlot)
				{
					assignedBeforeCleanup++;
					counter.AssignedBeforeCleanup++;
				}

				if (cycle.NativePeer.TryGetTarget(out var nativePeer))
				{
					aliveNativePeers++;
					counter.AliveNativePeers++;

					var slots = CaptureTooltipSlot(nativePeer);
					assignedTooltipSlots += slots.AssignedSlots;
					payloadSizedTooltipSlots += slots.PayloadSizedSlots;
					retainedPayloadBytes += slots.RetainedPayloadBytes;
					counter.AssignedTooltipSlots += slots.AssignedSlots;
					counter.PayloadSizedTooltipSlots += slots.PayloadSizedSlots;
					counter.RetainedPayloadBytes += slots.RetainedPayloadBytes;
				}

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				expectedPayloadSlots,
				aliveNativePeers,
				aliveVirtualViews,
				aliveHandlers,
				assignedTooltipSlots,
				payloadSizedTooltipSlots,
				retainedPayloadBytes,
				assignedBeforeCleanup,
				byType.ToDictionary(pair => pair.Key, pair => pair.Value.ToResult(), StringComparer.Ordinal));
		}

		static TypeCounter GetCounter(Dictionary<string, TypeCounter> values, string controlType)
		{
			if (!values.TryGetValue(controlType, out var counter))
			{
				counter = new TypeCounter();
				values.Add(controlType, counter);
			}

			return counter;
		}
	}

	internal sealed record TypeResult(
		int Tracked,
		int ExpectedPayloadSlots,
		int AliveNativePeers,
		int AssignedTooltipSlots,
		int PayloadSizedTooltipSlots,
		int AssignedBeforeCleanup,
		long RetainedPayloadBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int ExpectedPayloadSlots { get; set; }
		public int AliveNativePeers { get; set; }
		public int AssignedTooltipSlots { get; set; }
		public int PayloadSizedTooltipSlots { get; set; }
		public int AssignedBeforeCleanup { get; set; }
		public long RetainedPayloadBytes { get; set; }

		public TypeResult ToResult() =>
			new(
				Tracked,
				ExpectedPayloadSlots,
				AliveNativePeers,
				AssignedTooltipSlots,
				PayloadSizedTooltipSlots,
				AssignedBeforeCleanup,
				RetainedPayloadBytes);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int ControlTypes => 5;
	int TotalCycles => Cycles * ControlTypes;

	public bool LeakProved =>
		Control.AssignedBeforeCleanup == TotalCycles &&
		Current.AssignedBeforeCleanup == TotalCycles &&
		Control.AliveNativePeers == TotalCycles &&
		Control.PayloadSizedTooltipSlots == 0 &&
		Current.AliveNativePeers == TotalCycles &&
		Current.PayloadSizedTooltipSlots == Current.ExpectedPayloadSlots &&
		Current.RetainedPayloadBytes >= 40L * 1024 * 1024;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidToolTipTextRetentionRepro",
			$"Cycles per control type: {Cycles}",
			$"Control types per scenario: {ControlTypes}",
			$"Total handler cycles per scenario: {TotalCycles}",
			$"Payload chars per native tooltip slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native tooltip slot: {PayloadBytesPerSlot:N0}",
			"Source path exercised: ViewHandler.MapToolTip -> Android ViewExtensions.UpdateToolTip -> TooltipCompat.SetTooltipText",
			"Control clears native tooltip text before handler disconnect; current MAUI disconnect leaves it assigned",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native tooltip payload: {controlMiB:N1} MiB",
			$"Current retained native tooltip payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected payload tooltip slots: {result.ExpectedPayloadSlots}",
			$"  payload tooltips assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native tooltip slots: {result.AssignedTooltipSlots}/{result.ExpectedPayloadSlots}",
			$"  payload-sized native tooltip slots: {result.PayloadSizedTooltipSlots}/{result.ExpectedPayloadSlots}",
			$"  retained native tooltip payload bytes: {result.RetainedPayloadBytes:N0}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.AliveNativePeers}/{value.Tracked}, assignedBeforeCleanup={value.AssignedBeforeCleanup}/{value.Tracked}, assignedSlots={value.AssignedTooltipSlots}/{value.ExpectedPayloadSlots}, payloadSlots={value.PayloadSizedTooltipSlots}/{value.ExpectedPayloadSlots}, retained={value.RetainedPayloadBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}
