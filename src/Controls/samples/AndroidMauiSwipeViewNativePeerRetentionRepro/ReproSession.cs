#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Views;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;
using PlatformSwipeView = Microsoft.Maui.Platform.MauiSwipeView;

namespace AndroidMauiSwipeViewNativePeerRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadBytesPerCycle = 1024 * 1024;

	static readonly List<PlatformSwipeView> RetainedPlatformPeers = new();

	static readonly MethodInfo DisposeSwipeItemsMethod =
		typeof(PlatformSwipeView).GetMethod("DisposeSwipeItems", BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly FieldInfo SwipeItemsField =
		typeof(PlatformSwipeView).GetField("_swipeItems", BindingFlags.Instance | BindingFlags.NonPublic)!;

	public static Task<ReproReport> RunAsync(IMauiContext context, Action<string>? progress = null)
	{
		RetainedPlatformPeers.Clear();
		progress?.Invoke("Cleared retained platform peers.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
		progress?.Invoke($"Baseline managed heap: {baselineBytes:N0} bytes.");

		var control = RunScenario(
			"control: open swipe items, clear platform swipe state and known owner fields, then disconnect",
			context,
			clearPlatformSwipeState: true,
			progress);

		var current = RunScenario(
			"current: open swipe items, disconnect and clear known owner fields while platform swipe state remains",
			context,
			clearPlatformSwipeState: false,
			progress);

		ForceFullGc();
		GC.KeepAlive(RetainedPlatformPeers);
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		progress?.Invoke($"Final managed heap: {finalBytes:N0} bytes.");

		return Task.FromResult(new ReproReport(
			Cycles,
			PayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current));
	}

	static ScenarioResult RunScenario(string name, IMauiContext context, bool clearPlatformSwipeState, Action<string>? progress)
	{
		progress?.Invoke($"Starting {name}.");
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisconnectedSwipeViewCycle(i, context, tracked, clearPlatformSwipeState);
			if ((i + 1) % 10 == 0 || i == Cycles - 1)
				progress?.Invoke($"{name}: created {i + 1}/{Cycles} disconnected swipe views.");
		}

		ForceFullGc();

		var result = ScenarioResult.From(name, tracked);
		progress?.Invoke($"{name}: retained {result.AlivePayloadByteArrays}/{result.TrackedCycles} payload byte arrays.");
		return result;
	}

	static void CreateDisconnectedSwipeViewCycle(
		int cycle,
		IMauiContext context,
		List<TrackedCycle> tracked,
		bool clearPlatformSwipeState)
	{
		var payload = new LeakPayload(cycle, PayloadBytesPerCycle);
		var swipeItem = new SwipeItem
		{
			Text = $"Archive {cycle + 1}",
			BackgroundColor = Colors.OrangeRed,
			CommandParameter = payload
		};
		var swipeItems = new SwipeItems
		{
			swipeItem
		};
		swipeItems.Mode = SwipeMode.Reveal;
		swipeItems.SwipeBehaviorOnInvoked = SwipeBehaviorOnInvoked.RemainOpen;

		var rowContent = CreateRowContent(cycle);
		var swipeView = new SwipeView
		{
			Threshold = 24,
			Content = rowContent,
			RightItems = swipeItems
		};

		var platformView = (PlatformSwipeView)swipeView.ToPlatform(context);
		platformView.Measure(
			AView.MeasureSpec.MakeMeasureSpec(360, MeasureSpecMode.Exactly),
			AView.MeasureSpec.MakeMeasureSpec(72, MeasureSpecMode.Exactly));
		platformView.Layout(0, 0, 360, 72);

		swipeView.Open(OpenSwipeItem.RightItems, animated: false);

		if (clearPlatformSwipeState)
			DisposeSwipeItemsMethod.Invoke(platformView, null);

		rowContent.Handler?.DisconnectHandler();
		swipeView.Content = null;
		swipeView.RightItems = new SwipeItems();

		var handler = swipeView.Handler;
		handler?.DisconnectHandler();

		// Clear the separate C129 owner-field leak in both scenarios, so retained payloads
		// here must come from the materialized platform swipe state.
		ClearKnownOwnerFields(platformView);

		RetainedPlatformPeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(cycle, platformView, handler, swipeView, swipeItem, payload));
	}

	static Grid CreateRowContent(int cycle)
	{
		return new Grid
		{
			WidthRequest = 360,
			HeightRequest = 72,
			Padding = new Thickness(12),
			BackgroundColor = Colors.WhiteSmoke,
			Children =
			{
				new Label
				{
					Text = $"Order #{cycle + 1:0000}",
					TextColor = Colors.Black,
					VerticalOptions = LayoutOptions.Center
				}
			}
		};
	}

	static int GetPlatformSwipeItemsCount(PlatformSwipeView platformView)
	{
		return SwipeItemsField.GetValue(platformView) is IDictionary dictionary ? dictionary.Count : -1;
	}

	static void ClearKnownOwnerFields(PlatformSwipeView platformView)
	{
		platformView.RemoveAllViews();
		SetPropertyIfPresent(platformView, "CrossPlatformLayout", null);
		SetPropertyIfPresent(platformView, "Clip", null);
		SetFieldIfPresent(platformView, "_clip", null);
		SetFieldIfPresent(platformView, "<Element>k__BackingField", null);
		SetFieldIfPresent(platformView, "_content", null);
		SetFieldIfPresent(platformView, "_contentView", null);
	}

	static void SetPropertyIfPresent(object target, string name, object? value)
	{
		var property = FindProperty(target.GetType(), name);
		if (property is not null && property.CanWrite)
			property.SetValue(target, value);
	}

	static void SetFieldIfPresent(object target, string name, object? value)
	{
		var field = FindField(target.GetType(), name);
		if (field is not null)
			field.SetValue(target, value);
	}

	static PropertyInfo? FindProperty(Type type, string name)
	{
		for (var current = type; current != null; current = current.BaseType)
		{
			var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property is not null)
				return property;
		}

		return null;
	}

	static FieldInfo? FindField(Type type, string name)
	{
		for (var current = type; current != null; current = current.BaseType)
		{
			var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field is not null)
				return field;
		}

		return null;
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

	internal sealed class LeakPayload
	{
		public LeakPayload(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			SessionBytes = new byte[payloadBytes];

			for (var i = 0; i < SessionBytes.Length; i += 4096)
				SessionBytes[i] = (byte)((cycle + i) % 251);

			Rows = Enumerable.Range(1, 24)
				.Select(index => new RowState(
					$"swipe-row-{cycle + 1:000}-{index:000}",
					$"Action payload {index}",
					$"Permissions, undo, telemetry, and offline state {cycle + 1}.{index}"))
				.ToArray();
		}

		public int Cycle { get; }

		public int PayloadBytes { get; }

		public byte[] SessionBytes { get; }

		public IReadOnlyList<RowState> Rows { get; }
	}

	internal sealed record RowState(string Id, string Title, string UiState);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<PlatformSwipeView> PlatformView,
		WeakReference<IElementHandler> Handler,
		WeakReference<SwipeView> SwipeView,
		WeakReference<SwipeItem> SwipeItem,
		WeakReference<LeakPayload> Payload,
		WeakReference<byte[]> PayloadBytes,
		long PayloadSize)
	{
		public static TrackedCycle Create(
			int cycle,
			PlatformSwipeView platformView,
			IElementHandler? handler,
			SwipeView swipeView,
			SwipeItem swipeItem,
			LeakPayload payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<PlatformSwipeView>(platformView),
				new WeakReference<IElementHandler>(handler!),
				new WeakReference<SwipeView>(swipeView),
				new WeakReference<SwipeItem>(swipeItem),
				new WeakReference<LeakPayload>(payload),
				new WeakReference<byte[]>(payload.SessionBytes),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int PlatformPeersWithSwipeItems,
		int PlatformSwipeItemsRetained,
		int AlivePlatformViews,
		int AliveHandlers,
		int AliveSwipeViews,
		int AliveSwipeItems,
		int AlivePayloads,
		int AlivePayloadByteArrays,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
		{
			var platformPeersWithSwipeItems = 0;
			var platformSwipeItemsRetained = 0;
			var alivePlatformViews = 0;
			var aliveHandlers = 0;
			var aliveSwipeViews = 0;
			var aliveSwipeItems = 0;
			var alivePayloads = 0;
			var alivePayloadByteArrays = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.PlatformView.TryGetTarget(out var platformView))
				{
					alivePlatformViews++;
					var count = GetPlatformSwipeItemsCount(platformView);
					if (count > 0)
					{
						platformPeersWithSwipeItems++;
						platformSwipeItemsRetained += count;
					}
				}

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.SwipeView.TryGetTarget(out _))
					aliveSwipeViews++;

				if (cycle.SwipeItem.TryGetTarget(out _))
					aliveSwipeItems++;

				if (cycle.Payload.TryGetTarget(out _))
					alivePayloads++;

				if (cycle.PayloadBytes.TryGetTarget(out _))
				{
					alivePayloadByteArrays++;
					retainedPayloadBytes += cycle.PayloadSize;
				}
			}

			return new ScenarioResult(
				name,
				cycles.Count,
				platformPeersWithSwipeItems,
				platformSwipeItemsRetained,
				alivePlatformViews,
				aliveHandlers,
				aliveSwipeViews,
				aliveSwipeItems,
				alivePayloads,
				alivePayloadByteArrays,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveSwipeViews == 0 &&
		Current.PlatformPeersWithSwipeItems == Cycles &&
		Current.PlatformSwipeItemsRetained == Cycles &&
		Current.AliveSwipeItems == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.AlivePayloadByteArrays == Cycles;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidMauiSwipeViewNativePeerRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per swipe item: {PayloadBytesPerCycle / 1024 / 1024} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {controlMiB:N1} MiB",
			$"Current retained payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  platform peers with swipe items: {result.PlatformPeersWithSwipeItems}/{result.TrackedCycles}",
			$"  platform swipe items retained: {result.PlatformSwipeItemsRetained}/{result.TrackedCycles}",
			$"  alive platform views: {result.AlivePlatformViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive SwipeViews: {result.AliveSwipeViews}/{result.TrackedCycles}",
			$"  alive SwipeItems: {result.AliveSwipeItems}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadByteArrays}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
