using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Controls.Platform.Compatibility;

namespace ShellFlyoutContentRendererLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	internal const long PayloadSizeBytes = PayloadMegabytesPerCycle * 1024L * 1024L;

	static readonly MethodInfo HandleShellPropertyChangedMethod =
		typeof(ShellFlyoutContentRenderer).GetMethod("HandleShellPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellFlyoutContentRenderer).FullName, "HandleShellPropertyChanged");

	static readonly FieldInfo TableViewControllerField =
		typeof(ShellFlyoutContentRenderer).GetField("_tableViewController", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutContentRenderer).FullName, "_tableViewController");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunExplicitTeardownControl();
		var leak = RunCurrentDisposeScenario();

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(mauiContext);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunExplicitTeardownControl()
	{
		var retainedShells = new List<Shell>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(retainedShells, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("control: explicit content-renderer event and table teardown", tracked);
		GC.KeepAlive(retainedShells);
		return result;
	}

	static ScenarioResult RunCurrentDisposeScenario()
	{
		var retainedShells = new List<Shell>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(retainedShells, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("leak: ShellFlyoutContentRenderer.Dispose without content teardown", tracked);
		GC.KeepAlive(retainedShells);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(
		List<Shell> retainedShells,
		List<TrackedCycle> tracked,
		int cycle)
	{
		using var autoreleasePool = new NSAutoreleasePool();

		var shell = CreateRetainedShell(cycle);
		retainedShells.Add(shell);

		var context = new ReproShellContext(shell, cycle);
		var renderer = new ShellFlyoutContentRenderer(context);
		var tableController = GetTableViewController(renderer);

		tracked.Add(TrackedCycle.Create(cycle, renderer, tableController, context, context.Payload));

		ExplicitTeardown(renderer, shell, tableController);

		tableController = null!;
		renderer = null!;
		context = null!;
		shell = null!;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakCycle(
		List<Shell> retainedShells,
		List<TrackedCycle> tracked,
		int cycle)
	{
		using var autoreleasePool = new NSAutoreleasePool();

		var shell = CreateRetainedShell(cycle);
		retainedShells.Add(shell);

		var context = new ReproShellContext(shell, cycle);
		var renderer = new ShellFlyoutContentRenderer(context);
		var tableController = GetTableViewController(renderer);

		tracked.Add(TrackedCycle.Create(cycle, renderer, tableController, context, context.Payload));

		renderer.Dispose();

		tableController = null!;
		renderer = null!;
		context = null!;
		shell = null!;
	}

	static Shell CreateRetainedShell(int cycle)
	{
		var shell = new Shell
		{
			Title = $"Retained shell {cycle + 1}"
		};

		var shellContent = new ShellContent
		{
			Title = "Dashboard",
			Content = new ContentPage
			{
				Title = "Dashboard",
				Content = new Label { Text = "Dashboard" }
			}
		};

		var shellSection = new ShellSection
		{
			Title = "Operations"
		};
		shellSection.Items.Add(shellContent);

		var flyoutItem = new FlyoutItem
		{
			Title = $"Operations {cycle + 1}"
		};
		flyoutItem.Items.Add(shellSection);
		shell.Items.Add(flyoutItem);

		return shell;
	}

	static object GetTableViewController(ShellFlyoutContentRenderer renderer)
	{
		return TableViewControllerField.GetValue(renderer)
			?? throw new InvalidOperationException("ShellFlyoutContentRenderer._tableViewController was null.");
	}

	static void ExplicitTeardown(
		ShellFlyoutContentRenderer renderer,
		Shell shell,
		object tableController)
	{
		var shellChanged = (PropertyChangedEventHandler)Delegate.CreateDelegate(typeof(PropertyChangedEventHandler), renderer, HandleShellPropertyChangedMethod);
		shell.PropertyChanged -= shellChanged;
		shellChanged = null!;

		((IDisposable)tableController).Dispose();
		renderer.Dispose();
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

internal sealed class ReproShellContext : IShellContext
{
	public ReproShellContext(Shell shell, int cycle)
	{
		Shell = shell;
		Payload = new LeakPayload(cycle, ReproSession.PayloadSizeBytes);
	}

	public bool AllowFlyoutGesture => true;

	public IShellItemRenderer CurrentShellItemRenderer => throw new NotSupportedException();

	public Shell Shell { get; }

	public LeakPayload Payload { get; }

	public IShellPageRendererTracker CreatePageRendererTracker() => throw new NotSupportedException();

	public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => new ShellFlyoutContentRenderer(this);

	public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

	public IShellNavBarAppearanceTracker CreateNavBarAppearanceTracker() => throw new NotSupportedException();

	public IShellTabBarAppearanceTracker CreateTabBarAppearanceTracker() => throw new NotSupportedException();

	public IShellSearchResultsRenderer CreateShellSearchResultsRenderer() => throw new NotSupportedException();
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

		DashboardRows = Enumerable.Range(1, 40)
			.Select(index => new DashboardRow(
				$"CTX-{cycle + 1:000}-{index:000}",
				$"Shell flyout renderer context payload row {index}",
				index % 5 == 0 ? "Attention" : "Normal"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<DashboardRow> DashboardRows { get; }
}

internal sealed record DashboardRow(string Id, string Summary, string Status);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference ContentRenderer,
	WeakReference TableController,
	WeakReference ShellContext,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		ShellFlyoutContentRenderer renderer,
		object tableController,
		ReproShellContext context,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(renderer),
			new WeakReference(tableController),
			new WeakReference(context),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveContentRenderers,
	int AliveTableControllers,
	int AliveShellContexts,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveContentRenderers = 0;
		var aliveTableControllers = 0;
		var aliveShellContexts = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.ContentRenderer.IsAlive)
				aliveContentRenderers++;

			if (cycle.TableController.IsAlive)
				aliveTableControllers++;

			if (cycle.ShellContext.IsAlive)
				aliveShellContexts++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveContentRenderers,
			aliveTableControllers,
			aliveShellContexts,
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
		Control.AliveContentRenderers == 0 &&
		Control.AliveTableControllers == 0 &&
		Control.AliveShellContexts == 0 &&
		Control.AlivePayloads == 0 &&
		Leak.AliveContentRenderers == Leak.TrackedCycles &&
		Leak.AliveTableControllers == Leak.TrackedCycles &&
		Leak.AliveShellContexts == Leak.TrackedCycles &&
		Leak.AlivePayloads == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ShellFlyoutContentRendererLeakRepro",
			$"Cycles: {Cycles}",
			$"Payload per Shell context: {PayloadMegabytesPerCycle} MiB",
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
		var expectedPayload = result.TrackedCycles == 0 ? 0 : result.TrackedCycles * ReproSession.PayloadSizeBytes;
		var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  disposed content renderers alive after full GC: {result.AliveContentRenderers}/{result.TrackedCycles}",
			$"  table controllers alive after full GC: {result.AliveTableControllers}/{result.TrackedCycles}",
			$"  Shell contexts alive after full GC: {result.AliveShellContexts}/{result.TrackedCycles}",
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
