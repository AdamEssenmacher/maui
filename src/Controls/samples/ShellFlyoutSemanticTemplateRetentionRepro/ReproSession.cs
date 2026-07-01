#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Foundation;
using Microsoft.Maui.Controls;

namespace ShellFlyoutSemanticTemplateRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 160;
	internal const int PayloadKiBPerGeneratedCell = 512;
	const long PayloadBytesPerGeneratedCell = PayloadKiBPerGeneratedCell * 1024L;

	static readonly MethodInfo CreateDefaultFlyoutItemCellMethod =
		typeof(BaseShellItem).GetMethod(
			"CreateDefaultFlyoutItemCell",
			BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not locate BaseShellItem.CreateDefaultFlyoutItemCell.");

	static readonly List<IReadOnlyList<BaseShellItem>> RetainedShellItems = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "shell-flyout-semantic-template-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting Shell flyout semantic template retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: generated default flyout cells after BindingContext clear",
			clearBindingContextBeforeAbandon: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: generated default flyout cells abandoned with BaseShellItem semantic subscription still attached",
			clearBindingContextBeforeAbandon: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedShellItems);

		return new ReproReport(
			Cycles,
			PayloadKiBPerGeneratedCell,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearBindingContextBeforeAbandon)
	{
		var retainedShellItems = new List<BaseShellItem>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 40 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var item = CreateShellItem(i);
			retainedShellItems.Add(item);
			CreateTemplateCycle(i, item, clearBindingContextBeforeAbandon, tracked);
		}

		RetainedShellItems.Add(retainedShellItems);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static FlyoutItem CreateShellItem(int cycle)
	{
		var item = new FlyoutItem
		{
			Title = $"Operations dashboard {cycle:000}",
			Route = $"operations-dashboard-{cycle:000}"
		};

		SemanticProperties.SetHint(item, $"Open operations dashboard {cycle:000}");
		return item;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateTemplateCycle(
		int cycle,
		BaseShellItem item,
		bool clearBindingContextBeforeAbandon,
		List<TrackedCycle> tracked)
	{
		CreateTemplateCycleCore(cycle, item, clearBindingContextBeforeAbandon, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateTemplateCycleCore(
		int cycle,
		BaseShellItem item,
		bool clearBindingContextBeforeAbandon,
		List<TrackedCycle> tracked)
	{
		using var pool = new NSAutoreleasePool();

		var template = CreateDefaultFlyoutItemCell(item);
		var content = template.CreateContent();
		var grid = content as Grid
			?? throw new InvalidOperationException($"Default flyout item template created {content?.GetType().FullName ?? "null"} instead of Grid.");

		grid.BindingContext = item;

		var label = grid.Children.OfType<Label>().FirstOrDefault()
			?? throw new InvalidOperationException("Default flyout item template did not create a Label.");
		var image = grid.Children.OfType<Image>().FirstOrDefault()
			?? throw new InvalidOperationException("Default flyout item template did not create an Image.");
		var payload = PayloadHolder.Create(cycle);

		// Quantifies the retained generated cell graph. The leak root is the Shell item event subscription;
		// the payload stands in for row-local resources, handlers, or diagnostic state hanging off the cell.
		grid.Resources["RetainedGeneratedCellPayload"] = payload;
		label.AutomationId = $"GeneratedFlyoutItemLabel{cycle:000}";
		image.AutomationId = $"GeneratedFlyoutItemImage{cycle:000}";

		tracked.Add(TrackedCycle.Create(item, grid, label, image, payload));

		if (clearBindingContextBeforeAbandon)
			grid.BindingContext = null;
	}

	static DataTemplate CreateDefaultFlyoutItemCell(BindableObject source)
	{
		return (DataTemplate)(CreateDefaultFlyoutItemCellMethod.Invoke(null, [source])
			?? throw new InvalidOperationException("Default flyout item template factory returned null."));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 5; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
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

	internal sealed class PayloadHolder
	{
		PayloadHolder(int cycle, byte[] bytes, string description)
		{
			Cycle = cycle;
			Bytes = bytes;
			Description = description;
		}

		public int Cycle { get; }

		public byte[] Bytes { get; }

		public string Description { get; }

		public static PayloadHolder Create(int cycle)
		{
			var bytes = new byte[PayloadBytesPerGeneratedCell];
			Array.Fill(bytes, (byte)('A' + cycle % 26));

			var description = CreateDescription(cycle);
			return new PayloadHolder(cycle, bytes, description);
		}

		static string CreateDescription(int cycle)
		{
			var targetChars = 4096;
			var sentence =
				$"flyout cell {cycle:0000}: active route metrics, feature flags, accessibility audit notes, and generated style metadata. ";
			var builder = new StringBuilder(targetChars + sentence.Length);

			while (builder.Length < targetChars)
				builder.Append(sentence);

			if (builder.Length > targetChars)
				builder.Length = targetChars;

			return builder.ToString();
		}
	}

	internal sealed record TrackedCycle(
		WeakReference<object> ShellItem,
		WeakReference<object> Grid,
		WeakReference<object> Label,
		WeakReference<object> Image,
		WeakReference<object> Payload)
	{
		public static TrackedCycle Create(
			object shellItem,
			object grid,
			object label,
			object image,
			object payload)
		{
			return new TrackedCycle(
				new WeakReference<object>(shellItem),
				new WeakReference<object>(grid),
				new WeakReference<object>(label),
				new WeakReference<object>(image),
				new WeakReference<object>(payload));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveShellItems,
		int AliveGeneratedGrids,
		int AliveGeneratedLabels,
		int AliveGeneratedImages,
		int AlivePayloads,
		long EstimatedPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveShellItems = 0;
			var aliveGeneratedGrids = 0;
			var aliveGeneratedLabels = 0;
			var aliveGeneratedImages = 0;
			var alivePayloads = 0;
			long estimatedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.ShellItem.TryGetTarget(out _))
					aliveShellItems++;

				if (cycle.Grid.TryGetTarget(out _))
					aliveGeneratedGrids++;

				if (cycle.Label.TryGetTarget(out _))
					aliveGeneratedLabels++;

				if (cycle.Image.TryGetTarget(out _))
					aliveGeneratedImages++;

				if (cycle.Payload.TryGetTarget(out var payload) && payload is PayloadHolder payloadHolder)
				{
					alivePayloads++;
					estimatedPayloadBytes += payloadHolder.Bytes.LongLength;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveShellItems,
				aliveGeneratedGrids,
				aliveGeneratedLabels,
				aliveGeneratedImages,
				alivePayloads,
				estimatedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerGeneratedCell,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveShellItems == Cycles &&
		Control.AliveGeneratedGrids <= 1 &&
		Control.AliveGeneratedLabels <= 1 &&
		Control.AliveGeneratedImages <= 1 &&
		Control.AlivePayloads <= 1 &&
		Control.EstimatedPayloadBytes <= PayloadKiBPerGeneratedCell * 1024L &&
		Current.AliveShellItems == Cycles &&
		Current.AliveGeneratedGrids == Cycles &&
		Current.AliveGeneratedLabels == Cycles &&
		Current.AliveGeneratedImages == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.EstimatedPayloadBytes >= Cycles * PayloadKiBPerGeneratedCell * 1024L;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"ShellFlyoutSemanticTemplateRetentionRepro",
			$"Cycles per scenario: {Cycles}",
			$"Payload per generated flyout cell: {PayloadKiBPerGeneratedCell} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained generated-cell payload: {controlMiB:N1} MiB",
			$"Current estimated retained generated-cell payload: {currentMiB:N1} MiB",
			"Interpretation:",
			"BaseShellItem.CreateDefaultFlyoutItemCell installs a BindingContextChanged handler on each generated default flyout Grid.",
			"When the Grid receives a BaseShellItem BindingContext, that handler calls SemanticProperties.FakeBindSemanticProperties, which subscribes to BaseShellItem.PropertyChanged and captures the generated Grid as the destination.",
			"The returned ActionDisposable is only disposed on a later BindingContextChanged event. If the generated cell is abandoned without clearing or changing BindingContext, the long-lived Shell item keeps the generated Grid, its child visual tree, and row-local payload graph alive.",
			"The control keeps the same long-lived Shell item sources but clears BindingContext before abandoning the generated cell, causing the existing cleanup hook to unsubscribe.",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedPayloadBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive Shell item sources: {result.AliveShellItems}/{result.TrackedCycles}",
			$"  alive generated default flyout Grids: {result.AliveGeneratedGrids}/{result.TrackedCycles}",
			$"  alive generated Labels: {result.AliveGeneratedLabels}/{result.TrackedCycles}",
			$"  alive generated Images: {result.AliveGeneratedImages}/{result.TrackedCycles}",
			$"  alive generated-cell payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  estimated retained payload bytes: {result.EstimatedPayloadBytes:N0}",
			$"  estimated retained payload MiB: {retainedMiB:N1}");
	}
}
