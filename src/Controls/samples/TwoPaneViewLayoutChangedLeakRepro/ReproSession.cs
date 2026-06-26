using System.Reflection;
using Microsoft.Maui.Controls.Foldable;
using Microsoft.Maui.Foldable;
using Microsoft.Maui.Graphics;

namespace TwoPaneViewLayoutChangedLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly Assembly FoldableAssembly = typeof(TwoPaneView).Assembly;
	static readonly Type IFoldableServiceType =
		typeof(Shell).Assembly.GetType("Microsoft.Maui.Foldable.IFoldableService", throwOnError: true)!;

	static readonly ConstructorInfo TwoPaneViewConstructor =
		typeof(TwoPaneView).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: new[] { IFoldableServiceType },
			modifiers: null)!;

	static readonly MethodInfo OnHandlerChangingCoreMethod =
		typeof(TwoPaneView).GetMethod(
			"OnHandlerChangingCore",
			BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly FieldInfo LayoutGuideField =
		typeof(TwoPaneView).GetField(
			"_twoPaneViewLayoutGuide",
			BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly MethodInfo SetFoldableServiceMethod =
		LayoutGuideField.FieldType.GetMethod(
			"SetFoldableService",
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: new[] { IFoldableServiceType },
			modifiers: null)!;

	static readonly ConstructorInfo HandlerChangingEventArgsConstructor =
		typeof(HandlerChangingEventArgs).GetConstructor(new[] { typeof(IElementHandler), typeof(IElementHandler) })!;

	static readonly MethodInfo DispatchProxyCreateMethod =
		typeof(DispatchProxy)
			.GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Single(method => method.Name == nameof(DispatchProxy.Create) && method.GetGenericArguments().Length == 2)
			.MakeGenericMethod(IFoldableServiceType, typeof(FakeFoldableServiceProxy));

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
		var retainedServices = new List<object>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateCycle(retainedServices, tracked, i, invokeHandlerChanging: false);

		ForceFullGc();
		var result = ScenarioResult.From("control: retained foldable service without handler-changing subscription", tracked);
		GC.KeepAlive(retainedServices);
		return result;
	}

	static ScenarioResult RunLeak()
	{
		var retainedServices = new List<object>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateCycle(retainedServices, tracked, i, invokeHandlerChanging: true);

		ForceFullGc();
		var result = ScenarioResult.From("leak: TwoPaneView subscribed to retained foldable service", tracked);
		GC.KeepAlive(retainedServices);
		return result;
	}

	static void CreateCycle(List<object> retainedServices, List<TrackedCycle> tracked, int cycle, bool invokeHandlerChanging)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var service = CreateFoldableServiceProxy();
		var view = (TwoPaneView)TwoPaneViewConstructor.Invoke(new[] { service });
		SetFoldableService(view, service);

		view.BindingContext = payload;
		view.Pane1 = new Label { Text = $"Operations pane {cycle + 1}" };
		view.Pane2 = new Label { Text = $"Detail pane {cycle + 1}" };

		if (invokeHandlerChanging)
			InvokeHandlerChanging(view);

		retainedServices.Add(service);
		tracked.Add(TrackedCycle.Create(cycle, view, payload, GetProxy(service).LayoutChangedSubscriberCount));
	}

	static object CreateFoldableServiceProxy()
	{
		return DispatchProxyCreateMethod.Invoke(null, null)!;
	}

	static FakeFoldableServiceProxy GetProxy(object service)
	{
		return (FakeFoldableServiceProxy)service;
	}

	static void SetFoldableService(TwoPaneView view, object service)
	{
		var layoutGuide = LayoutGuideField.GetValue(view)!;
		SetFoldableServiceMethod.Invoke(layoutGuide, new[] { service });
	}

	static void InvokeHandlerChanging(TwoPaneView view)
	{
		var args = HandlerChangingEventArgsConstructor.Invoke(new object?[] { null, null });
		OnHandlerChangingCoreMethod.Invoke(view, new[] { args });
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

public class FakeFoldableServiceProxy : DispatchProxy
{
	EventHandler? _screenChanged;
	EventHandler<FoldEventArgs>? _layoutChanged;

	public int LayoutChangedSubscriberCount =>
		_layoutChanged?.GetInvocationList().Length ?? 0;

	protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
	{
		return targetMethod?.Name switch
		{
			"add_OnScreenChanged" => AddScreenChanged(args),
			"remove_OnScreenChanged" => RemoveScreenChanged(args),
			"add_OnLayoutChanged" => AddLayoutChanged(args),
			"remove_OnLayoutChanged" => RemoveLayoutChanged(args),
			"get_IsSpanned" => false,
			"get_IsLandscape" => false,
			"get_ScaledScreenSize" => Size.Zero,
			"GetHinge" => Rect.Zero,
			"GetLocationOnScreen" => null,
			"GetHingeAngleAsync" => Task.FromResult(0),
			_ => throw new NotSupportedException(targetMethod?.Name)
		};
	}

	object? AddScreenChanged(object?[]? args)
	{
		_screenChanged += (EventHandler)args![0]!;
		return null;
	}

	object? RemoveScreenChanged(object?[]? args)
	{
		_screenChanged -= (EventHandler)args![0]!;
		return null;
	}

	object? AddLayoutChanged(object?[]? args)
	{
		_layoutChanged += (EventHandler<FoldEventArgs>)args![0]!;
		return null;
	}

	object? RemoveLayoutChanged(object?[]? args)
	{
		_layoutChanged -= (EventHandler<FoldEventArgs>)args![0]!;
		return null;
	}
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		WorkspaceBytes = new byte[payloadBytes];

		for (var i = 0; i < WorkspaceBytes.Length; i += 4096)
			WorkspaceBytes[i] = (byte)(cycle + i);

		OpenPanes = Enumerable.Range(1, 24)
			.Select(index => new FoldablePaneState(
				$"PANE-{cycle + 1:000}-{index:000}",
				$"Foldable dashboard pane {index}",
				"cached layout state"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] WorkspaceBytes { get; }

	public IReadOnlyList<FoldablePaneState> OpenPanes { get; }
}

internal sealed record FoldablePaneState(string Id, string Title, string State);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference View,
	WeakReference Payload,
	long PayloadBytes,
	int LayoutChangedSubscriberCount)
{
	public static TrackedCycle Create(int cycle, TwoPaneView view, LeakPayload payload, int layoutChangedSubscriberCount)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(view),
			new WeakReference(payload),
			payload.PayloadBytes,
			layoutChangedSubscriberCount);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveViews,
	int AlivePayloads,
	int ServiceSubscriptions,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveViews = 0;
		var alivePayloads = 0;
		var subscriptions = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.View.IsAlive)
				aliveViews++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}

			subscriptions += cycle.LayoutChangedSubscriberCount;
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveViews,
			alivePayloads,
			subscriptions,
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
		Control.AliveViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.ServiceSubscriptions == 0 &&
		Leak.AliveViews == Leak.TrackedCycles &&
		Leak.AlivePayloads == Leak.TrackedCycles &&
		Leak.ServiceSubscriptions == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"TwoPaneViewLayoutChangedLeakRepro",
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
			$"  views alive after full GC: {result.AliveViews}/{result.TrackedCycles}",
			$"  payloads alive after full GC: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  service OnLayoutChanged subscriptions: {result.ServiceSubscriptions}",
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
