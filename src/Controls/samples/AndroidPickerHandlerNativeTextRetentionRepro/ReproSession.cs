#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;
using MauiPickerControl = Microsoft.Maui.Controls.Picker;

namespace AndroidPickerHandlerNativeTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int HandlerTypes = 2;
	const int ExpectedPayloadSlotsPerCycle = 4;
	const int PayloadCharsPerSlot = 4 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);

	static readonly List<AView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear Android picker native text/title slots before disconnect",
			context,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves Android picker native text/title slots assigned",
			context,
			clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			HandlerTypes,
			ExpectedPayloadSlotsPerCycle,
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
		bool clearNativeText)
	{
		var tracked = new List<TrackedCycle>(Cycles * HandlerTypes);

		for (var i = 0; i < Cycles; i++)
		{
			CreateClassicPickerCycle(context, i, tracked, clearNativeText);
			CreateMaterialPickerCycle(context, i, tracked, clearNativeText);
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateClassicPickerCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var title = CreatePayload("ClassicPicker", cycle, "Title");
		var item = CreatePayload("ClassicPicker", cycle, "SelectedItem");
		var picker = CreatePicker(title, item);
		var handler = new PickerHandler();

		AttachHandler(picker, handler, context, "PickerHandler");
		handler.UpdateValue(nameof(MauiPickerControl.Title));
		handler.UpdateValue(nameof(MauiPickerControl.SelectedIndex));

		var platformView = GetTextView(handler, "ClassicPicker");
		if (clearNativeText)
			ClearTextView(platformView);

		Disconnect(picker, handler);
		ClearPicker(picker);

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("ClassicPicker", cycle, platformView, picker, handler, expectedPayloadSlots: 2));
	}

	static void CreateMaterialPickerCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var title = CreatePayload("MaterialPicker", cycle, "Title");
		var item = CreatePayload("MaterialPicker", cycle, "SelectedItem");
		var picker = CreatePicker(title, item);
		var handler = AttachMaterialHandler(picker, context, "PickerHandler2");
		handler.UpdateValue(nameof(MauiPickerControl.Title));
		handler.UpdateValue(nameof(MauiPickerControl.SelectedIndex));

		var platformView = GetTextView(handler, "MaterialPicker");
		if (clearNativeText)
			ClearTextView(platformView);

		Disconnect(picker, handler);
		ClearPicker(picker);

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("MaterialPicker", cycle, platformView, picker, handler, expectedPayloadSlots: 2));
	}

	static MauiPickerControl CreatePicker(string title, string item)
	{
		var picker = new MauiPickerControl
		{
			Title = title
		};

		picker.Items.Add(item);
		picker.SelectedIndex = 0;
		return picker;
	}

	static void ClearPicker(MauiPickerControl picker)
	{
		picker.SelectedIndex = -1;
		picker.Title = null;
		picker.Items.Clear();
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context, string expectedHandlerName)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;

		if (!string.Equals(handler.GetType().Name, expectedHandlerName, StringComparison.Ordinal))
			throw new InvalidOperationException($"Expected handler {expectedHandlerName}, but got {handler.GetType().FullName}.");
	}

	static IElementHandler AttachMaterialHandler(IElement view, IMauiContext context, string expectedHandlerName)
	{
		var handler = view.ToHandler(context);
		if (!string.Equals(handler.GetType().Name, expectedHandlerName, StringComparison.Ordinal))
			throw new InvalidOperationException($"Expected Material3 handler {expectedHandlerName}, but got {handler.GetType().FullName}.");

		return handler;
	}

	static TextView GetTextView(IElementHandler handler, string controlType)
	{
		return handler.PlatformView as TextView
			?? throw new InvalidOperationException($"{controlType} platform view was {handler.PlatformView?.GetType().FullName ?? "null"}, not a TextView.");
	}

	static void Disconnect(IElement view, IElementHandler handler)
	{
		((IElementHandler)handler).DisconnectHandler();
		view.Handler = null;
	}

	static string CreatePayload(string pickerType, int cycle, string slot)
	{
		var prefix = $"{pickerType}:{cycle:D4}:{slot}:";
		var fill = (char)('A' + ((cycle + pickerType.Length + slot.Length) % 26));
		return prefix + new string(fill, PayloadCharsPerSlot - prefix.Length);
	}

	static void ClearTextView(TextView textView)
	{
		textView.Text = string.Empty;
		textView.Hint = string.Empty;
	}

	static TextSlotSnapshot CaptureTextSlots(TextView textView)
	{
		var lengths = new List<int>();
		AddLength(lengths, textView.Text);
		AddLength(lengths, textView.Hint);

		var payloadSlots = lengths.Count(static length => length >= PayloadCharsPerSlot);
		var maxSlotLength = lengths.Count == 0 ? 0 : lengths.Max();
		var retainedBytes = (long)payloadSlots * PayloadBytesPerSlot;
		return new TextSlotSnapshot(lengths.Count, payloadSlots, maxSlotLength, retainedBytes);
	}

	static void AddLength(List<int> lengths, string? value)
	{
		if (!string.IsNullOrEmpty(value))
			lengths.Add(value.Length);
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
		string PickerType,
		int Cycle,
		WeakReference<TextView> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		int ExpectedPayloadSlots)
	{
		public static TrackedCycle Create(
			string pickerType,
			int cycle,
			TextView platformView,
			object virtualView,
			IElementHandler handler,
			int expectedPayloadSlots)
		{
			return new TrackedCycle(
				pickerType,
				cycle,
				new WeakReference<TextView>(platformView),
				new WeakReference<object>(virtualView),
				new WeakReference<IElementHandler>(handler),
				expectedPayloadSlots);
		}
	}

	internal sealed record TextSlotSnapshot(
		int AssignedSlots,
		int PayloadSizedSlots,
		int MaxSlotLength,
		long RetainedPayloadBytes);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ExpectedPayloadSlots,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AssignedTextSlots,
		int PayloadSizedTextSlots,
		int MaxTextSlotLength,
		long RetainedPayloadBytes,
		IReadOnlyDictionary<string, TypeResult> ByPickerType)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var expectedPayloadSlots = 0;
			var aliveNativePeers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var assignedTextSlots = 0;
			var payloadSizedTextSlots = 0;
			var maxTextSlotLength = 0;
			long retainedPayloadBytes = 0;
			var byType = new Dictionary<string, TypeCounter>(StringComparer.Ordinal);

			foreach (var cycle in tracked)
			{
				var counter = GetCounter(byType, cycle.PickerType);
				counter.Tracked++;
				counter.ExpectedPayloadSlots += cycle.ExpectedPayloadSlots;
				expectedPayloadSlots += cycle.ExpectedPayloadSlots;

				if (cycle.NativePeer.TryGetTarget(out var nativePeer))
				{
					aliveNativePeers++;
					counter.AliveNativePeers++;

					var slots = CaptureTextSlots(nativePeer);
					assignedTextSlots += slots.AssignedSlots;
					payloadSizedTextSlots += slots.PayloadSizedSlots;
					maxTextSlotLength = Math.Max(maxTextSlotLength, slots.MaxSlotLength);
					retainedPayloadBytes += slots.RetainedPayloadBytes;
					counter.AssignedTextSlots += slots.AssignedSlots;
					counter.PayloadSizedTextSlots += slots.PayloadSizedSlots;
					counter.MaxTextSlotLength = Math.Max(counter.MaxTextSlotLength, slots.MaxSlotLength);
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
				assignedTextSlots,
				payloadSizedTextSlots,
				maxTextSlotLength,
				retainedPayloadBytes,
				byType.ToDictionary(pair => pair.Key, pair => pair.Value.ToResult(), StringComparer.Ordinal));
		}

		static TypeCounter GetCounter(Dictionary<string, TypeCounter> values, string pickerType)
		{
			if (!values.TryGetValue(pickerType, out var counter))
			{
				counter = new TypeCounter();
				values.Add(pickerType, counter);
			}

			return counter;
		}
	}

	internal sealed record TypeResult(
		int Tracked,
		int ExpectedPayloadSlots,
		int AliveNativePeers,
		int AssignedTextSlots,
		int PayloadSizedTextSlots,
		int MaxTextSlotLength,
		long RetainedPayloadBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int ExpectedPayloadSlots { get; set; }
		public int AliveNativePeers { get; set; }
		public int AssignedTextSlots { get; set; }
		public int PayloadSizedTextSlots { get; set; }
		public int MaxTextSlotLength { get; set; }
		public long RetainedPayloadBytes { get; set; }

		public TypeResult ToResult() =>
			new(Tracked, ExpectedPayloadSlots, AliveNativePeers, AssignedTextSlots, PayloadSizedTextSlots, MaxTextSlotLength, RetainedPayloadBytes);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int HandlerTypes,
	int ExpectedPayloadSlotsPerCycle,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int TotalCycles => Cycles * HandlerTypes;
	int ExpectedPayloadSlots => Cycles * ExpectedPayloadSlotsPerCycle;

	public bool LeakProved =>
		Control.AliveNativePeers == TotalCycles &&
		Control.PayloadSizedTextSlots == 0 &&
		Current.AliveNativePeers == TotalCycles &&
		Current.PayloadSizedTextSlots >= ExpectedPayloadSlots &&
		Current.RetainedPayloadBytes >= 32L * 1024 * 1024;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidPickerHandlerNativeTextRetentionRepro",
			$"Cycles per picker handler type: {Cycles}",
			$"Picker handler types per scenario: {HandlerTypes}",
			$"Total handler cycles per scenario: {TotalCycles}",
			$"Expected payload text/title slots per cycle: {ExpectedPayloadSlotsPerCycle}",
			$"Expected payload text/title slots per scenario: {ExpectedPayloadSlots}",
			$"Payload chars per native text/title slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native text/title slot: {PayloadBytesPerSlot:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native text/title payload: {controlMiB:N1} MiB",
			$"Current retained native text/title payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected payload text/title slots: {result.ExpectedPayloadSlots}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native text/title slots: {result.AssignedTextSlots}/{result.ExpectedPayloadSlots}",
			$"  payload-sized native text/title slots: {result.PayloadSizedTextSlots}/{result.ExpectedPayloadSlots}",
			$"  max native text/title slot length: {result.MaxTextSlotLength:N0}",
			$"  retained native text/title payload bytes: {result.RetainedPayloadBytes:N0}"
		};

		foreach (var pair in result.ByPickerType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.AliveNativePeers}/{value.Tracked}, assignedSlots={value.AssignedTextSlots}/{value.ExpectedPayloadSlots}, payloadSlots={value.PayloadSizedTextSlots}/{value.ExpectedPayloadSlots}, maxLength={value.MaxTextSlotLength:N0}, retained={value.RetainedPayloadBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}
