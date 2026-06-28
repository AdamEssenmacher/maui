using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using UIKit;

namespace HandlerToRendererShimElementRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly List<IReadOnlyList<HandlerToRendererShim>> RetainedDisposedShims = new();

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "handlertorenderershim-element-retention-results.txt");

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario("control: dispose shim and clear stale Element", clearElementAfterDispose: true);
		var current = RunScenario("current: dispose shim with Element still assigned", clearElementAfterDispose: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearElementAfterDispose)
	{
		var tracking = RunScenarioCore(clearElementAfterDispose);
		RetainedDisposedShims.Add(tracking.Shims);

		ForceFullGc();

		return ScenarioResult.From(name, tracking.Shims, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(bool clearElementAfterDispose)
	{
		var shims = new List<HandlerToRendererShim>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisposedShimCycle(i, shims, tracked, clearElementAfterDispose);
		}

		return new ScenarioTracking(shims, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedShimCycle(
		int cycle,
		List<HandlerToRendererShim> shims,
		List<TrackedCycle> tracked,
		bool clearElementAfterDispose)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var view = new PayloadContentView(cycle, payload);
		var handler = new PayloadPlatformViewHandler();
		var shim = new HandlerToRendererShim(handler);

		shim.SetElement(view);
		shim.Dispose();

		if (clearElementAfterDispose)
			ElementField(shim) = null!;

		shims.Add(shim);
		tracked.Add(TrackedCycle.Create(cycle, shim, handler, view, payload));
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Element>k__BackingField")]
	static extern ref VisualElement ElementField(HandlerToRendererShim shim);

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

sealed class PayloadContentView : ContentView
{
	public PayloadContentView(int cycle, LeakPayload payload)
	{
		Cycle = cycle;
		AutomationId = $"handler-to-renderer-shim-payload-{cycle + 1}";
		BindingContext = payload;
		Content = new Label { Text = payload.OpenDocuments[0].Title };
	}

	public int Cycle { get; }
}

sealed class PayloadPlatformViewHandler : IPlatformViewHandler, IDisposable
{
	readonly UIView _platformView = new();
	readonly UIViewController _viewController = new();
	IView? _virtualView;
	bool _disposed;

	public PayloadPlatformViewHandler()
	{
		_viewController.View = _platformView;
	}

	public bool HasContainer { get; set; }

	object? IViewHandler.ContainerView => null;

	UIView? IPlatformViewHandler.ContainerView => null;

	IView? IViewHandler.VirtualView => _virtualView;

	IElement? IElementHandler.VirtualView => _virtualView;

	object? IElementHandler.PlatformView => _platformView;

	public UIView? PlatformView => _platformView;

	public UIViewController? ViewController => _viewController;

	public IMauiContext? MauiContext { get; private set; }

	public Size GetDesiredSize(double widthConstraint, double heightConstraint) => new(44, 44);

	public void PlatformArrange(Rect frame)
	{
		_platformView.Frame = new CGRect(frame.X, frame.Y, frame.Width, frame.Height);
	}

	public void SetMauiContext(IMauiContext mauiContext)
	{
		MauiContext = mauiContext;
	}

	public void SetVirtualView(IElement view)
	{
		if (_virtualView?.Handler == this)
			_virtualView.Handler = null;

		_virtualView = (IView)view;

		if (_virtualView.Handler != this)
			_virtualView.Handler = this;
	}

	public void UpdateValue(string property)
	{
	}

	public void Invoke(string command, object? args = null)
	{
	}

	public void DisconnectHandler()
	{
		if (_virtualView?.Handler == this)
			_virtualView.Handler = null;

		_virtualView = null;
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		DisconnectHandler();
		_platformView.Dispose();
		_viewController.Dispose();
	}
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		SessionBytes = new byte[payloadBytes];

		for (var i = 0; i < SessionBytes.Length; i += 4096)
			SessionBytes[i] = (byte)(cycle + i);

		OpenDocuments = Enumerable.Range(1, 10)
			.Select(index => new OpenDocument(
				$"DOC-{cycle + 1:000}-{index:000}",
				$"Field report {index}",
				$"Draft editor state, validation cache, and attachments {cycle + 1}.{index}"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] SessionBytes { get; }

	public IReadOnlyList<OpenDocument> OpenDocuments { get; }
}

internal sealed record OpenDocument(string Id, string Title, string EditorState);

internal sealed record ScenarioTracking(
	IReadOnlyList<HandlerToRendererShim> Shims,
	IReadOnlyList<TrackedCycle> TrackedCycles);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference Shim,
	WeakReference Handler,
	WeakReference View,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		HandlerToRendererShim shim,
		PayloadPlatformViewHandler handler,
		PayloadContentView view,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(shim),
			new WeakReference(handler),
			new WeakReference(view),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int RetainedShimPeers,
	int TrackedCycles,
	int ShimsWithElementAssigned,
	int AliveShims,
	int AliveHandlers,
	int AliveViews,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(
		string name,
		IReadOnlyList<HandlerToRendererShim> shims,
		IReadOnlyList<TrackedCycle> cycles)
	{
		var shimsWithElementAssigned = 0;
		foreach (var shim in shims)
		{
			if (ElementField(shim) is not null)
				shimsWithElementAssigned++;
		}

		var aliveShims = 0;
		var aliveHandlers = 0;
		var aliveViews = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.Shim.IsAlive)
				aliveShims++;
			if (cycle.Handler.IsAlive)
				aliveHandlers++;
			if (cycle.View.IsAlive)
				aliveViews++;
			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			shims.Count,
			cycles.Count,
			shimsWithElementAssigned,
			aliveShims,
			aliveHandlers,
			aliveViews,
			alivePayloads,
			retainedPayloadBytes);
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Element>k__BackingField")]
	static extern ref VisualElement ElementField(HandlerToRendererShim shim);
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool Proven =>
		Control.RetainedShimPeers == Cycles &&
		Control.AliveShims == Cycles &&
		Control.ShimsWithElementAssigned == 0 &&
		Control.AliveViews == 0 &&
		Control.AlivePayloads == 0 &&
		Current.RetainedShimPeers == Cycles &&
		Current.AliveShims == Cycles &&
		Current.ShimsWithElementAssigned == Cycles &&
		Current.AliveViews == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine, new[]
		{
			"HandlerToRendererShim Element retention repro",
			$"RESULT: {(Proven ? "PROVEN" : "NOT PROVEN")}",
			$"cycles={Cycles}",
			$"payloadMegabytesPerCycle={PayloadMegabytesPerCycle}",
			$"baselineManagedBytes={BaselineManagedBytes}",
			$"finalManagedBytes={FinalManagedBytes}",
			Format(Control),
			Format(Current),
		});
	}

	static string Format(ScenarioResult result)
	{
		return string.Join(Environment.NewLine, new[]
		{
			$"scenario={result.Name}",
			$"  retainedShimPeers={result.RetainedShimPeers}",
			$"  trackedCycles={result.TrackedCycles}",
			$"  shimsWithElementAssigned={result.ShimsWithElementAssigned}/{result.TrackedCycles}",
			$"  aliveShims={result.AliveShims}/{result.TrackedCycles}",
			$"  aliveHandlers={result.AliveHandlers}/{result.TrackedCycles}",
			$"  aliveViews={result.AliveViews}/{result.TrackedCycles}",
			$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
			$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
			$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
		});
	}
}
