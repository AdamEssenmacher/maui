#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using AColor = Android.Graphics.Color;
using MauiSwipeItem = Microsoft.Maui.Controls.SwipeItem;

namespace AndroidSwipeItemMenuItemDrawableRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadBytesPerDrawable = 1024 * 1024;

	static readonly PropertyMapper<ISwipeItemMenuItem, ISwipeItemMenuItemHandler> EmptyMapper = new();
	static readonly List<TextView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear compound drawables and reset SourceLoader before disconnect",
			context,
			clearNativeDrawableAndResetLoader: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves compound drawable and SourceLoader result assigned",
			context,
			clearNativeDrawableAndResetLoader: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadBytesPerDrawable,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeDrawableAndResetLoader)
	{
		var ledger = new ScenarioLedger(name);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
			await CreateCycleAsync(context, ledger, i, tracked, clearNativeDrawableAndResetLoader);

		ForceFullGc();

		return ScenarioResult.From(name, ledger, tracked);
	}

	static async Task CreateCycleAsync(
		IMauiContext context,
		ScenarioLedger ledger,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeDrawableAndResetLoader)
	{
		var source = new TrackingImageSource(ledger, cycle, PayloadBytesPerDrawable);
		var item = new MauiSwipeItem
		{
			Text = $"Archive item {cycle:000}",
			IconImageSource = source,
			BackgroundColor = Colors.DarkBlue
		};
		var handler = new TestSwipeItemMenuItemHandler(EmptyMapper);

		AttachHandler(item, handler, context);
		await SwipeItemMenuItemHandler.MapSourceAsync(handler, item);

		var platformView = (TextView)handler.PlatformView;
		var drawable = source.LoadedDrawable
			?? throw new InvalidOperationException("SwipeItemMenuItem did not load a drawable.");

		if (clearNativeDrawableAndResetLoader)
		{
			platformView.SetCompoundDrawables(null, null, null, null);
			handler.SourceLoader.Reset();
		}

		((IElementHandler)handler).DisconnectHandler();
		item.IconImageSource = null;
		item.Handler = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(cycle, platformView, item, handler, source, drawable));
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
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

	sealed class TestSwipeItemMenuItemHandler : SwipeItemMenuItemHandler
	{
		public TestSwipeItemMenuItemHandler(IPropertyMapper mapper)
			: base(mapper)
		{
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TextView> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		WeakReference<TrackingImageSource> Source,
		WeakReference<TrackingDrawable> Drawable,
		WeakReference<byte[]> Payload,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			TextView platformView,
			object virtualView,
			IElementHandler handler,
			TrackingImageSource source,
			TrackingDrawable drawable)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TextView>(platformView),
				new WeakReference<object>(virtualView),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<TrackingImageSource>(source),
				new WeakReference<TrackingDrawable>(drawable),
				new WeakReference<byte[]>(drawable.Payload),
				drawable.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ServiceResultsCreated,
		int ServiceResultsDisposed,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveSources,
		int AliveDrawables,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(string name, ScenarioLedger ledger, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var aliveSources = 0;
			var aliveDrawables = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;

				if (cycle.Drawable.TryGetTarget(out _))
					aliveDrawables++;

				if (cycle.Payload.TryGetTarget(out _))
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ResultsCreated,
				ledger.ResultsDisposed,
				aliveNativePeers,
				aliveVirtualViews,
				aliveHandlers,
				aliveSources,
				aliveDrawables,
				alivePayloads,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerDrawable,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.ServiceResultsCreated == Cycles &&
		Control.ServiceResultsDisposed == Cycles &&
		Control.AlivePayloads == 0 &&
		Current.ServiceResultsCreated == Cycles &&
		Current.ServiceResultsDisposed == 0 &&
		Current.AliveNativePeers == Cycles &&
		Current.AliveVirtualViews == 0 &&
		Current.AliveHandlers == 0 &&
		Current.AliveSources == 0 &&
		Current.AliveDrawables == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidSwipeItemMenuItemDrawableRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per drawable: {PayloadBytesPerDrawable / 1024 / 1024} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {controlMiB:N1} MiB",
			$"Current retained payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  service results created/disposed: {result.ServiceResultsCreated}/{result.ServiceResultsDisposed}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}",
			$"  alive Drawables: {result.AliveDrawables}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}

internal sealed class TrackingImageSource : ImageSource
{
	public TrackingImageSource(ScenarioLedger ledger, int cycle, int payloadBytes)
	{
		Ledger = ledger;
		Cycle = cycle;
		PayloadBytes = payloadBytes;
	}

	public ScenarioLedger Ledger { get; }

	public int Cycle { get; }

	public int PayloadBytes { get; }

	public TrackingDrawable? LoadedDrawable { get; set; }

	public override bool IsEmpty => false;
}

internal sealed class TrackingImageSourceService : ImageSourceService, IImageSourceService<TrackingImageSource>
{
	public override Task<IImageSourceServiceResult<Drawable>?> GetDrawableAsync(
		IImageSource imageSource,
		Context context,
		CancellationToken cancellationToken = default)
	{
		if (imageSource is not TrackingImageSource trackingSource)
			return Task.FromResult<IImageSourceServiceResult<Drawable>?>(null);

		var drawable = new TrackingDrawable(
			trackingSource.Cycle,
			trackingSource.PayloadBytes);

		trackingSource.LoadedDrawable = drawable;
		trackingSource.Ledger.RecordCreated();

		var result = new ImageSourceServiceResult(
			drawable,
			dispose: trackingSource.Ledger.RecordDisposed);

		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(result);
	}
}

internal sealed class TrackingDrawable : ColorDrawable
{
	public TrackingDrawable(int cycle, int payloadBytes)
		: base(AColor.Rgb((cycle * 37) % 255, (cycle * 67) % 255, (cycle * 97) % 255))
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		Payload = new byte[payloadBytes];

		for (var i = 0; i < Payload.Length; i += 4096)
			Payload[i] = (byte)(cycle + i);
	}

	public override int IntrinsicWidth => 96;

	public override int IntrinsicHeight => 96;

	public int Cycle { get; }

	public int PayloadBytes { get; }

	public byte[] Payload { get; }
}

internal sealed class ScenarioLedger
{
	readonly string _name;

	public ScenarioLedger(string name)
	{
		_name = name;
	}

	public string Name => _name;

	public int ResultsCreated { get; private set; }

	public int ResultsDisposed { get; private set; }

	public void RecordCreated() => ResultsCreated++;

	public void RecordDisposed() => ResultsDisposed++;
}
