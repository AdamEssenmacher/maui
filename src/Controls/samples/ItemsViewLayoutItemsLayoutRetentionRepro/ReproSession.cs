using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;

namespace ItemsViewLayoutItemsLayoutRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly List<IReadOnlyList<GridViewLayout>> RetainedNativeLayoutPeers = new();

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "itemsviewlayout-itemslayout-retention-results.txt");

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario("control: dispose native layout and clear stale ItemsLayout fields", clearItemsLayoutAfterDispose: true);
		var current = RunScenario("current: dispose native layout with ItemsLayout fields still assigned", clearItemsLayoutAfterDispose: false);

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

	static ScenarioResult RunScenario(string name, bool clearItemsLayoutAfterDispose)
	{
		var tracking = RunScenarioCore(clearItemsLayoutAfterDispose);
		RetainedNativeLayoutPeers.Add(tracking.Layouts);

		ForceFullGc();

		return ScenarioResult.From(name, tracking.Layouts, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(bool clearItemsLayoutAfterDispose)
	{
		var layouts = new List<GridViewLayout>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisposedLayoutCycle(i, layouts, tracked, clearItemsLayoutAfterDispose);
		}

		return new ScenarioTracking(layouts, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedLayoutCycle(
		int cycle,
		List<GridViewLayout> layouts,
		List<TrackedCycle> tracked,
		bool clearItemsLayoutAfterDispose)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var itemsLayout = new PayloadGridItemsLayout(cycle, payload)
		{
			HorizontalItemSpacing = 8,
			VerticalItemSpacing = 12,
			SnapPointsType = SnapPointsType.MandatorySingle,
			SnapPointsAlignment = SnapPointsAlignment.Start
		};

		var platformLayout = new GridViewLayout(itemsLayout, ItemSizingStrategy.MeasureAllItems);
		platformLayout.ConstrainTo(new CGSize(390, 844));
		platformLayout.Dispose();

		if (clearItemsLayoutAfterDispose)
		{
			GridItemsLayoutField(platformLayout) = null!;
			BaseItemsLayoutField(platformLayout) = null!;
		}

		layouts.Add(platformLayout);
		tracked.Add(TrackedCycle.Create(cycle, platformLayout, itemsLayout, payload));
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_itemsLayout")]
	static extern ref ItemsLayout BaseItemsLayoutField(ItemsViewLayout layout);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_itemsLayout")]
	static extern ref GridItemsLayout GridItemsLayoutField(GridViewLayout layout);

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}
}

sealed class PayloadGridItemsLayout : GridItemsLayout
{
	public PayloadGridItemsLayout(int cycle, LeakPayload payload)
		: base(2, ItemsLayoutOrientation.Vertical)
	{
		Cycle = cycle;
		LayoutState = payload;
		BindingContext = payload;
	}

	public int Cycle { get; }

	public LeakPayload LayoutState { get; }
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

		VisibleSections = Enumerable.Range(1, 24)
			.Select(index => new LayoutSectionState(
				$"section-{cycle + 1:000}-{index:000}",
				$"Visible analytics lane {index}",
				$"Sort, filter, span, and realized-size cache state {cycle + 1}.{index}"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] SessionBytes { get; }

	public IReadOnlyList<LayoutSectionState> VisibleSections { get; }
}

internal sealed record LayoutSectionState(string Id, string Title, string UiState);

internal sealed record ScenarioTracking(
	IReadOnlyList<GridViewLayout> Layouts,
	IReadOnlyList<TrackedCycle> TrackedCycles);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference NativeLayout,
	WeakReference ItemsLayout,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		GridViewLayout nativeLayout,
		PayloadGridItemsLayout itemsLayout,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(nativeLayout),
			new WeakReference(itemsLayout),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int RetainedNativeLayoutPeers,
	int TrackedCycles,
	int LayoutsWithBaseItemsLayoutAssigned,
	int LayoutsWithGridItemsLayoutAssigned,
	int AliveNativeLayouts,
	int AliveItemsLayouts,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(
		string name,
		IReadOnlyList<GridViewLayout> layouts,
		IReadOnlyList<TrackedCycle> cycles)
	{
		var layoutsWithBaseItemsLayoutAssigned = 0;
		var layoutsWithGridItemsLayoutAssigned = 0;

		foreach (var layout in layouts)
		{
			if (BaseItemsLayoutField(layout) is not null)
				layoutsWithBaseItemsLayoutAssigned++;
			if (GridItemsLayoutField(layout) is not null)
				layoutsWithGridItemsLayoutAssigned++;
		}

		var aliveNativeLayouts = 0;
		var aliveItemsLayouts = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.NativeLayout.IsAlive)
				aliveNativeLayouts++;
			if (cycle.ItemsLayout.IsAlive)
				aliveItemsLayouts++;
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
			layoutsWithBaseItemsLayoutAssigned,
			layoutsWithGridItemsLayoutAssigned,
			aliveNativeLayouts,
			aliveItemsLayouts,
			alivePayloads,
			retainedPayloadBytes);
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_itemsLayout")]
	static extern ref ItemsLayout BaseItemsLayoutField(ItemsViewLayout layout);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_itemsLayout")]
	static extern ref GridItemsLayout GridItemsLayoutField(GridViewLayout layout);
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
		Control.LayoutsWithBaseItemsLayoutAssigned == 0 &&
		Control.LayoutsWithGridItemsLayoutAssigned == 0 &&
		Control.AliveItemsLayouts == 0 &&
		Control.AlivePayloads == 0 &&
		Current.RetainedNativeLayoutPeers == Cycles &&
		Current.AliveNativeLayouts == Cycles &&
		Current.LayoutsWithBaseItemsLayoutAssigned == Cycles &&
		Current.LayoutsWithGridItemsLayoutAssigned == Cycles &&
		Current.AliveItemsLayouts == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine, new[]
		{
			"ItemsViewLayout ItemsLayout retention repro",
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
			$"  layoutsWithBaseItemsLayoutAssigned={result.LayoutsWithBaseItemsLayoutAssigned}/{result.TrackedCycles}",
			$"  layoutsWithGridItemsLayoutAssigned={result.LayoutsWithGridItemsLayoutAssigned}/{result.TrackedCycles}",
			$"  aliveNativeLayouts={result.AliveNativeLayouts}/{result.TrackedCycles}",
			$"  aliveItemsLayouts={result.AliveItemsLayouts}/{result.TrackedCycles}",
			$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
			$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
			$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
		});
	}
}
