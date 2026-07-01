#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;
using MauiDatePicker = Microsoft.Maui.Controls.DatePicker;
using MauiTimePicker = Microsoft.Maui.Controls.TimePicker;

namespace AndroidMaterialDateTimePickerHandlerNativeTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int PayloadCharsPerSlot = 4 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);

	static readonly List<AView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native date/time display text before disconnect",
			context,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native date/time display text assigned",
			context,
			clearNativeText: false);

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
		bool clearNativeText)
	{
		var tracked = new List<TrackedCycle>(Cycles * 2);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDatePickerCycle(context, i, tracked, clearNativeText);
			CreateTimePickerCycle(context, i, tracked, clearNativeText);
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateDatePickerCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var payload = CreatePayload("DatePicker", cycle);
		var datePicker = new MauiDatePicker
		{
			Date = new DateTime(2026, 7, 1).AddDays(cycle % 28),
			Format = CreateDateFormat(payload)
		};
		var handler = AttachMaterialHandler(datePicker, context, "DatePickerHandler2");
		var platformView = GetTextView(handler, "DatePicker");

		if (clearNativeText)
			ClearTextView(platformView);

		Disconnect(datePicker, handler);
		datePicker.Format = "d";

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("DatePicker", cycle, platformView, datePicker, handler, expectedPayloadSlots: 1));
	}

	static void CreateTimePickerCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var payload = CreatePayload("TimePicker", cycle);
		var timePicker = new MauiTimePicker
		{
			Time = new TimeSpan(cycle % 24, cycle % 60, 0),
			Format = CreateTimeFormat(payload)
		};
		var handler = AttachMaterialHandler(timePicker, context, "TimePickerHandler2");
		var platformView = GetTextView(handler, "TimePicker");

		if (clearNativeText)
			ClearTextView(platformView);

		Disconnect(timePicker, handler);
		timePicker.Format = "t";

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("TimePicker", cycle, platformView, timePicker, handler, expectedPayloadSlots: 1));
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
			?? throw new InvalidOperationException($"{controlType} Material3 platform view was {handler.PlatformView?.GetType().FullName ?? "null"}, not a TextView.");
	}

	static void Disconnect(IElement view, IElementHandler handler)
	{
		((IElementHandler)handler).DisconnectHandler();
		view.Handler = null;
	}

	static string CreatePayload(string controlType, int cycle)
	{
		var prefix = $"{controlType}:{cycle:D4}:GeneratedScheduleDisplay:";
		var fill = (char)('A' + ((cycle + controlType.Length) % 26));
		return prefix + new string(fill, PayloadCharsPerSlot - prefix.Length);
	}

	static string CreateDateFormat(string literalPayload) =>
		$"yyyy-MM-dd '{literalPayload}'";

	static string CreateTimeFormat(string literalPayload) =>
		$"HH:mm '{literalPayload}'";

	static void ClearTextView(TextView textView)
	{
		textView.Text = string.Empty;
		textView.Hint = string.Empty;
	}

	static TextSlotSnapshot CaptureTextSlots(AView view, string controlType)
	{
		var lengths = new List<int>();

		switch (controlType)
		{
			case "DatePicker":
			case "TimePicker":
				if (view is TextView textView)
					AddLength(lengths, textView.Text);
				break;
		}

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
		string ControlType,
		int Cycle,
		WeakReference<AView> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		int ExpectedPayloadSlots)
	{
		public static TrackedCycle Create(
			string controlType,
			int cycle,
			AView platformView,
			object virtualView,
			IElementHandler handler,
			int expectedPayloadSlots)
		{
			return new TrackedCycle(
				controlType,
				cycle,
				new WeakReference<AView>(platformView),
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
		IReadOnlyDictionary<string, TypeResult> ByControlType)
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
				var counter = GetCounter(byType, cycle.ControlType);
				counter.Tracked++;
				counter.ExpectedPayloadSlots += cycle.ExpectedPayloadSlots;
				expectedPayloadSlots += cycle.ExpectedPayloadSlots;

				if (cycle.NativePeer.TryGetTarget(out var nativePeer))
				{
					aliveNativePeers++;
					counter.AliveNativePeers++;

					var slots = CaptureTextSlots(nativePeer, cycle.ControlType);
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
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int TotalCycles => Cycles * 2;

	public bool LeakProved =>
		Control.AliveNativePeers == TotalCycles &&
		Control.PayloadSizedTextSlots == 0 &&
		Current.AliveNativePeers == TotalCycles &&
		Current.PayloadSizedTextSlots == Current.ExpectedPayloadSlots &&
		Current.RetainedPayloadBytes >= 16L * 1024 * 1024;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidMaterialDateTimePickerHandlerNativeTextRetentionRepro",
			$"Cycles per picker type: {Cycles}",
			$"Total handler cycles per scenario: {TotalCycles}",
			$"Payload chars per native DatePicker/TimePicker text slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native DatePicker/TimePicker text slot: {PayloadBytesPerSlot:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native text payload: {controlMiB:N1} MiB",
			$"Current retained native text payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected payload text slots: {result.ExpectedPayloadSlots}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native DatePicker/TimePicker text slots: {result.AssignedTextSlots}/{result.ExpectedPayloadSlots}",
			$"  payload-sized native DatePicker/TimePicker text slots: {result.PayloadSizedTextSlots}/{result.ExpectedPayloadSlots}",
			$"  max native text slot length: {result.MaxTextSlotLength:N0}",
			$"  retained native text payload bytes: {result.RetainedPayloadBytes:N0}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.AliveNativePeers}/{value.Tracked}, assignedSlots={value.AssignedTextSlots}/{value.ExpectedPayloadSlots}, payloadSlots={value.PayloadSizedTextSlots}/{value.ExpectedPayloadSlots}, maxLength={value.MaxTextSlotLength:N0}, retained={value.RetainedPayloadBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}
