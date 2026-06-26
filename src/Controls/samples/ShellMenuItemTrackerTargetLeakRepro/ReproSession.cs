using System.Reflection;

namespace ShellMenuItemTrackerTargetLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly Type MenuBarTrackerType =
		typeof(Shell).Assembly.GetType("Microsoft.Maui.Controls.MenuBarTracker", throwOnError: true)!;

	static readonly ConstructorInfo MenuBarTrackerConstructor =
		MenuBarTrackerType.GetConstructor(
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(Element), typeof(string) },
			modifiers: null)!;

	static readonly PropertyInfo TargetProperty =
		MenuBarTrackerType.GetProperty(
			"Target",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)!;

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunControl();
		var leak = RunLeak();

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunControl()
	{
		var retainedTargets = new List<Page>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(retainedTargets, tracked, i);

		ForceFullGc();
		var result = ScenarioResult.From("control: ContentPage target cleared from MenuBarTracker", tracked);
		GC.KeepAlive(retainedTargets);
		return result;
	}

	static ScenarioResult RunLeak()
	{
		var retainedShells = new List<Shell>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakyCycle(retainedShells, tracked, i);

		ForceFullGc();
		var result = ScenarioResult.From("leak: live Shell target still roots cleared MenuBarTracker", tracked);
		GC.KeepAlive(retainedShells);
		return result;
	}

	static void CreateControlCycle(List<Page> retainedTargets, List<TrackedCycle> tracked, int cycle)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var owner = CreateTrackerOwner(cycle, payload);
		var target = new ContentPage { Title = $"Plain target {cycle + 1}" };
		target.MenuBarItems.Add(new MenuBarItem { Text = $"Target menu {cycle + 1}" });

		var tracker = CreateMenuBarTracker(owner);
		SetTarget(tracker, target);
		SetTarget(tracker, null);

		retainedTargets.Add(target);
		tracked.Add(TrackedCycle.Create(cycle, tracker, owner, payload));
	}

	static void CreateLeakyCycle(List<Shell> retainedShells, List<TrackedCycle> tracked, int cycle)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var owner = CreateTrackerOwner(cycle, payload);
		var shell = CreateShellTarget(cycle);

		var tracker = CreateMenuBarTracker(owner);
		SetTarget(tracker, shell);
		SetTarget(tracker, null);

		retainedShells.Add(shell);
		tracked.Add(TrackedCycle.Create(cycle, tracker, owner, payload));
	}

	static ContentPage CreateTrackerOwner(int cycle, LeakPayload payload)
	{
		return new ContentPage
		{
			Title = $"Transient tracker owner {cycle + 1}",
			BindingContext = payload,
			Content = new VerticalStackLayout
			{
				Children =
				{
					new Label { Text = $"Menu owner {cycle + 1}" }
				}
			}
		};
	}

	static Shell CreateShellTarget(int cycle)
	{
		var shell = new Shell { Title = $"Retained Shell {cycle + 1}" };
		shell.MenuBarItems.Add(new MenuBarItem { Text = $"Shell menu {cycle + 1}" });

		var page = new ContentPage
		{
			Title = $"Shell content {cycle + 1}",
			Content = new Label { Text = $"Shell content {cycle + 1}" }
		};

		var content = new ShellContent
		{
			Title = $"Work {cycle + 1}",
			Content = page
		};

		var section = new ShellSection
		{
			Title = $"Section {cycle + 1}",
			Items = { content }
		};

		var item = new ShellItem
		{
			Title = $"Item {cycle + 1}",
			Items = { section }
		};

		shell.Items.Add(item);
		return shell;
	}

	static object CreateMenuBarTracker(Element owner)
	{
		return MenuBarTrackerConstructor.Invoke(new object?[] { owner, "MenuBar" });
	}

	static void SetTarget(object tracker, Page? target)
	{
		TargetProperty.SetValue(tracker, target);
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
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		ReportBytes = new byte[payloadBytes];

		for (var i = 0; i < ReportBytes.Length; i += 4096)
			ReportBytes[i] = (byte)(cycle + i);

		OpenDocuments = Enumerable.Range(1, 30)
			.Select(index => new MenuDocument(
				$"DOC-{cycle + 1:000}-{index:000}",
				$"Quarterly operations menu export {index}",
				"cached"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] ReportBytes { get; }

	public IReadOnlyList<MenuDocument> OpenDocuments { get; }
}

internal sealed record MenuDocument(string Id, string Title, string State);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference Tracker,
	WeakReference Owner,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(int cycle, object tracker, Element owner, LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(tracker),
			new WeakReference(owner),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveTrackers,
	int AliveOwners,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveTrackers = 0;
		var aliveOwners = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.Tracker.IsAlive)
				aliveTrackers++;

			if (cycle.Owner.IsAlive)
				aliveOwners++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveTrackers,
			aliveOwners,
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
	ScenarioResult Leak)
{
	public bool LeakProved =>
		Control.AlivePayloads == 0 &&
		Control.AliveOwners == 0 &&
		Control.AliveTrackers == 0 &&
		Leak.AlivePayloads == Leak.TrackedCycles &&
		Leak.AliveOwners == Leak.TrackedCycles &&
		Leak.AliveTrackers == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ShellMenuItemTrackerTargetLeakRepro",
			$"Cycles: {Cycles}",
			$"Payload per cycle: {PayloadMegabytesPerCycle} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			FormatScenario(Control),
			string.Empty,
			FormatScenario(Leak),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(BaselineManagedBytes)}",
			$"Managed heap final: {FormatBytes(FinalManagedBytes)}",
			$"Managed heap delta: {FormatBytes(FinalManagedBytes - BaselineManagedBytes)}");
	}

	static string FormatScenario(ScenarioResult result)
	{
		var expectedPayload = result.TrackedCycles == 0 ? 0 : result.TrackedCycles * 1024L * 1024L;
		var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  trackers alive after full GC: {result.AliveTrackers}/{result.TrackedCycles}",
			$"  owners alive after full GC: {result.AliveOwners}/{result.TrackedCycles}",
			$"  payloads alive after full GC: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : string.Empty;
		var value = Math.Abs(bytes);

		if (value >= 1024L * 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GiB";

		if (value >= 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d:0.0} MiB";

		if (value >= 1024L)
			return $"{sign}{value / 1024d:0.0} KiB";

		return $"{sign}{value} B";
	}
}
