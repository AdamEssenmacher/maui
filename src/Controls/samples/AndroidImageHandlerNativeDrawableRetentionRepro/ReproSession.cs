#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics.Drawables;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;
using MauiButton = Microsoft.Maui.Controls.Button;
using MauiImage = Microsoft.Maui.Controls.Image;
using MauiImageButton = Microsoft.Maui.Controls.ImageButton;

namespace AndroidImageHandlerNativeDrawableRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadBytesPerDrawable = 1024 * 1024;

	static readonly PropertyMapper<IImage, IImageHandler> EmptyImageMapper = new();
	static readonly PropertyMapper<IImageButton, IImageButtonHandler> EmptyImageButtonMapper = new();
	static readonly PropertyMapper<IButton, IButtonHandler> EmptyButtonMapper = new();
	static readonly List<AView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native drawable/icon before disconnect",
			context,
			clearNativeDrawable: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native drawable/icon assigned",
			context,
			clearNativeDrawable: false);

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
		bool clearNativeDrawable)
	{
		var ledger = new ScenarioLedger(name);
		var tracked = new List<TrackedCycle>(Cycles * 3);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateImageCycleAsync(context, ledger, i, tracked, clearNativeDrawable);
			await CreateImageButtonCycleAsync(context, ledger, i, tracked, clearNativeDrawable);
			await CreateButtonCycleAsync(context, ledger, i, tracked, clearNativeDrawable);
		}

		ForceFullGc();

		return ScenarioResult.From(name, ledger, tracked);
	}

	static async Task CreateImageCycleAsync(
		IMauiContext context,
		ScenarioLedger ledger,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeDrawable)
	{
		var source = new TrackingImageSource(ledger, "Image", cycle, PayloadBytesPerDrawable);
		var image = new MauiImage
		{
			Source = source,
			WidthRequest = 320,
			HeightRequest = 180
		};
		var handler = new ImageHandler(EmptyImageMapper);

		AttachHandler(image, handler, context);
		await ImageHandler.MapSourceAsync(handler, image);

		var platformView = handler.PlatformView;
		var drawable = source.LoadedDrawable ?? throw new InvalidOperationException("Image did not load a drawable.");

		if (clearNativeDrawable)
			platformView.SetImageDrawable(null);

		((IElementHandler)handler).DisconnectHandler();
		image.Source = null;
		image.Handler = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("Image", cycle, platformView, image, handler, source, drawable));
	}

	static async Task CreateImageButtonCycleAsync(
		IMauiContext context,
		ScenarioLedger ledger,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeDrawable)
	{
		var source = new TrackingImageSource(ledger, "ImageButton", cycle, PayloadBytesPerDrawable);
		var imageButton = new MauiImageButton
		{
			Source = source,
			WidthRequest = 96,
			HeightRequest = 96
		};
		var handler = new ImageButtonHandler(EmptyImageButtonMapper);

		AttachHandler(imageButton, handler, context);
		await ImageHandler.MapSourceAsync(handler, imageButton);

		var platformView = handler.PlatformView;
		var drawable = source.LoadedDrawable ?? throw new InvalidOperationException("ImageButton did not load a drawable.");

		if (clearNativeDrawable)
			platformView.SetImageDrawable(null);

		((IElementHandler)handler).DisconnectHandler();
		imageButton.Source = null;
		imageButton.Handler = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("ImageButton", cycle, platformView, imageButton, handler, source, drawable));
	}

	static async Task CreateButtonCycleAsync(
		IMauiContext context,
		ScenarioLedger ledger,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeDrawable)
	{
		var source = new TrackingImageSource(ledger, "Button", cycle, PayloadBytesPerDrawable);
		var button = new MauiButton
		{
			Text = $"Item {cycle:000}",
			ImageSource = source,
			WidthRequest = 320,
			HeightRequest = 56
		};
		var handler = new ButtonHandler(EmptyButtonMapper);

		AttachHandler(button, handler, context);
		await ButtonHandler.MapImageSourceAsync(handler, button);

		var platformView = handler.PlatformView;
		var drawable = source.LoadedDrawable ?? throw new InvalidOperationException("Button did not load a drawable.");

		if (clearNativeDrawable)
			platformView.Icon = null;

		((IElementHandler)handler).DisconnectHandler();
		button.ImageSource = null;
		button.Handler = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("Button", cycle, platformView, button, handler, source, drawable));
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

	internal sealed record TrackedCycle(
		string ControlType,
		int Cycle,
		WeakReference<AView> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		WeakReference<TrackingImageSource> Source,
		WeakReference<TrackingDrawable> Drawable,
		WeakReference<byte[]> Payload,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			string controlType,
			int cycle,
			AView platformView,
			object virtualView,
			IElementHandler handler,
			TrackingImageSource source,
			TrackingDrawable drawable)
		{
			return new TrackedCycle(
				controlType,
				cycle,
				new WeakReference<AView>(platformView),
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
		long RetainedPayloadBytes,
		IReadOnlyDictionary<string, TypeResult> ByControlType)
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
			var byType = new Dictionary<string, TypeCounter>(StringComparer.Ordinal);

			foreach (var cycle in tracked)
			{
				var counter = GetCounter(byType, cycle.ControlType);
				counter.Tracked++;

				if (cycle.NativePeer.TryGetTarget(out _))
				{
					aliveNativePeers++;
					counter.AliveNativePeers++;
				}

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;

				if (cycle.Drawable.TryGetTarget(out _))
				{
					aliveDrawables++;
					counter.AliveDrawables++;
				}

				if (cycle.Payload.TryGetTarget(out _))
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
					counter.AlivePayloads++;
					counter.RetainedPayloadBytes += cycle.PayloadBytes;
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
				retainedPayloadBytes,
				byType.ToDictionary(pair => pair.Key, pair => pair.Value.ToResult(), StringComparer.Ordinal));
		}

		static TypeCounter GetCounter(Dictionary<string, TypeCounter> values, string controlType)
		{
			if (!values.TryGetValue(controlType, out var counter))
			{
				counter = new TypeCounter();
				values.Add(controlType, counter);
			}

			return counter;
		}
	}

	internal sealed record TypeResult(
		int Tracked,
		int AliveNativePeers,
		int AliveDrawables,
		int AlivePayloads,
		long RetainedPayloadBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int AliveNativePeers { get; set; }
		public int AliveDrawables { get; set; }
		public int AlivePayloads { get; set; }
		public long RetainedPayloadBytes { get; set; }

		public TypeResult ToResult() =>
			new(Tracked, AliveNativePeers, AliveDrawables, AlivePayloads, RetainedPayloadBytes);
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
	int TotalCycles => Cycles * 3;

	public bool LeakProved =>
		Control.ServiceResultsCreated == TotalCycles &&
		Control.ServiceResultsDisposed == TotalCycles &&
		Control.AlivePayloads == 0 &&
		Current.ServiceResultsCreated == TotalCycles &&
		Current.ServiceResultsDisposed == TotalCycles &&
		Current.AliveNativePeers == TotalCycles &&
		Current.ByControlType.TryGetValue("Image", out var image) &&
		image.AliveDrawables == Cycles &&
		image.AlivePayloads == Cycles &&
		Current.ByControlType.TryGetValue("ImageButton", out var imageButton) &&
		imageButton.AliveDrawables == Cycles &&
		imageButton.AlivePayloads == Cycles &&
		Current.ByControlType.TryGetValue("Button", out var button) &&
		button.AliveDrawables == 0 &&
		button.AlivePayloads == 0;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidImageHandlerNativeDrawableRetentionRepro",
			$"Cycles per control type: {Cycles}",
			$"Total handler cycles per scenario: {TotalCycles}",
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
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  service results created/disposed: {result.ServiceResultsCreated}/{result.ServiceResultsDisposed}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}",
			$"  alive Drawables: {result.AliveDrawables}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.AliveNativePeers}/{value.Tracked}, drawable={value.AliveDrawables}/{value.Tracked}, payload={value.AlivePayloads}/{value.Tracked}, retained={value.RetainedPayloadBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}

internal sealed class TrackingImageSource : ImageSource
{
	public TrackingImageSource(ScenarioLedger ledger, string controlType, int cycle, int payloadBytes)
	{
		Ledger = ledger;
		ControlType = controlType;
		Cycle = cycle;
		PayloadBytes = payloadBytes;
	}

	public ScenarioLedger Ledger { get; }

	public string ControlType { get; }

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
			trackingSource.ControlType,
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
	public TrackingDrawable(string controlType, int cycle, int payloadBytes)
		: base(AColor.Rgb((cycle * 37) % 255, (cycle * 67) % 255, (cycle * 97) % 255))
	{
		ControlType = controlType;
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		Payload = new byte[payloadBytes];

		for (var i = 0; i < Payload.Length; i += 4096)
			Payload[i] = (byte)(cycle + i + controlType.Length);
	}

	public string ControlType { get; }

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
