#nullable enable

#pragma warning disable CS0618

using System.Reflection;
using System.Text;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;
using Microsoft.Maui.Controls.Handlers.Items2;
using UIKit;

using LegacyDefaultCell = Microsoft.Maui.Controls.Handlers.Items.DefaultCell;
using LegacyGroupableController = Microsoft.Maui.Controls.Handlers.Items.GroupableItemsViewController<Microsoft.Maui.Controls.CollectionView>;
using Items2GroupableController = Microsoft.Maui.Controls.Handlers.Items2.GroupableItemsViewController2<Microsoft.Maui.Controls.CollectionView>;
using Items2StructuredController = Microsoft.Maui.Controls.Handlers.Items2.StructuredItemsViewController2<Microsoft.Maui.Controls.CollectionView>;

namespace IosCollectionViewSupplementaryLabelRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerText = 128;
	internal const int CellsPerCycle = 4;

	const long PayloadBytesPerText = PayloadKiBPerText * 1024L;

	static readonly List<RetainedCell> RetainedCells = new();

	static readonly MethodInfo LegacyGroupUpdateMethod =
		typeof(LegacyGroupableController).GetMethod("UpdateDefaultSupplementaryView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(LegacyGroupableController), "UpdateDefaultSupplementaryView");

	static readonly MethodInfo Items2GroupUpdateMethod =
		typeof(Items2GroupableController).GetMethod("UpdateDefaultSupplementaryView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(Items2GroupableController), "UpdateDefaultSupplementaryView");

	static readonly MethodInfo Items2StructuredUpdateMethod =
		typeof(Items2StructuredController).GetMethod("UpdateDefaultSupplementaryView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(Items2StructuredController), "UpdateDefaultSupplementaryView");

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-collectionview-supplementary-label-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS CollectionView supplementary label retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear retained supplementary UILabel.Text slots",
			clearNativeText: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: supplementary cells leave UILabel.Text assigned",
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
			retainedCells.AddRange(cycleResult.Cells);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedCells.AddRange(retainedCells);
		ForceFullGc();

		return ScenarioResult.From(name, retainedCells, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(int cycle, bool clearNativeText)
	{
		var legacyGroup = new PayloadGroup(CreateLargeText("legacy grouped supplementary header", cycle));
		legacyGroup.Add(new PayloadItem("legacy child"));
		var legacyGroups = new List<PayloadGroup> { legacyGroup };
		var legacyItemsView = new CollectionView
		{
			IsGrouped = true,
			ItemsLayout = LinearItemsLayout.Vertical,
			ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
			ItemsSource = legacyGroups
		};
		var legacyLayout = new ListViewLayout((LinearItemsLayout)legacyItemsView.ItemsLayout, legacyItemsView.ItemSizingStrategy);
		var legacyController = new LegacyGroupableController(legacyItemsView, legacyLayout);
		legacyController.LoadView();
		legacyController.ViewDidLoad();
		var legacyCell = new TestDefaultCell(new CGRect(0, 0, 360, 44));
		LegacyGroupUpdateMethod.Invoke(legacyController, new object[] { legacyCell, UICollectionElementKindSectionKey.Header, NSIndexPath.FromItemSection(0, 0) });
		AssertPayloadText(legacyCell.Label.Text, "Legacy GroupableItemsViewController did not assign the payload-sized group label text.");

		var items2Group = new PayloadGroup(CreateLargeText("Items2 grouped supplementary header", cycle));
		items2Group.Add(new PayloadItem("items2 child"));
		var items2Groups = new List<PayloadGroup> { items2Group };
		var items2GroupedView = new CollectionView
		{
			IsGrouped = true,
			ItemsLayout = LinearItemsLayout.Vertical,
			ItemsSource = items2Groups
		};
		var items2GroupController = new Items2GroupableController(items2GroupedView, new UICollectionViewFlowLayout());
		items2GroupController.LoadView();
		items2GroupController.ViewDidLoad();
		var items2GroupCell = new DefaultCell2(new CGRect(0, 0, 360, 44));
		Items2GroupUpdateMethod.Invoke(items2GroupController, new object[] { items2GroupCell, UICollectionElementKindSectionKey.Header, NSIndexPath.FromItemSection(0, 0) });
		AssertPayloadText(items2GroupCell.Label.Text, "Items2 GroupableItemsViewController2 did not assign the payload-sized group label text.");

		var headerItem = new PayloadItem(CreateLargeText("Items2 CollectionView Header string", cycle));
		var footerItem = new PayloadItem(CreateLargeText("Items2 CollectionView Footer string", cycle));
		var items2StructuredView = new CollectionView
		{
			ItemsLayout = LinearItemsLayout.Vertical,
			ItemsSource = new[] { "row" },
			Header = headerItem,
			Footer = footerItem
		};
		var items2StructuredController = new Items2StructuredController(items2StructuredView, new UICollectionViewFlowLayout());
		items2StructuredController.LoadView();
		items2StructuredController.ViewDidLoad();
		var headerCell = new DefaultCell2(new CGRect(0, 0, 360, 44));
		var footerCell = new DefaultCell2(new CGRect(0, 0, 360, 44));
		Items2StructuredUpdateMethod.Invoke(items2StructuredController, new object[] { headerCell, UICollectionElementKindSectionKey.Header });
		Items2StructuredUpdateMethod.Invoke(items2StructuredController, new object[] { footerCell, UICollectionElementKindSectionKey.Footer });
		AssertPayloadText(headerCell.Label.Text, "Items2 StructuredItemsViewController2 did not assign the payload-sized header label text.");
		AssertPayloadText(footerCell.Label.Text, "Items2 StructuredItemsViewController2 did not assign the payload-sized footer label text.");

		legacyGroup.ClearText();
		legacyGroups[0] = new PayloadGroup(string.Empty);
		legacyItemsView.ItemsSource = null;

		items2Group.ClearText();
		items2Groups[0] = new PayloadGroup(string.Empty);
		items2GroupedView.ItemsSource = null;

		headerItem.Clear();
		footerItem.Clear();
		items2StructuredView.Header = null;
		items2StructuredView.Footer = null;
		items2StructuredView.ItemsSource = null;

		if (clearNativeText)
		{
			legacyCell.Label.Text = string.Empty;
			items2GroupCell.Label.Text = string.Empty;
			headerCell.Label.Text = string.Empty;
			footerCell.Label.Text = string.Empty;
		}

		var tracked = TrackedCycle.Create(
			cycle,
			legacyController,
			legacyItemsView,
			legacyGroups,
			legacyGroup,
			items2GroupController,
			items2GroupedView,
			items2Groups,
			items2Group,
			items2StructuredController,
			items2StructuredView,
			headerItem,
			footerItem);

		legacyController.Dispose();
		items2GroupController.Dispose();
		items2StructuredController.Dispose();
		await DrainMainQueueAsync();

		var retainedCells = new[]
		{
			new RetainedCell(CellFamily.LegacyGroupSupplementary, legacyCell, legacyCell.Label),
			new RetainedCell(CellFamily.Items2GroupSupplementary, items2GroupCell, items2GroupCell.Label),
			new RetainedCell(CellFamily.Items2Header, headerCell, headerCell.Label),
			new RetainedCell(CellFamily.Items2Footer, footerCell, footerCell.Label)
		};

		GC.KeepAlive(retainedCells);

		return new CycleResult(retainedCells, tracked);
	}

	static string CreateLargeText(string prefix, int cycle)
	{
		var header = $"{prefix} {cycle:000}. ";
		var sentence = "Regional schedule section, grouped search results, account notes, and audit trail summary. ";
		var targetChars = (int)(PayloadBytesPerText / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void AssertPayloadText(string? text, string message)
	{
		if (EstimateTextBytes(text) < PayloadBytesPerText * 0.95)
			throw new InvalidOperationException(message);
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
		LegacyGroupSupplementary,
		Items2GroupSupplementary,
		Items2Header,
		Items2Footer
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

	internal sealed class PayloadGroup : List<PayloadItem>
	{
		string _text;

		public PayloadGroup(string text)
		{
			_text = text;
		}

		public void ClearText()
		{
			_text = string.Empty;
		}

		public override string ToString() => _text;
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

	sealed record CycleResult(IReadOnlyList<RetainedCell> Cells, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<LegacyGroupableController> LegacyController,
		WeakReference<CollectionView> LegacyItemsView,
		WeakReference<List<PayloadGroup>> LegacyGroups,
		WeakReference<PayloadGroup> LegacyGroup,
		WeakReference<Items2GroupableController> Items2GroupController,
		WeakReference<CollectionView> Items2GroupedView,
		WeakReference<List<PayloadGroup>> Items2Groups,
		WeakReference<PayloadGroup> Items2Group,
		WeakReference<Items2StructuredController> Items2StructuredController,
		WeakReference<CollectionView> Items2StructuredView,
		WeakReference<PayloadItem> HeaderItem,
		WeakReference<PayloadItem> FooterItem)
	{
		public static TrackedCycle Create(
			int cycle,
			LegacyGroupableController legacyController,
			CollectionView legacyItemsView,
			List<PayloadGroup> legacyGroups,
			PayloadGroup legacyGroup,
			Items2GroupableController items2GroupController,
			CollectionView items2GroupedView,
			List<PayloadGroup> items2Groups,
			PayloadGroup items2Group,
			Items2StructuredController items2StructuredController,
			CollectionView items2StructuredView,
			PayloadItem headerItem,
			PayloadItem footerItem)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<LegacyGroupableController>(legacyController),
				new WeakReference<CollectionView>(legacyItemsView),
				new WeakReference<List<PayloadGroup>>(legacyGroups),
				new WeakReference<PayloadGroup>(legacyGroup),
				new WeakReference<Items2GroupableController>(items2GroupController),
				new WeakReference<CollectionView>(items2GroupedView),
				new WeakReference<List<PayloadGroup>>(items2Groups),
				new WeakReference<PayloadGroup>(items2Group),
				new WeakReference<Items2StructuredController>(items2StructuredController),
				new WeakReference<CollectionView>(items2StructuredView),
				new WeakReference<PayloadItem>(headerItem),
				new WeakReference<PayloadItem>(footerItem));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedCells,
		int RetainedLegacyGroupCells,
		int RetainedItems2GroupCells,
		int RetainedItems2HeaderCells,
		int RetainedItems2FooterCells,
		int AssignedPayloadTexts,
		int AssignedLegacyGroupPayloadTexts,
		int AssignedItems2GroupPayloadTexts,
		int AssignedItems2HeaderPayloadTexts,
		int AssignedItems2FooterPayloadTexts,
		long EstimatedAssignedTextBytes,
		long EstimatedLegacyGroupTextBytes,
		long EstimatedItems2GroupTextBytes,
		long EstimatedItems2HeaderTextBytes,
		long EstimatedItems2FooterTextBytes,
		int AliveLegacyControllers,
		int AliveLegacyItemsViews,
		int AliveLegacyGroups,
		int AliveLegacyGroupObjects,
		int AliveItems2GroupControllers,
		int AliveItems2GroupedViews,
		int AliveItems2Groups,
		int AliveItems2GroupObjects,
		int AliveItems2StructuredControllers,
		int AliveItems2StructuredViews,
		int AliveHeaderItems,
		int AliveFooterItems)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedCell> retainedCells,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadTexts = 0;
			var assignedLegacyGroupPayloadTexts = 0;
			var assignedItems2GroupPayloadTexts = 0;
			var assignedItems2HeaderPayloadTexts = 0;
			var assignedItems2FooterPayloadTexts = 0;
			long estimatedAssignedTextBytes = 0;
			long estimatedLegacyGroupTextBytes = 0;
			long estimatedItems2GroupTextBytes = 0;
			long estimatedItems2HeaderTextBytes = 0;
			long estimatedItems2FooterTextBytes = 0;
			var retainedLegacyGroupCells = 0;
			var retainedItems2GroupCells = 0;
			var retainedItems2HeaderCells = 0;
			var retainedItems2FooterCells = 0;

			foreach (var retainedCell in retainedCells)
			{
				var assigned = CountAssignedPayloadTexts(retainedCell);
				var estimated = EstimateAssignedTextBytes(retainedCell);
				assignedPayloadTexts += assigned;
				estimatedAssignedTextBytes += estimated;

				switch (retainedCell.Family)
				{
					case CellFamily.LegacyGroupSupplementary:
						retainedLegacyGroupCells++;
						assignedLegacyGroupPayloadTexts += assigned;
						estimatedLegacyGroupTextBytes += estimated;
						break;
					case CellFamily.Items2GroupSupplementary:
						retainedItems2GroupCells++;
						assignedItems2GroupPayloadTexts += assigned;
						estimatedItems2GroupTextBytes += estimated;
						break;
					case CellFamily.Items2Header:
						retainedItems2HeaderCells++;
						assignedItems2HeaderPayloadTexts += assigned;
						estimatedItems2HeaderTextBytes += estimated;
						break;
					case CellFamily.Items2Footer:
						retainedItems2FooterCells++;
						assignedItems2FooterPayloadTexts += assigned;
						estimatedItems2FooterTextBytes += estimated;
						break;
				}
			}

			var aliveLegacyControllers = 0;
			var aliveLegacyItemsViews = 0;
			var aliveLegacyGroups = 0;
			var aliveLegacyGroupObjects = 0;
			var aliveItems2GroupControllers = 0;
			var aliveItems2GroupedViews = 0;
			var aliveItems2Groups = 0;
			var aliveItems2GroupObjects = 0;
			var aliveItems2StructuredControllers = 0;
			var aliveItems2StructuredViews = 0;
			var aliveHeaderItems = 0;
			var aliveFooterItems = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.LegacyController.TryGetTarget(out _))
					aliveLegacyControllers++;

				if (cycle.LegacyItemsView.TryGetTarget(out _))
					aliveLegacyItemsViews++;

				if (cycle.LegacyGroups.TryGetTarget(out _))
					aliveLegacyGroups++;

				if (cycle.LegacyGroup.TryGetTarget(out _))
					aliveLegacyGroupObjects++;

				if (cycle.Items2GroupController.TryGetTarget(out _))
					aliveItems2GroupControllers++;

				if (cycle.Items2GroupedView.TryGetTarget(out _))
					aliveItems2GroupedViews++;

				if (cycle.Items2Groups.TryGetTarget(out _))
					aliveItems2Groups++;

				if (cycle.Items2Group.TryGetTarget(out _))
					aliveItems2GroupObjects++;

				if (cycle.Items2StructuredController.TryGetTarget(out _))
					aliveItems2StructuredControllers++;

				if (cycle.Items2StructuredView.TryGetTarget(out _))
					aliveItems2StructuredViews++;

				if (cycle.HeaderItem.TryGetTarget(out _))
					aliveHeaderItems++;

				if (cycle.FooterItem.TryGetTarget(out _))
					aliveFooterItems++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedCells.Count,
				retainedLegacyGroupCells,
				retainedItems2GroupCells,
				retainedItems2HeaderCells,
				retainedItems2FooterCells,
				assignedPayloadTexts,
				assignedLegacyGroupPayloadTexts,
				assignedItems2GroupPayloadTexts,
				assignedItems2HeaderPayloadTexts,
				assignedItems2FooterPayloadTexts,
				estimatedAssignedTextBytes,
				estimatedLegacyGroupTextBytes,
				estimatedItems2GroupTextBytes,
				estimatedItems2HeaderTextBytes,
				estimatedItems2FooterTextBytes,
				aliveLegacyControllers,
				aliveLegacyItemsViews,
				aliveLegacyGroups,
				aliveLegacyGroupObjects,
				aliveItems2GroupControllers,
				aliveItems2GroupedViews,
				aliveItems2Groups,
				aliveItems2GroupObjects,
				aliveItems2StructuredControllers,
				aliveItems2StructuredViews,
				aliveHeaderItems,
				aliveFooterItems);
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
				Current.RetainedLegacyGroupCells == Cycles &&
				Current.RetainedItems2GroupCells == Cycles &&
				Current.RetainedItems2HeaderCells == Cycles &&
				Current.RetainedItems2FooterCells == Cycles &&
				Current.AssignedPayloadTexts == expectedCells &&
				Current.AssignedLegacyGroupPayloadTexts == Cycles &&
				Current.AssignedItems2GroupPayloadTexts == Cycles &&
				Current.AssignedItems2HeaderPayloadTexts == Cycles &&
				Current.AssignedItems2FooterPayloadTexts == Cycles &&
				Current.EstimatedAssignedTextBytes >= expectedCells * PayloadKiBPerText * 1024L * 0.95 &&
				Current.AliveLegacyControllers <= 1 &&
				Current.AliveLegacyItemsViews <= 1 &&
				Current.AliveLegacyGroups <= 1 &&
				Current.AliveLegacyGroupObjects <= 1 &&
				Current.AliveItems2GroupControllers <= 1 &&
				Current.AliveItems2GroupedViews <= 1 &&
				Current.AliveItems2Groups <= 1 &&
				Current.AliveItems2GroupObjects <= 1 &&
				Current.AliveItems2StructuredControllers <= 1 &&
				Current.AliveItems2StructuredViews <= 1 &&
				Current.AliveHeaderItems <= 1 &&
				Current.AliveFooterItems <= 1;
		}
	}

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedTextBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTextBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosCollectionViewSupplementaryLabelRetentionRepro",
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
			$"Control estimated assigned native supplementary label text payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native supplementary label text payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTextMiB = result.EstimatedAssignedTextBytes / 1024d / 1024d;
		var legacyGroupMiB = result.EstimatedLegacyGroupTextBytes / 1024d / 1024d;
		var items2GroupMiB = result.EstimatedItems2GroupTextBytes / 1024d / 1024d;
		var items2HeaderMiB = result.EstimatedItems2HeaderTextBytes / 1024d / 1024d;
		var items2FooterMiB = result.EstimatedItems2FooterTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native supplementary cells: {result.RetainedCells}/{result.TrackedCycles * 4}",
			$"  retained legacy group supplementary cells: {result.RetainedLegacyGroupCells}/{result.TrackedCycles}",
			$"  retained Items2 group supplementary cells: {result.RetainedItems2GroupCells}/{result.TrackedCycles}",
			$"  retained Items2 header cells: {result.RetainedItems2HeaderCells}/{result.TrackedCycles}",
			$"  retained Items2 footer cells: {result.RetainedItems2FooterCells}/{result.TrackedCycles}",
			$"  assigned payload-sized label texts: {result.AssignedPayloadTexts}/{result.TrackedCycles * 4}",
			$"  assigned legacy group payload texts: {result.AssignedLegacyGroupPayloadTexts}/{result.TrackedCycles}",
			$"  assigned Items2 group payload texts: {result.AssignedItems2GroupPayloadTexts}/{result.TrackedCycles}",
			$"  assigned Items2 header payload texts: {result.AssignedItems2HeaderPayloadTexts}/{result.TrackedCycles}",
			$"  assigned Items2 footer payload texts: {result.AssignedItems2FooterPayloadTexts}/{result.TrackedCycles}",
			$"  estimated assigned native label text bytes: {result.EstimatedAssignedTextBytes:N0}",
			$"  estimated assigned native label text MiB: {nativeTextMiB:N1}",
			$"  estimated legacy group label text MiB: {legacyGroupMiB:N1}",
			$"  estimated Items2 group label text MiB: {items2GroupMiB:N1}",
			$"  estimated Items2 header label text MiB: {items2HeaderMiB:N1}",
			$"  estimated Items2 footer label text MiB: {items2FooterMiB:N1}",
			$"  alive legacy controllers: {result.AliveLegacyControllers}/{result.TrackedCycles}",
			$"  alive legacy CollectionViews: {result.AliveLegacyItemsViews}/{result.TrackedCycles}",
			$"  alive legacy group sources: {result.AliveLegacyGroups}/{result.TrackedCycles}",
			$"  alive legacy group objects: {result.AliveLegacyGroupObjects}/{result.TrackedCycles}",
			$"  alive Items2 group controllers: {result.AliveItems2GroupControllers}/{result.TrackedCycles}",
			$"  alive Items2 grouped CollectionViews: {result.AliveItems2GroupedViews}/{result.TrackedCycles}",
			$"  alive Items2 group sources: {result.AliveItems2Groups}/{result.TrackedCycles}",
			$"  alive Items2 group objects: {result.AliveItems2GroupObjects}/{result.TrackedCycles}",
			$"  alive Items2 structured controllers: {result.AliveItems2StructuredControllers}/{result.TrackedCycles}",
			$"  alive Items2 structured CollectionViews: {result.AliveItems2StructuredViews}/{result.TrackedCycles}",
			$"  alive Items2 header objects: {result.AliveHeaderItems}/{result.TrackedCycles}",
			$"  alive Items2 footer objects: {result.AliveFooterItems}/{result.TrackedCycles}");
	}
}
