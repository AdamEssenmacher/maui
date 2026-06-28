using System.Collections;
using System.Reflection;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using UIKit;
using PlatformSwipeView = Microsoft.Maui.Platform.MauiSwipeView;

namespace MauiSwipeViewNativePeerRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly List<IReadOnlyList<PlatformSwipeView>> RetainedPlatformPeers = new();

	static readonly MethodInfo DisposeSwipeItemsMethod =
		typeof(PlatformSwipeView).GetMethod("DisposeSwipeItems", BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly FieldInfo SwipeItemsField =
		typeof(PlatformSwipeView).GetField("_swipeItems", BindingFlags.Instance | BindingFlags.NonPublic)!;

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "mauiswipeview-native-peer-retention-results.txt");

	public static ReproReport Run(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario(
			"control: open swipe items, explicitly clear platform swipe state, then disconnect",
			context,
			clearPlatformSwipeStateBeforeDisconnect: true);

		var current = RunScenario(
			"current: open swipe items, disconnect with platform swipe state still assigned",
			context,
			clearPlatformSwipeStateBeforeDisconnect: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		IMauiContext context,
		bool clearPlatformSwipeStateBeforeDisconnect)
	{
		var tracking = RunScenarioCore(context, clearPlatformSwipeStateBeforeDisconnect);
		RetainedPlatformPeers.Add(tracking.PlatformViews);

		ForceFullGc();

		return ScenarioResult.From(name, tracking.PlatformViews, tracking.TrackedCycles);
	}

	static ScenarioTracking RunScenarioCore(IMauiContext context, bool clearPlatformSwipeStateBeforeDisconnect)
	{
		var platformViews = new List<PlatformSwipeView>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisconnectedSwipeViewCycle(i, context, platformViews, tracked, clearPlatformSwipeStateBeforeDisconnect);
		}

		return new ScenarioTracking(platformViews, tracked);
	}

	static void CreateDisconnectedSwipeViewCycle(
		int cycle,
		IMauiContext context,
		List<PlatformSwipeView> platformViews,
		List<TrackedCycle> tracked,
		bool clearPlatformSwipeStateBeforeDisconnect)
	{
		using var pool = new NSAutoreleasePool();

		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
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

		var swipeView = new SwipeView
		{
			Threshold = 24,
			Content = CreateRowContent(cycle),
			RightItems = swipeItems
		};

		var platformView = (PlatformSwipeView)swipeView.ToPlatform(context);
		platformView.Frame = new CGRect(0, 0, 360, 72);
		platformView.LayoutSubviews();

		swipeView.Open(OpenSwipeItem.RightItems, animated: false);

		if (clearPlatformSwipeStateBeforeDisconnect)
			DisposeSwipeItemsMethod.Invoke(platformView, null);

		var handler = swipeView.Handler;
		handler?.DisconnectHandler();

		platformViews.Add(platformView);
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
		public LeakPayload(int cycle, long payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			SessionBytes = new byte[payloadBytes];

			for (var i = 0; i < SessionBytes.Length; i += 4096)
				SessionBytes[i] = (byte)(cycle + i);

			Rows = Enumerable.Range(1, 24)
				.Select(index => new RowState(
					$"swipe-row-{cycle + 1:000}-{index:000}",
					$"Action payload {index}",
					$"Permissions, undo, telemetry, and offline state {cycle + 1}.{index}"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] SessionBytes { get; }

		public IReadOnlyList<RowState> Rows { get; }
	}

	internal sealed record RowState(string Id, string Title, string UiState);

	internal sealed record ScenarioTracking(
		IReadOnlyList<PlatformSwipeView> PlatformViews,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference PlatformView,
		WeakReference Handler,
		WeakReference SwipeView,
		WeakReference SwipeItem,
		WeakReference Payload,
		long PayloadBytes)
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
				new WeakReference(platformView),
				new WeakReference(handler!),
				new WeakReference(swipeView),
				new WeakReference(swipeItem),
				new WeakReference(payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int RetainedPlatformPeers,
		int TrackedCycles,
		int PlatformPeersWithSwipeItems,
		int PlatformSwipeItemsRetained,
		int AlivePlatformViews,
		int AliveHandlers,
		int AliveSwipeViews,
		int AliveSwipeItems,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(
			string name,
			IReadOnlyList<PlatformSwipeView> platformViews,
			IReadOnlyList<TrackedCycle> cycles)
		{
			var platformPeersWithSwipeItems = 0;
			var platformSwipeItemsRetained = 0;

			foreach (var platformView in platformViews)
			{
				var count = GetPlatformSwipeItemsCount(platformView);
				if (count > 0)
				{
					platformPeersWithSwipeItems++;
					platformSwipeItemsRetained += count;
				}
			}

			var alivePlatformViews = 0;
			var aliveHandlers = 0;
			var aliveSwipeViews = 0;
			var aliveSwipeItems = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.PlatformView.IsAlive)
					alivePlatformViews++;
				if (cycle.Handler.IsAlive)
					aliveHandlers++;
				if (cycle.SwipeView.IsAlive)
					aliveSwipeViews++;
				if (cycle.SwipeItem.IsAlive)
					aliveSwipeItems++;
				if (cycle.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				platformViews.Count,
				cycles.Count,
				platformPeersWithSwipeItems,
				platformSwipeItemsRetained,
				alivePlatformViews,
				aliveHandlers,
				aliveSwipeViews,
				aliveSwipeItems,
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
		ScenarioResult Current)
	{
		public bool Proven =>
			Control.RetainedPlatformPeers == Cycles &&
			Control.AlivePlatformViews == Cycles &&
			Control.PlatformPeersWithSwipeItems == 0 &&
			Control.PlatformSwipeItemsRetained == 0 &&
			Control.AliveSwipeItems == 0 &&
			Control.AlivePayloads == 0 &&
			Current.RetainedPlatformPeers == Cycles &&
			Current.AlivePlatformViews == Cycles &&
			Current.PlatformPeersWithSwipeItems == Cycles &&
			Current.PlatformSwipeItemsRetained == Cycles &&
			Current.AliveSwipeItems == Cycles &&
			Current.AlivePayloads == Cycles;

		public string ToText()
		{
			return string.Join(Environment.NewLine, new[]
			{
				"MauiSwipeView native peer retention repro",
				$"RESULT: {(Proven ? "PROVEN" : "NOT PROVEN")}",
				$"cycles={Cycles}",
				$"payloadMegabytesPerCycle={PayloadMegabytesPerCycle}",
				$"baselineManagedBytes={BaselineManagedBytes}",
				$"finalManagedBytes={FinalManagedBytes}",
				Format(Control),
				Format(Current),
			});
		}

		static string Format(ScenarioResult result)
		{
			return string.Join(Environment.NewLine, new[]
			{
				$"scenario={result.Name}",
				$"  retainedPlatformPeers={result.RetainedPlatformPeers}",
				$"  trackedCycles={result.TrackedCycles}",
				$"  platformPeersWithSwipeItems={result.PlatformPeersWithSwipeItems}/{result.TrackedCycles}",
				$"  platformSwipeItemsRetained={result.PlatformSwipeItemsRetained}/{result.TrackedCycles}",
				$"  alivePlatformViews={result.AlivePlatformViews}/{result.TrackedCycles}",
				$"  aliveHandlers={result.AliveHandlers}/{result.TrackedCycles}",
				$"  aliveSwipeViews={result.AliveSwipeViews}/{result.TrackedCycles}",
				$"  aliveSwipeItems={result.AliveSwipeItems}/{result.TrackedCycles}",
				$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
				$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
				$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
			});
		}
	}
}
