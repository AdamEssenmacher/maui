#nullable enable

#pragma warning disable CS0618

using System.Text;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using UIKit;

namespace IosShellSectionRootHeaderTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerTitle = 256;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly List<RetainedHeaderCell> RetainedHeaderCells = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shellsection-rootheader-title-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS ShellSection root header title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear ShellSectionRootHeader cell UILabel text before retaining native cell",
			clearNativeTitle: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ShellSectionRootHeader leaves header cell UILabel text assigned",
			clearNativeTitle: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(context);
		GC.KeepAlive(RetainedHeaderCells);

		return new ReproReport(
			Cycles,
			PayloadKiBPerTitle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearNativeTitle)
	{
		var retainedCells = new List<RetainedHeaderCell>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, clearNativeTitle);
			retainedCells.Add(cycleResult.RetainedCell);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedHeaderCells.AddRange(retainedCells);
		ForceFullGc();

		return ScenarioResult.From(name, retainedCells, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(int cycle, bool clearNativeTitle)
	{
		var title = CreateLargeTitle(cycle);
		var page = new ContentPage { Title = $"Content {cycle:000}" };
		var shellContent = new ShellContent
		{
			Title = title,
			Content = page
		};
		var shellSection = new ShellSection { Title = $"Section {cycle:000}" };
		shellSection.Items.Add(shellContent);
		shellSection.CurrentItem = shellContent;
		var shellItem = new FlyoutItem { Title = "Root" };
		shellItem.Items.Add(shellSection);
		shellItem.CurrentItem = shellSection;
		var shell = new Shell();
		shell.Items.Add(shellItem);
		shell.CurrentItem = shellItem;

		var header = new ShellSectionRootHeader(new FakeShellContext(shell))
		{
			ShellSection = shellSection
		};

		_ = header.View;
		header.ViewDidLoad();
		await DrainMainQueueAsync();

		var indexPath = NSIndexPath.FromItemSection(0, 0);
		var cell = header.GetCell(header.CollectionView, indexPath) as ShellSectionRootHeader.ShellSectionHeaderCell
			?? throw new InvalidOperationException("ShellSectionRootHeader did not create a ShellSectionHeaderCell.");

		if (EstimateTitleBytes(cell.Label.Text) < PayloadBytesPerTitle * 0.95)
			throw new InvalidOperationException("ShellSectionRootHeader did not assign the payload-sized native label text.");

		shellContent.Title = string.Empty;

		if (clearNativeTitle)
			cell.Label.Text = string.Empty;

		header.Dispose();
		page.Content = null;
		await DrainMainQueueAsync();

		return new CycleResult(
			new RetainedHeaderCell(cell),
			TrackedCycle.Create(cycle, header, shell, shellItem, shellSection, shellContent, page));
	}

	static string CreateLargeTitle(int cycle)
	{
		var header = $"Shell section header title {cycle:000}. ";
		var sentence = "Generated workspace, offline case group, routed operation lane, and review queue. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
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

	static int CountAssignedPayloadTitles(ShellSectionRootHeader.ShellSectionHeaderCell cell)
	{
		return EstimateTitleBytes(cell.Label.Text) >= PayloadBytesPerTitle * 0.95 ? 1 : 0;
	}

	static long EstimateAssignedTitleBytes(ShellSectionRootHeader.ShellSectionHeaderCell cell)
	{
		return Math.Min(EstimateTitleBytes(cell.Label.Text), PayloadBytesPerTitle);
	}

	static long EstimateTitleBytes(string? title)
	{
		return string.IsNullOrEmpty(title) ? 0 : title.Length * 2L;
	}

	internal sealed record RetainedHeaderCell(ShellSectionRootHeader.ShellSectionHeaderCell Cell);

	internal sealed record CycleResult(RetainedHeaderCell RetainedCell, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<ShellSectionRootHeader> Header,
		WeakReference<Shell> Shell,
		WeakReference<ShellItem> ShellItem,
		WeakReference<ShellSection> ShellSection,
		WeakReference<ShellContent> ShellContent,
		WeakReference<ContentPage> Page)
	{
		public static TrackedCycle Create(
			int cycle,
			ShellSectionRootHeader header,
			Shell shell,
			ShellItem shellItem,
			ShellSection shellSection,
			ShellContent shellContent,
			ContentPage page)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<ShellSectionRootHeader>(header),
				new WeakReference<Shell>(shell),
				new WeakReference<ShellItem>(shellItem),
				new WeakReference<ShellSection>(shellSection),
				new WeakReference<ShellContent>(shellContent),
				new WeakReference<ContentPage>(page));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedHeaderCells,
		int AssignedPayloadTitles,
		long EstimatedAssignedTitleBytes,
		int AliveHeaders,
		int AliveShells,
		int AliveShellItems,
		int AliveShellSections,
		int AliveShellContents,
		int AlivePages)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedHeaderCell> retainedCells,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadTitles = 0;
			long estimatedAssignedTitleBytes = 0;

			foreach (var retainedCell in retainedCells)
			{
				assignedPayloadTitles += CountAssignedPayloadTitles(retainedCell.Cell);
				estimatedAssignedTitleBytes += EstimateAssignedTitleBytes(retainedCell.Cell);
			}

			var aliveHeaders = 0;
			var aliveShells = 0;
			var aliveShellItems = 0;
			var aliveShellSections = 0;
			var aliveShellContents = 0;
			var alivePages = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Header.TryGetTarget(out _))
					aliveHeaders++;

				if (cycle.Shell.TryGetTarget(out _))
					aliveShells++;

				if (cycle.ShellItem.TryGetTarget(out _))
					aliveShellItems++;

				if (cycle.ShellSection.TryGetTarget(out _))
					aliveShellSections++;

				if (cycle.ShellContent.TryGetTarget(out _))
					aliveShellContents++;

				if (cycle.Page.TryGetTarget(out _))
					alivePages++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedCells.Count,
				assignedPayloadTitles,
				estimatedAssignedTitleBytes,
				aliveHeaders,
				aliveShells,
				aliveShellItems,
				aliveShellSections,
				aliveShellContents,
				alivePages);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerTitle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedHeaderCells == Cycles &&
		Control.AssignedPayloadTitles == 0 &&
		Current.RetainedHeaderCells == Cycles &&
		Current.AssignedPayloadTitles == Cycles &&
		Current.EstimatedAssignedTitleBytes >= Cycles * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AliveHeaders <= 1 &&
		Current.AliveShells <= 1 &&
		Current.AliveShellItems <= 1 &&
		Current.AliveShellSections <= 1 &&
		Current.AliveShellContents <= 1 &&
		Current.AlivePages <= 1;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellSectionRootHeaderTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native title: {PayloadKiBPerTitle} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native title payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native title payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTitleMiB = result.EstimatedAssignedTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native header cells: {result.RetainedHeaderCells}/{result.TrackedCycles}",
			$"  assigned payload-sized titles: {result.AssignedPayloadTitles}/{result.TrackedCycles}",
			$"  estimated assigned native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated assigned native title MiB: {nativeTitleMiB:N1}",
			$"  alive headers: {result.AliveHeaders}/{result.TrackedCycles}",
			$"  alive shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive ShellItems: {result.AliveShellItems}/{result.TrackedCycles}",
			$"  alive ShellSections: {result.AliveShellSections}/{result.TrackedCycles}",
			$"  alive ShellContents: {result.AliveShellContents}/{result.TrackedCycles}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}");
	}
}

internal sealed class FakeShellContext : IShellContext
{
	public FakeShellContext(Shell shell)
	{
		Shell = shell;
	}

	public bool AllowFlyoutGesture => false;

	public IShellItemRenderer CurrentShellItemRenderer => null!;

	public Shell Shell { get; }

	public IShellPageRendererTracker CreatePageRendererTracker() => throw new NotSupportedException();

	public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

	public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

	public IShellNavBarAppearanceTracker CreateNavBarAppearanceTracker() => throw new NotSupportedException();

	public IShellTabBarAppearanceTracker CreateTabBarAppearanceTracker() => throw new NotSupportedException();

	public IShellSearchResultsRenderer CreateShellSearchResultsRenderer() => throw new NotSupportedException();
}
