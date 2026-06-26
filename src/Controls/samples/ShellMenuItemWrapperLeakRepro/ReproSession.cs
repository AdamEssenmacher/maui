using System.Reflection;

namespace ShellMenuItemWrapperLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly Type MenuShellItemType =
		typeof(Shell).Assembly.GetType("Microsoft.Maui.Controls.MenuShellItem", throwOnError: true)!;

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
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(tracked, i);

		ForceFullGc();
		return ScenarioResult.From("control: dropped ordinary ShellItem wrappers", tracked);
	}

	static ScenarioResult RunLeak()
	{
		var retainedMenuItems = new List<MenuItem>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakyCycle(retainedMenuItems, tracked, i);

		ForceFullGc();
		var result = ScenarioResult.From("leak: long-lived MenuItem roots MenuShellItem wrapper", tracked);
		GC.KeepAlive(retainedMenuItems);
		return result;
	}

	static void CreateControlCycle(List<TrackedCycle> tracked, int cycle)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var shellItem = new ShellItem
		{
			Title = $"Transient shell item {cycle + 1}",
			BindingContext = payload
		};

		tracked.Add(TrackedCycle.Create(cycle, shellItem, payload));
	}

	static void CreateLeakyCycle(List<MenuItem> retainedMenuItems, List<TrackedCycle> tracked, int cycle)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var menuItem = new MenuItem
		{
			Text = $"Retained menu item {cycle + 1}"
		};

		var wrapper = (ShellItem)Activator.CreateInstance(
			MenuShellItemType,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			args: new object[] { menuItem },
			culture: null)!;

		wrapper.BindingContext = payload;

		retainedMenuItems.Add(menuItem);
		tracked.Add(TrackedCycle.Create(cycle, wrapper, payload));
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
		DocumentBytes = new byte[payloadBytes];

		for (var i = 0; i < DocumentBytes.Length; i += 4096)
			DocumentBytes[i] = (byte)(cycle + i);

		RecentCases = Enumerable.Range(1, 25)
			.Select(index => new CustomerCase(
				$"CASE-{cycle + 1:000}-{index:000}",
				$"Customer menu action package {index}",
				"Cached for menu command review"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<CustomerCase> RecentCases { get; }
}

internal sealed record CustomerCase(string Id, string Summary, string Status);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference Wrapper,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(int cycle, ShellItem wrapper, LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(wrapper),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveWrappers,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveWrappers = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.Wrapper.IsAlive)
				aliveWrappers++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveWrappers,
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
		Control.AliveWrappers == 0 &&
		Leak.AlivePayloads == Leak.TrackedCycles &&
		Leak.AliveWrappers == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ShellMenuItemWrapperLeakRepro",
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
			$"  wrappers alive after full GC: {result.AliveWrappers}/{result.TrackedCycles}",
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
