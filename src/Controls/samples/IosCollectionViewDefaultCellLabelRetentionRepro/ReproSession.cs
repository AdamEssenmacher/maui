#nullable enable

#pragma warning disable CS0618

using System.Text;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;
using Microsoft.Maui.Controls.Handlers.Items2;
using UIKit;

using LegacyDefaultCell = Microsoft.Maui.Controls.Handlers.Items.DefaultCell;

namespace IosCollectionViewDefaultCellLabelRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerText = 256;
	internal const int CellsPerCycle = 2;

	const long PayloadBytesPerText = PayloadKiBPerText * 1024L;

	static readonly List<RetainedCell> RetainedCells = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-collectionview-defaultcell-label-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS CollectionView default-cell label retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear retained default-cell UILabel.Text slots",
			clearNativeText: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: default cells leave UILabel.Text assigned",
			clearNativeText: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(context);
		GC.KeepAlive(RetainedCells);

		return new ReproReport(
			Cycles,
			PayloadKiBPerText,
			CellsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearNativeText)
	{
		var retainedCells = new List<RetainedCell>(Cycles * CellsPerCycle);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, clearNativeText);
			retainedCells.Add(cycleResult.LegacyCell);
			retainedCells.Add(cycleResult.Items2Cell);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedCells.AddRange(retainedCells);
		ForceFullGc();

		return ScenarioResult.From(name, retainedCells, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(int cycle, bool clearNativeText)
	{
		var legacyItem = new PayloadItem(CreateLargeText("legacy CollectionView default item", cycle));
		var legacyItems = new List<PayloadItem> { legacyItem };
		var legacyItemsView = new CollectionView
		{
			ItemsLayout = LinearItemsLayout.Vertical,
			ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
			ItemsSource = legacyItems
		};
		var legacyLayout = new ListViewLayout((LinearItemsLayout)legacyItemsView.ItemsLayout, legacyItemsView.ItemSizingStrategy);
		var legacyController = new ExposedItemsViewController(legacyItemsView, legacyLayout);
		legacyController.InitializeForTest();

		var legacyCell = new TestDefaultCell(new CGRect(0, 0, 360, 44));
		legacyController.ApplyDefaultText(legacyCell, NSIndexPath.FromItemSection(0, 0));

		if (EstimateTextBytes(legacyCell.Label.Text) < PayloadBytesPerText * 0.95)
			throw new InvalidOperationException("Legacy ItemsViewController did not assign the payload-sized native label text.");

		legacyItem.Clear();
		legacyItems[0] = new PayloadItem(string.Empty);
		legacyItemsView.ItemsSource = null;

		var items2Item = new PayloadItem(CreateLargeText("Items2 CollectionView default item", cycle));
		var items2Cell = new DefaultCell2(new CGRect(0, 0, 360, 44));
		items2Cell.Label.Text = items2Item.ToString();

		if (EstimateTextBytes(items2Cell.Label.Text) < PayloadBytesPerText * 0.95)
			throw new InvalidOperationException("Items2 DefaultCell2 did not assign the payload-sized native label text.");

		items2Item.Clear();

		if (clearNativeText)
		{
			legacyCell.Label.Text = string.Empty;
			items2Cell.Label.Text = string.Empty;
		}

		var tracked = TrackedCycle.Create(
			cycle,
			legacyController,
			legacyItemsView,
			legacyItems,
			legacyItem,
			items2Item);

		legacyController.Dispose();
		await DrainMainQueueAsync();

		GC.KeepAlive(legacyCell);
		GC.KeepAlive(items2Cell);

		return new CycleResult(
			new RetainedCell(CellFamily.LegacyItemsView, legacyCell, legacyCell.Label),
			new RetainedCell(CellFamily.Items2, items2Cell, items2Cell.Label),
			tracked);
	}

	static string CreateLargeText(string prefix, int cycle)
	{
		var header = $"{prefix} {cycle:000}. ";
		var sentence = "Imported work order note, search result summary, customer activity log, and support timeline. ";
		var targetChars = (int)(PayloadBytesPerText / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(30);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
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

	static int CountAssignedPayloadTexts(RetainedCell cell)
	{
		return EstimateTextBytes(cell.Label.Text) >= PayloadBytesPerText * 0.95 ? 1 : 0;
	}

	static long EstimateAssignedTextBytes(RetainedCell cell)
	{
		return Math.Min(EstimateTextBytes(cell.Label.Text), PayloadBytesPerText);
	}

	static long EstimateTextBytes(string? text)
	{
		return string.IsNullOrEmpty(text) ? 0 : text.Length * 2L;
	}

	internal enum CellFamily
	{
		LegacyItemsView,
		Items2
	}

	internal sealed class PayloadItem
	{
		string _text;

		public PayloadItem(string text)
		{
			_text = text;
		}

		public void Clear()
		{
			_text = string.Empty;
		}

		public override string ToString() => _text;
	}

	internal sealed class ExposedItemsViewController : ItemsViewController<CollectionView>
	{
		public ExposedItemsViewController(CollectionView itemsView, ItemsViewLayout layout)
			: base(itemsView, layout)
		{
		}

		protected override bool IsHorizontal => false;

		public void InitializeForTest()
		{
			LoadView();
			ViewDidLoad();
		}

		public void ApplyDefaultText(LegacyDefaultCell cell, NSIndexPath indexPath)
		{
			UpdateDefaultCell(cell, indexPath);
		}
	}

	internal sealed class TestDefaultCell : LegacyDefaultCell
	{
		public TestDefaultCell(CGRect frame)
			: base(frame)
		{
			Constraint = Label.WidthAnchor.ConstraintEqualTo(Frame.Width);
			Constraint.Priority = (float)UILayoutPriority.DefaultHigh;
			Constraint.Active = true;
		}

		public override void ConstrainTo(nfloat constant)
		{
			Constraint.Constant = constant;
		}

		public override void ConstrainTo(CGSize constraint)
		{
			Constraint.Constant = constraint.Width;
		}

		public override CGSize Measure() => Frame.Size;
	}

	internal sealed record RetainedCell(CellFamily Family, object Cell, UILabel Label);

	sealed record CycleResult(RetainedCell LegacyCell, RetainedCell Items2Cell, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<ExposedItemsViewController> LegacyController,
		WeakReference<CollectionView> LegacyItemsView,
		WeakReference<List<PayloadItem>> LegacyItems,
		WeakReference<PayloadItem> LegacyItem,
		WeakReference<PayloadItem> Items2Item)
	{
		public static TrackedCycle Create(
			int cycle,
			ExposedItemsViewController legacyController,
			CollectionView legacyItemsView,
			List<PayloadItem> legacyItems,
			PayloadItem legacyItem,
			PayloadItem items2Item)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<ExposedItemsViewController>(legacyController),
				new WeakReference<CollectionView>(legacyItemsView),
				new WeakReference<List<PayloadItem>>(legacyItems),
				new WeakReference<PayloadItem>(legacyItem),
				new WeakReference<PayloadItem>(items2Item));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedCells,
		int RetainedLegacyCells,
		int RetainedItems2Cells,
		int AssignedPayloadTexts,
		int AssignedLegacyPayloadTexts,
		int AssignedItems2PayloadTexts,
		long EstimatedAssignedTextBytes,
		long EstimatedLegacyTextBytes,
		long EstimatedItems2TextBytes,
		int AliveLegacyControllers,
		int AliveLegacyItemsViews,
		int AliveLegacyItemSources,
		int AliveLegacyItems,
		int AliveItems2Items)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedCell> retainedCells,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadTexts = 0;
			var assignedLegacyPayloadTexts = 0;
			var assignedItems2PayloadTexts = 0;
			long estimatedAssignedTextBytes = 0;
			long estimatedLegacyTextBytes = 0;
			long estimatedItems2TextBytes = 0;
			var retainedLegacyCells = 0;
			var retainedItems2Cells = 0;

			foreach (var retainedCell in retainedCells)
			{
				var assigned = CountAssignedPayloadTexts(retainedCell);
				var estimated = EstimateAssignedTextBytes(retainedCell);
				assignedPayloadTexts += assigned;
				estimatedAssignedTextBytes += estimated;

				if (retainedCell.Family == CellFamily.LegacyItemsView)
				{
					retainedLegacyCells++;
					assignedLegacyPayloadTexts += assigned;
					estimatedLegacyTextBytes += estimated;
				}
				else
				{
					retainedItems2Cells++;
					assignedItems2PayloadTexts += assigned;
					estimatedItems2TextBytes += estimated;
				}
			}

			var aliveLegacyControllers = 0;
			var aliveLegacyItemsViews = 0;
			var aliveLegacyItemSources = 0;
			var aliveLegacyItems = 0;
			var aliveItems2Items = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.LegacyController.TryGetTarget(out _))
					aliveLegacyControllers++;

				if (cycle.LegacyItemsView.TryGetTarget(out _))
					aliveLegacyItemsViews++;

				if (cycle.LegacyItems.TryGetTarget(out _))
					aliveLegacyItemSources++;

				if (cycle.LegacyItem.TryGetTarget(out _))
					aliveLegacyItems++;

				if (cycle.Items2Item.TryGetTarget(out _))
					aliveItems2Items++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedCells.Count,
				retainedLegacyCells,
				retainedItems2Cells,
				assignedPayloadTexts,
				assignedLegacyPayloadTexts,
				assignedItems2PayloadTexts,
				estimatedAssignedTextBytes,
				estimatedLegacyTextBytes,
				estimatedItems2TextBytes,
				aliveLegacyControllers,
				aliveLegacyItemsViews,
				aliveLegacyItemSources,
				aliveLegacyItems,
				aliveItems2Items);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerText,
	int CellsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved
	{
		get
		{
			var expectedCells = Cycles * CellsPerCycle;
			return
				Control.RetainedCells == expectedCells &&
				Control.AssignedPayloadTexts == 0 &&
				Current.RetainedCells == expectedCells &&
				Current.RetainedLegacyCells == Cycles &&
				Current.RetainedItems2Cells == Cycles &&
				Current.AssignedPayloadTexts == expectedCells &&
				Current.AssignedLegacyPayloadTexts == Cycles &&
				Current.AssignedItems2PayloadTexts == Cycles &&
				Current.EstimatedAssignedTextBytes >= expectedCells * PayloadKiBPerText * 1024L * 0.95 &&
				Current.AliveLegacyControllers <= 1 &&
				Current.AliveLegacyItemsViews <= 1 &&
				Current.AliveLegacyItemSources <= 1 &&
				Current.AliveLegacyItems <= 1 &&
				Current.AliveItems2Items <= 1;
		}
	}

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedTextBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTextBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosCollectionViewDefaultCellLabelRetentionRepro",
			$"Cycles: {Cycles}",
			$"Cells per cycle: {CellsPerCycle}",
			$"Payload per native label text: {PayloadKiBPerText} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native label text payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native label text payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTextMiB = result.EstimatedAssignedTextBytes / 1024d / 1024d;
		var legacyMiB = result.EstimatedLegacyTextBytes / 1024d / 1024d;
		var items2MiB = result.EstimatedItems2TextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native default cells: {result.RetainedCells}/{result.TrackedCycles * 2}",
			$"  retained legacy default cells: {result.RetainedLegacyCells}/{result.TrackedCycles}",
			$"  retained Items2 default cells: {result.RetainedItems2Cells}/{result.TrackedCycles}",
			$"  assigned payload-sized label texts: {result.AssignedPayloadTexts}/{result.TrackedCycles * 2}",
			$"  assigned legacy payload texts: {result.AssignedLegacyPayloadTexts}/{result.TrackedCycles}",
			$"  assigned Items2 payload texts: {result.AssignedItems2PayloadTexts}/{result.TrackedCycles}",
			$"  estimated assigned native label text bytes: {result.EstimatedAssignedTextBytes:N0}",
			$"  estimated assigned native label text MiB: {nativeTextMiB:N1}",
			$"  estimated legacy label text MiB: {legacyMiB:N1}",
			$"  estimated Items2 label text MiB: {items2MiB:N1}",
			$"  alive legacy controllers: {result.AliveLegacyControllers}/{result.TrackedCycles}",
			$"  alive legacy CollectionViews: {result.AliveLegacyItemsViews}/{result.TrackedCycles}",
			$"  alive legacy item sources: {result.AliveLegacyItemSources}/{result.TrackedCycles}",
			$"  alive legacy item models: {result.AliveLegacyItems}/{result.TrackedCycles}",
			$"  alive Items2 item models: {result.AliveItems2Items}/{result.TrackedCycles}");
	}
}
