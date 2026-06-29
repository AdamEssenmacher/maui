#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Views;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Graphics;
using AView = Android.Views.View;

namespace AndroidViewCellContainerRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	const int ContextPayloadBytes = 512 * 1024;
	const int CellPayloadBytes = 512 * 1024;
	const int ViewPayloadBytes = 512 * 1024;
	const int TotalPayloadBytesPerCycle = ContextPayloadBytes + CellPayloadBytes + ViewPayloadBytes;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly Type ViewCellContainerType = typeof(ViewCellRenderer).GetNestedType("ViewCellContainer", BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(ViewCellRenderer).FullName, "ViewCellContainer");
	static readonly FieldInfo ViewCellField = ViewCellContainerType.GetField("_viewCell", InstanceNonPublic)
		?? throw new MissingFieldException(ViewCellContainerType.FullName, "_viewCell");
	static readonly FieldInfo ViewHandlerField = ViewCellContainerType.GetField("_viewHandler", InstanceNonPublic)
		?? throw new MissingFieldException(ViewCellContainerType.FullName, "_viewHandler");
	static readonly FieldInfo CurrentViewField = ViewCellContainerType.GetField("_currentView", InstanceNonPublic)
		?? throw new MissingFieldException(ViewCellContainerType.FullName, "_currentView");
	static readonly FieldInfo TapGestureDetectorField = ViewCellContainerType.GetField("_tapGestureDetector", InstanceNonPublic)
		?? throw new MissingFieldException(ViewCellContainerType.FullName, "_tapGestureDetector");
	static readonly FieldInfo LongPressGestureDetectorField = ViewCellContainerType.GetField("_longPressGestureDetector", InstanceNonPublic)
		?? throw new MissingFieldException(ViewCellContainerType.FullName, "_longPressGestureDetector");
	static readonly FieldInfo ListViewRendererField = ViewCellContainerType.GetField("_listViewRenderer", InstanceNonPublic)
		?? throw new MissingFieldException(ViewCellContainerType.FullName, "_listViewRenderer");

	static readonly List<AView> RetainedContainers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext appContext)
	{
		RetainedContainers.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var androidContext = appContext.Context
			?? throw new InvalidOperationException("The app MauiContext has no Android context.");

		var control = await RunScenarioAsync(
			"control: clear ViewCellContainer view-cell, handler, and native child fields after disconnect",
			appContext.Services,
			androidContext,
			clearContainerFields: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves ViewCellContainer fields assigned",
			appContext.Services,
			androidContext,
			clearContainerFields: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedContainers);

		return new ReproReport(
			Cycles,
			ContextPayloadBytes,
			CellPayloadBytes,
			ViewPayloadBytes,
			TotalPayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IServiceProvider services,
		Context androidContext,
		bool clearContainerFields)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(services, androidContext, i, tracked, clearContainerFields);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IServiceProvider services,
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearContainerFields)
	{
		var serviceProvider = new PayloadServiceProvider(services, cycle, ContextPayloadBytes);
		var cycleContext = new MauiContext(serviceProvider, androidContext);
		var cellPayload = new Payload("cell", cycle, CellPayloadBytes);
		var viewPayload = new Payload("view", cycle, ViewPayloadBytes);
		var child = new BoxView
		{
			WidthRequest = 40,
			HeightRequest = 40,
			Color = Colors.CornflowerBlue,
			BindingContext = viewPayload
		};
		var cell = new ViewCell
		{
			View = child,
			BindingContext = cellPayload
		};
		var parent = new ListView
		{
			HasUnevenRows = false,
			RowHeight = 44
		};

		var renderer = new ViewCellRenderer();
		renderer.ParentView = parent;
		renderer.SetMauiContext(cycleContext);
		cell.Handler = renderer;
		renderer.SetVirtualView(cell);

		if (renderer.PlatformView is not AView container ||
			!ViewCellContainerType.IsInstanceOfType(container))
		{
			throw new InvalidOperationException($"Expected ViewCellContainer, got {renderer.PlatformView?.GetType().FullName ?? "<null>"}.");
		}

		if (GetContainerViewCell(container) is not ViewCell ||
			GetContainerViewHandler(container) is null)
		{
			throw new InvalidOperationException("ViewCellContainer was not initialized with a ViewCell and child handler.");
		}

		((IElementHandler)renderer).DisconnectHandler();

		if (clearContainerFields)
			ClearContainerFields(container);

		RetainedContainers.Add(container);
		tracked.Add(TrackedCycle.Create(cycle, container, cell, child, renderer, cycleContext, serviceProvider, cellPayload, viewPayload));
	}

	static ViewCell? GetContainerViewCell(AView container)
	{
		return ViewCellField.GetValue(container) as ViewCell;
	}

	static object? GetContainerViewHandler(AView container)
	{
		return ViewHandlerField.GetValue(container);
	}

	static AView? GetCurrentView(AView container)
	{
		return CurrentViewField.GetValue(container) as AView;
	}

	static void ClearContainerFields(AView container)
	{
		ViewCellField.SetValue(container, null);
		ViewHandlerField.SetValue(container, null);
		CurrentViewField.SetValue(container, null);
		TapGestureDetectorField.SetValue(container, null);
		LongPressGestureDetectorField.SetValue(container, null);
		ListViewRendererField.SetValue(container, null);
	}

	static bool HasPayloadViewCell(AView container)
	{
		return GetContainerViewCell(container)?.BindingContext is Payload;
	}

	static bool HasPayloadViewHandler(AView container)
	{
		return GetContainerViewHandler(container) is not null;
	}

	static bool HasCurrentView(AView container)
	{
		return GetCurrentView(container) is not null;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<AView> Container,
		WeakReference<ViewCell> ViewCell,
		WeakReference<BoxView> ChildView,
		WeakReference<ViewCellRenderer> Renderer,
		WeakReference<MauiContext> MauiContext,
		WeakReference<PayloadServiceProvider> ServiceProvider,
		WeakReference<byte[]> ContextPayload,
		WeakReference<Payload> CellPayload,
		WeakReference<byte[]> CellPayloadBytes,
		WeakReference<Payload> ViewPayload,
		WeakReference<byte[]> ViewPayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			AView container,
			ViewCell viewCell,
			BoxView childView,
			ViewCellRenderer renderer,
			MauiContext mauiContext,
			PayloadServiceProvider serviceProvider,
			Payload cellPayload,
			Payload viewPayload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<AView>(container),
				new WeakReference<ViewCell>(viewCell),
				new WeakReference<BoxView>(childView),
				new WeakReference<ViewCellRenderer>(renderer),
				new WeakReference<MauiContext>(mauiContext),
				new WeakReference<PayloadServiceProvider>(serviceProvider),
				new WeakReference<byte[]>(serviceProvider.Payload),
				new WeakReference<Payload>(cellPayload),
				new WeakReference<byte[]>(cellPayload.Bytes),
				new WeakReference<Payload>(viewPayload),
				new WeakReference<byte[]>(viewPayload.Bytes));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveContainers,
		int AliveViewCells,
		int AliveChildViews,
		int AliveRenderers,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AliveContextPayloads,
		int AliveCellPayloads,
		int AliveCellPayloadByteArrays,
		int AliveViewPayloads,
		int AliveViewPayloadByteArrays,
		int ContainersWithPayloadViewCell,
		int ContainersWithViewHandler,
		int ContainersWithCurrentView,
		long RetainedContextPayloadBytes,
		long RetainedCellPayloadBytes,
		long RetainedViewPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveContainers = 0;
			var aliveViewCells = 0;
			var aliveChildViews = 0;
			var aliveRenderers = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var aliveContextPayloads = 0;
			var aliveCellPayloads = 0;
			var aliveCellPayloadByteArrays = 0;
			var aliveViewPayloads = 0;
			var aliveViewPayloadByteArrays = 0;
			var containersWithPayloadViewCell = 0;
			var containersWithViewHandler = 0;
			var containersWithCurrentView = 0;
			long retainedContextPayloadBytes = 0;
			long retainedCellPayloadBytes = 0;
			long retainedViewPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Container.TryGetTarget(out var container))
				{
					aliveContainers++;

					if (HasPayloadViewCell(container))
						containersWithPayloadViewCell++;

					if (HasPayloadViewHandler(container))
						containersWithViewHandler++;

					if (HasCurrentView(container))
						containersWithCurrentView++;
				}

				if (cycle.ViewCell.TryGetTarget(out _))
					aliveViewCells++;

				if (cycle.ChildView.TryGetTarget(out _))
					aliveChildViews++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.MauiContext.TryGetTarget(out _))
					aliveMauiContexts++;

				if (cycle.ServiceProvider.TryGetTarget(out _))
					aliveServiceProviders++;

				if (cycle.ContextPayload.TryGetTarget(out _))
				{
					aliveContextPayloads++;
					retainedContextPayloadBytes += ContextPayloadBytes;
				}

				if (cycle.CellPayload.TryGetTarget(out _))
					aliveCellPayloads++;

				if (cycle.CellPayloadBytes.TryGetTarget(out _))
				{
					aliveCellPayloadByteArrays++;
					retainedCellPayloadBytes += CellPayloadBytes;
				}

				if (cycle.ViewPayload.TryGetTarget(out _))
					aliveViewPayloads++;

				if (cycle.ViewPayloadBytes.TryGetTarget(out _))
				{
					aliveViewPayloadByteArrays++;
					retainedViewPayloadBytes += ViewPayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveContainers,
				aliveViewCells,
				aliveChildViews,
				aliveRenderers,
				aliveMauiContexts,
				aliveServiceProviders,
				aliveContextPayloads,
				aliveCellPayloads,
				aliveCellPayloadByteArrays,
				aliveViewPayloads,
				aliveViewPayloadByteArrays,
				containersWithPayloadViewCell,
				containersWithViewHandler,
				containersWithCurrentView,
				retainedContextPayloadBytes,
				retainedCellPayloadBytes,
				retainedViewPayloadBytes);
		}
	}
}

internal sealed class PayloadServiceProvider : IServiceProvider
{
	readonly IServiceProvider _inner;

	public PayloadServiceProvider(IServiceProvider inner, int cycle, int payloadBytes)
	{
		_inner = inner;
		Cycle = cycle;
		Payload = new byte[payloadBytes];
		Array.Fill(Payload, (byte)(cycle % 251));
	}

	public int Cycle { get; }

	public byte[] Payload { get; }

	public object? GetService(Type serviceType)
	{
		return _inner.GetService(serviceType);
	}
}

internal sealed class Payload
{
	public Payload(string kind, int cycle, int payloadBytes)
	{
		Kind = kind;
		Cycle = cycle;
		Bytes = new byte[payloadBytes];
		Array.Fill(Bytes, (byte)((cycle + kind.Length) % 251));
	}

	public string Kind { get; }

	public int Cycle { get; }

	public byte[] Bytes { get; }
}

internal sealed record ReproReport(
	int Cycles,
	int ContextPayloadBytes,
	int CellPayloadBytes,
	int ViewPayloadBytes,
	int TotalPayloadBytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveContainers == Cycles &&
		Current.AliveContainers == Cycles &&
		Control.ContainersWithPayloadViewCell == 0 &&
		Control.ContainersWithViewHandler == 0 &&
		Control.ContainersWithCurrentView == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveContextPayloads == 0 &&
		Control.AliveCellPayloadByteArrays == 0 &&
		Control.AliveViewPayloadByteArrays == 0 &&
		Current.ContainersWithPayloadViewCell == Cycles &&
		Current.ContainersWithViewHandler == Cycles &&
		Current.ContainersWithCurrentView == Cycles &&
		Current.AliveMauiContexts == Cycles &&
		Current.AliveServiceProviders == Cycles &&
		Current.AliveContextPayloads == Cycles &&
		Current.AliveViewCells == Cycles &&
		Current.AliveChildViews == Cycles &&
		Current.AliveCellPayloadByteArrays == Cycles &&
		Current.AliveViewPayloadByteArrays == Cycles &&
		Current.AliveRenderers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var currentTotalBytes = Current.RetainedContextPayloadBytes + Current.RetainedCellPayloadBytes + Current.RetainedViewPayloadBytes;

		return string.Join(Environment.NewLine,
			"AndroidViewCellContainerRetentionRepro",
			$"Cycles: {Cycles}",
			$"Context payload bytes per cycle: {ContextPayloadBytes:N0}",
			$"Cell payload bytes per cycle: {CellPayloadBytes:N0}",
			$"View payload bytes per cycle: {ViewPayloadBytes:N0}",
			$"Total payload bytes per cycle: {TotalPayloadBytesPerCycle:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {FormatBytes(Control.RetainedContextPayloadBytes + Control.RetainedCellPayloadBytes + Control.RetainedViewPayloadBytes)}",
			$"Current retained payload: {FormatBytes(currentTotalBytes)}",
			$"Current retained context payload: {FormatBytes(Current.RetainedContextPayloadBytes)}",
			$"Current retained cell payload: {FormatBytes(Current.RetainedCellPayloadBytes)}",
			$"Current retained view payload: {FormatBytes(Current.RetainedViewPayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native containers: {result.AliveContainers}/{result.TrackedCycles}",
			$"  alive ViewCells: {result.AliveViewCells}/{result.TrackedCycles}",
			$"  alive child views: {result.AliveChildViews}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive context payload byte arrays: {result.AliveContextPayloads}/{result.TrackedCycles}",
			$"  alive cell payloads: {result.AliveCellPayloads}/{result.TrackedCycles}",
			$"  alive cell payload byte arrays: {result.AliveCellPayloadByteArrays}/{result.TrackedCycles}",
			$"  alive view payloads: {result.AliveViewPayloads}/{result.TrackedCycles}",
			$"  alive view payload byte arrays: {result.AliveViewPayloadByteArrays}/{result.TrackedCycles}",
			$"  containers with payload _viewCell: {result.ContainersWithPayloadViewCell}/{result.TrackedCycles}",
			$"  containers with _viewHandler: {result.ContainersWithViewHandler}/{result.TrackedCycles}",
			$"  containers with _currentView: {result.ContainersWithCurrentView}/{result.TrackedCycles}",
			$"  retained context payload bytes: {result.RetainedContextPayloadBytes:N0}",
			$"  retained cell payload bytes: {result.RetainedCellPayloadBytes:N0}",
			$"  retained view payload bytes: {result.RetainedViewPayloadBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
