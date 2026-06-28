using System.Reflection;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items2;
using UIKit;

namespace Items2LayoutHeaderFooterRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly List<IReadOnlyList<UICollectionViewLayout>> RetainedNativeLayoutPeers = new();

	static readonly Type LayoutFactoryType = typeof(CollectionViewHandler2).Assembly.GetType(
		"Microsoft.Maui.Controls.Handlers.Items2.LayoutFactory2",
		throwOnError: true)!;

	static readonly Type LayoutGroupingInfoType = typeof(CollectionViewHandler2).Assembly.GetType(
		"Microsoft.Maui.Controls.Handlers.Items2.LayoutGroupingInfo",
		throwOnError: true)!;

	static readonly Type LayoutHeaderFooterInfoType = typeof(CollectionViewHandler2).Assembly.GetType(
		"Microsoft.Maui.Controls.Handlers.Items2.LayoutHeaderFooterInfo",
		throwOnError: true)!;

	static readonly MethodInfo CreateGridMethod = LayoutFactoryType.GetMethod(
		"CreateGrid",
		BindingFlags.Public | BindingFlags.Static,
		binder: null,
		types: new[] { typeof(GridItemsLayout), LayoutGroupingInfoType, LayoutHeaderFooterInfoType },
		modifiers: null)!;

	static readonly PropertyInfo HeaderViewProperty = LayoutHeaderFooterInfoType.GetProperty("HeaderView")!;
	static readonly PropertyInfo FooterViewProperty = LayoutHeaderFooterInfoType.GetProperty("FooterView")!;
	static readonly PropertyInfo HasHeaderProperty = LayoutHeaderFooterInfoType.GetProperty("HasHeader")!;
	static readonly PropertyInfo HasFooterProperty = LayoutHeaderFooterInfoType.GetProperty("HasFooter")!;

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "items2-layout-headerfooter-retention-results.txt");

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario("control: dispose native layout and clear stale header/footer info", clearHeaderFooterAfterDispose: true);
		var current = RunScenario("current: dispose native layout with header/footer info still assigned", clearHeaderFooterAfterDispose: false);

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

	static ScenarioResult RunScenario(string name, bool clearHeaderFooterAfterDispose)
	{
		var tracking = RunScenarioCore(clearHeaderFooterAfterDispose);
		RetainedNativeLayoutPeers.Add(tracking.Layouts);

		ForceFullGc();

		return ScenarioResult.From(name, tracking.Layouts, tracking.TrackedCycles);
	}

	static ScenarioTracking RunScenarioCore(bool clearHeaderFooterAfterDispose)
	{
		var layouts = new List<UICollectionViewLayout>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisposedLayoutCycle(i, layouts, tracked, clearHeaderFooterAfterDispose);
		}

		return new ScenarioTracking(layouts, tracked);
	}

	static void CreateDisposedLayoutCycle(
		int cycle,
		List<UICollectionViewLayout> layouts,
		List<TrackedCycle> tracked,
		bool clearHeaderFooterAfterDispose)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var headerView = new PayloadHeaderFooterView(cycle, "Header", payload);
		var footerView = new PayloadHeaderFooterView(cycle, "Footer", payload);
		var itemsLayout = new GridItemsLayout(2, ItemsLayoutOrientation.Vertical)
		{
			HorizontalItemSpacing = 8,
			VerticalItemSpacing = 12,
			SnapPointsType = SnapPointsType.MandatorySingle,
			SnapPointsAlignment = SnapPointsAlignment.Start
		};
		var headerFooterInfo = CreateHeaderFooterInfo(headerView, footerView);

		var platformLayout = CreateItems2GridLayout(itemsLayout, headerFooterInfo);
		platformLayout.Dispose();

		if (clearHeaderFooterAfterDispose)
			ClearHeaderFooterInfo(platformLayout);

		layouts.Add(platformLayout);
		tracked.Add(TrackedCycle.Create(cycle, platformLayout, headerView, footerView, payload));
	}

	static object CreateHeaderFooterInfo(PayloadHeaderFooterView headerView, PayloadHeaderFooterView footerView)
	{
		var headerFooterInfo = Activator.CreateInstance(LayoutHeaderFooterInfoType, nonPublic: true)!;
		HeaderViewProperty.SetValue(headerFooterInfo, headerView);
		FooterViewProperty.SetValue(headerFooterInfo, footerView);
		HasHeaderProperty.SetValue(headerFooterInfo, true);
		HasFooterProperty.SetValue(headerFooterInfo, true);
		return headerFooterInfo;
	}

	static UICollectionViewLayout CreateItems2GridLayout(GridItemsLayout itemsLayout, object headerFooterInfo)
	{
		var groupingInfo = Activator.CreateInstance(LayoutGroupingInfoType, nonPublic: true)!;
		return (UICollectionViewLayout)CreateGridMethod.Invoke(null, new[] { itemsLayout, groupingInfo, headerFooterInfo })!;
	}

	static void ClearHeaderFooterInfo(UICollectionViewLayout layout)
	{
		if (GetHeaderFooterInfo(layout) is { } headerFooterInfo)
		{
			HeaderViewProperty.SetValue(headerFooterInfo, null);
			FooterViewProperty.SetValue(headerFooterInfo, null);
		}

		HeaderFooterInfoField(layout).SetValue(layout, null);
	}

	static object? GetHeaderFooterInfo(UICollectionViewLayout layout)
	{
		return HeaderFooterInfoField(layout).GetValue(layout);
	}

	static object? GetHeaderView(UICollectionViewLayout layout)
	{
		return GetHeaderFooterInfo(layout) is { } info ? HeaderViewProperty.GetValue(info) : null;
	}

	static object? GetFooterView(UICollectionViewLayout layout)
	{
		return GetHeaderFooterInfo(layout) is { } info ? FooterViewProperty.GetValue(info) : null;
	}

	static FieldInfo HeaderFooterInfoField(UICollectionViewLayout layout)
	{
		return layout.GetType().GetField("_headerFooterInfo", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"Layout type {layout.GetType().FullName} does not expose _headerFooterInfo.");
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

	internal sealed class PayloadHeaderFooterView : ContentView
	{
		public PayloadHeaderFooterView(int cycle, string role, LeakPayload payload)
		{
			Cycle = cycle;
			Role = role;
			BindingContext = payload;
			Payload = payload;
			Content = new Label { Text = $"{role} {cycle}" };
		}

		public int Cycle { get; }

		public string Role { get; }

		public LeakPayload Payload { get; }
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

			HeaderFooterState = Enumerable.Range(1, 12)
				.Select(index => new HeaderFooterState(
					$"widget-{cycle + 1:000}-{index:000}",
					$"Header/footer dashboard widget {index}",
					$"Filter, command, and visual state payload {cycle + 1}.{index}"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] SessionBytes { get; }

		public IReadOnlyList<HeaderFooterState> HeaderFooterState { get; }
	}

	internal sealed record HeaderFooterState(string Id, string Title, string UiState);

	internal sealed record ScenarioTracking(
		IReadOnlyList<UICollectionViewLayout> Layouts,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference NativeLayout,
		WeakReference HeaderView,
		WeakReference FooterView,
		WeakReference Payload,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			UICollectionViewLayout nativeLayout,
			PayloadHeaderFooterView headerView,
			PayloadHeaderFooterView footerView,
			LeakPayload payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference(nativeLayout),
				new WeakReference(headerView),
				new WeakReference(footerView),
				new WeakReference(payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int RetainedNativeLayoutPeers,
		int TrackedCycles,
		int LayoutsWithHeaderFooterInfoAssigned,
		int LayoutsWithHeaderViewAssigned,
		int LayoutsWithFooterViewAssigned,
		int AliveNativeLayouts,
		int AliveHeaderViews,
		int AliveFooterViews,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(
			string name,
			IReadOnlyList<UICollectionViewLayout> layouts,
			IReadOnlyList<TrackedCycle> cycles)
		{
			var layoutsWithHeaderFooterInfoAssigned = 0;
			var layoutsWithHeaderViewAssigned = 0;
			var layoutsWithFooterViewAssigned = 0;

			foreach (var layout in layouts)
			{
				if (GetHeaderFooterInfo(layout) is not null)
					layoutsWithHeaderFooterInfoAssigned++;
				if (GetHeaderView(layout) is not null)
					layoutsWithHeaderViewAssigned++;
				if (GetFooterView(layout) is not null)
					layoutsWithFooterViewAssigned++;
			}

			var aliveNativeLayouts = 0;
			var aliveHeaderViews = 0;
			var aliveFooterViews = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.NativeLayout.IsAlive)
					aliveNativeLayouts++;
				if (cycle.HeaderView.IsAlive)
					aliveHeaderViews++;
				if (cycle.FooterView.IsAlive)
					aliveFooterViews++;
				if (cycle.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				layouts.Count,
				cycles.Count,
				layoutsWithHeaderFooterInfoAssigned,
				layoutsWithHeaderViewAssigned,
				layoutsWithFooterViewAssigned,
				aliveNativeLayouts,
				aliveHeaderViews,
				aliveFooterViews,
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
			Control.RetainedNativeLayoutPeers == Cycles &&
			Control.AliveNativeLayouts == Cycles &&
			Control.LayoutsWithHeaderFooterInfoAssigned == 0 &&
			Control.LayoutsWithHeaderViewAssigned == 0 &&
			Control.LayoutsWithFooterViewAssigned == 0 &&
			Control.AliveHeaderViews == 0 &&
			Control.AliveFooterViews == 0 &&
			Control.AlivePayloads == 0 &&
			Current.RetainedNativeLayoutPeers == Cycles &&
			Current.AliveNativeLayouts == Cycles &&
			Current.LayoutsWithHeaderFooterInfoAssigned == Cycles &&
			Current.LayoutsWithHeaderViewAssigned == Cycles &&
			Current.LayoutsWithFooterViewAssigned == Cycles &&
			Current.AliveHeaderViews == Cycles &&
			Current.AliveFooterViews == Cycles &&
			Current.AlivePayloads == Cycles;

		public string ToText()
		{
			return string.Join(Environment.NewLine, new[]
			{
				"Items2 layout header/footer retention repro",
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
				$"  retainedNativeLayoutPeers={result.RetainedNativeLayoutPeers}",
				$"  trackedCycles={result.TrackedCycles}",
				$"  layoutsWithHeaderFooterInfoAssigned={result.LayoutsWithHeaderFooterInfoAssigned}/{result.TrackedCycles}",
				$"  layoutsWithHeaderViewAssigned={result.LayoutsWithHeaderViewAssigned}/{result.TrackedCycles}",
				$"  layoutsWithFooterViewAssigned={result.LayoutsWithFooterViewAssigned}/{result.TrackedCycles}",
				$"  aliveNativeLayouts={result.AliveNativeLayouts}/{result.TrackedCycles}",
				$"  aliveHeaderViews={result.AliveHeaderViews}/{result.TrackedCycles}",
				$"  aliveFooterViews={result.AliveFooterViews}/{result.TrackedCycles}",
				$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
				$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
				$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
			});
		}
	}
}
