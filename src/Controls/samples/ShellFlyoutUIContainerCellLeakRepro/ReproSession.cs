using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Handlers;
using UIKit;

namespace ShellFlyoutUIContainerCellLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	internal const long PayloadSizeBytes = PayloadMegabytesPerCycle * 1024L * 1024L;

	static readonly ConstructorInfo CellConstructor =
		typeof(UIContainerCell).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(string), typeof(View), typeof(Shell), typeof(object) },
			modifiers: null)
		?? throw new MissingMethodException(typeof(UIContainerCell).FullName, ".ctor(string, View, Shell, object)");

	static readonly MethodInfo DisconnectMethod =
		typeof(UIContainerCell).GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(UIContainerCell).FullName, "Disconnect");

	static readonly PropertyInfo ViewMeasureInvalidatedProperty =
		typeof(UIContainerCell).GetProperty("ViewMeasureInvalidated", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(UIContainerCell).FullName, "ViewMeasureInvalidated");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunExplicitDisconnectControl(mauiContext);
		var leak = RunMissingDisconnectScenario(mauiContext);

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

	static ScenarioResult RunExplicitDisconnectControl(IMauiContext mauiContext)
	{
		var retainedShells = new List<Shell>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(mauiContext, retainedShells, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("control: call UIContainerCell.Disconnect before disposal", tracked);
		GC.KeepAlive(retainedShells);
		return result;
	}

	static ScenarioResult RunMissingDisconnectScenario(IMauiContext mauiContext)
	{
		var retainedShells = new List<Shell>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(mauiContext, retainedShells, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("leak: Shell flyout UIContainerCell disposed without Disconnect", tracked);
		GC.KeepAlive(retainedShells);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(
		IMauiContext mauiContext,
		List<Shell> retainedShells,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var shell = CreateRetainedShell(cycle);
		retainedShells.Add(shell);

		var view = new PayloadFlyoutView(cycle);
		var handler = AttachPayloadHandler(mauiContext, view);
		var owner = new CellCacheOwner(cycle);
		var cell = CreateFlyoutCell(shell, shell.Items[0], view, owner, cycle);

		tracked.Add(TrackedCycle.Create(cycle, cell, view, handler, handler.Payload, owner));

		Disconnect(cell, shell);
		cell.Dispose();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakCycle(
		IMauiContext mauiContext,
		List<Shell> retainedShells,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var shell = CreateRetainedShell(cycle);
		retainedShells.Add(shell);

		var view = new PayloadFlyoutView(cycle);
		var handler = AttachPayloadHandler(mauiContext, view);
		var owner = new CellCacheOwner(cycle);
		var cell = CreateFlyoutCell(shell, shell.Items[0], view, owner, cycle);

		tracked.Add(TrackedCycle.Create(cycle, cell, view, handler, handler.Payload, owner));

		cell.Dispose();
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

	static PayloadFlyoutViewHandler AttachPayloadHandler(IMauiContext mauiContext, PayloadFlyoutView view)
	{
		var handler = new PayloadFlyoutViewHandler();
		((IElementHandler)handler).SetMauiContext(mauiContext);
		((IElementHandler)handler).SetVirtualView(view);
		return handler;
	}

	static UIContainerCell CreateFlyoutCell(
		Shell shell,
		Element flyoutContext,
		PayloadFlyoutView view,
		CellCacheOwner owner,
		int cycle)
	{
		var cell = (UIContainerCell)CellConstructor.Invoke(new object[] { $"flyout-cell-{cycle}", view, shell, flyoutContext });
		Action<UIContainerCell> callback = owner.OnViewMeasureInvalidated;
		ViewMeasureInvalidatedProperty.SetValue(cell, callback);
		return cell;
	}

	static void Disconnect(UIContainerCell cell, Shell shell)
	{
		DisconnectMethod.Invoke(cell, new object?[] { shell, false });
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

internal sealed class PayloadFlyoutView : View
{
	public PayloadFlyoutView(int cycle)
	{
		Cycle = cycle;
		WidthRequest = 320;
		HeightRequest = 56;
		Payload = new LeakPayload(cycle, ReproSession.PayloadSizeBytes);
		BindingContext = Payload;
	}

	public int Cycle { get; }

	public LeakPayload Payload { get; }
}

internal sealed class PayloadFlyoutViewHandler : ViewHandler<PayloadFlyoutView, PayloadNativeView>
{
	public static readonly IPropertyMapper<PayloadFlyoutView, PayloadFlyoutViewHandler> Mapper =
		new PropertyMapper<PayloadFlyoutView, PayloadFlyoutViewHandler>(ViewHandler.ViewMapper);

	public PayloadFlyoutViewHandler() : base(Mapper)
	{
	}

	public LeakPayload Payload { get; private set; } = null!;

	protected override PayloadNativeView CreatePlatformView()
	{
		Payload = VirtualView.Payload;
		return new PayloadNativeView(VirtualView.Cycle);
	}
}

internal sealed class PayloadNativeView : UIView
{
	public PayloadNativeView(int cycle)
	{
		Cycle = cycle;
		Frame = new CGRect(0, 0, 320, 56);
	}

	public int Cycle { get; }
}

internal sealed class CellCacheOwner
{
	public CellCacheOwner(int cycle)
	{
		Cycle = cycle;
	}

	public int Cycle { get; }

	public UIContainerCell? LastInvalidatedCell { get; private set; }

	public void OnViewMeasureInvalidated(UIContainerCell cell)
	{
		LastInvalidatedCell = cell;
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

		RecentOrders = Enumerable.Range(1, 40)
			.Select(index => new OperationsOrder(
				$"OPS-{cycle + 1:000}-{index:000}",
				$"Flyout command cache package {index}",
				index % 4 == 0 ? "Escalated" : "Ready"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<OperationsOrder> RecentOrders { get; }
}

internal sealed record OperationsOrder(string Id, string Summary, string Status);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference Cell,
	WeakReference FlyoutView,
	WeakReference Handler,
	WeakReference Payload,
	WeakReference CacheOwner,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		UIContainerCell cell,
		PayloadFlyoutView view,
		PayloadFlyoutViewHandler handler,
		LeakPayload payload,
		CellCacheOwner owner)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(cell),
			new WeakReference(view),
			new WeakReference(handler),
			new WeakReference(payload),
			new WeakReference(owner),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveCells,
	int AliveFlyoutViews,
	int AliveHandlers,
	int AlivePayloads,
	int AliveCacheOwners,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveCells = 0;
		var aliveFlyoutViews = 0;
		var aliveHandlers = 0;
		var alivePayloads = 0;
		var aliveCacheOwners = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.Cell.IsAlive)
				aliveCells++;

			if (cycle.FlyoutView.IsAlive)
				aliveFlyoutViews++;

			if (cycle.Handler.IsAlive)
				aliveHandlers++;

			if (cycle.CacheOwner.IsAlive)
				aliveCacheOwners++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveCells,
			aliveFlyoutViews,
			aliveHandlers,
			alivePayloads,
			aliveCacheOwners,
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
		Control.AliveCells == 0 &&
		Control.AliveFlyoutViews == 0 &&
		Control.AliveHandlers == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AliveCacheOwners == 0 &&
		Leak.AliveCells == Leak.TrackedCycles &&
		Leak.AliveFlyoutViews == Leak.TrackedCycles &&
		Leak.AliveHandlers == Leak.TrackedCycles &&
		Leak.AlivePayloads == Leak.TrackedCycles &&
		Leak.AliveCacheOwners == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ShellFlyoutUIContainerCellLeakRepro",
			$"Cycles: {Cycles}",
			$"Payload per flyout cell view: {PayloadMegabytesPerCycle} MiB",
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
			$"  disposed UIContainerCells alive after full GC: {result.AliveCells}/{result.TrackedCycles}",
			$"  flyout template views alive after full GC: {result.AliveFlyoutViews}/{result.TrackedCycles}",
			$"  handlers alive after full GC: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  source-style cache owners alive after full GC: {result.AliveCacheOwners}/{result.TrackedCycles}",
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
