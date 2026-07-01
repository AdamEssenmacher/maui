#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Google.Android.Material.Button;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;

namespace AndroidButtonHandlerImageRetentionRepro;

public static class ReproSession
{
	const int Iterations = 96;
	internal const int ImageWidth = 512;
	internal const int ImageHeight = 512;
	const long PayloadBytes = ImageWidth * ImageHeight * 4L;

	public static async Task<string> RunAsync(Page hostPage)
	{
		var mauiContext = await WaitForMauiContextAsync(hostPage);
		PayloadImageSourceService.ResetCounters();

		var control = await RunScenarioAsync("explicit native icon clear", mauiContext, clearNativeIconBeforeDisconnect: true);
		await ForceCollectionsAsync();

		var current = await RunScenarioAsync("current MAUI disconnect", mauiContext, clearNativeIconBeforeDisconnect: false);
		await ForceCollectionsAsync();

		var controlResult = Inspect(control);
		var currentResult = Inspect(current);

		var report = $"""
			Android ButtonHandler image retention repro
			Iterations: {Iterations}
			Per-button generated image: {ImageWidth}x{ImageHeight} ARGB_8888 = {FormatBytes(PayloadBytes)}
			Expected retained payload if every native icon slot survives: {FormatBytes(PayloadBytes * Iterations)}

			Control ({controlResult.Name})
			  Native MaterialButtons retained by JNI global refs: {controlResult.NativeButtonsRetained}/{Iterations}
			  Assigned native icon slots: {controlResult.AssignedIcons}/{Iterations}
			  Payload-sized native icon slots: {controlResult.PayloadSizedIcons}/{Iterations}
			  Retained native icon payload: {FormatBytes(controlResult.RetainedIconBytes)}
			  Managed Button wrappers alive: {controlResult.ManagedButtonsAlive}/{Iterations}
			  Managed ButtonHandler wrappers alive: {controlResult.ManagedHandlersAlive}/{Iterations}
			  Managed image sources alive: {controlResult.ManagedImageSourcesAlive}/{Iterations}

			Current MAUI ({currentResult.Name})
			  Native MaterialButtons retained by JNI global refs: {currentResult.NativeButtonsRetained}/{Iterations}
			  Assigned native icon slots: {currentResult.AssignedIcons}/{Iterations}
			  Payload-sized native icon slots: {currentResult.PayloadSizedIcons}/{Iterations}
			  Retained native icon payload: {FormatBytes(currentResult.RetainedIconBytes)}
			  Managed Button wrappers alive: {currentResult.ManagedButtonsAlive}/{Iterations}
			  Managed ButtonHandler wrappers alive: {currentResult.ManagedHandlersAlive}/{Iterations}
			  Managed image sources alive: {currentResult.ManagedImageSourcesAlive}/{Iterations}

			Image service results created: {PayloadImageSourceService.ResultsCreated}
			Image service results disposed by MAUI: {PayloadImageSourceService.ResultsDisposed}

			Verdict: {(currentResult.RetainedIconBytes > controlResult.RetainedIconBytes ? "PROVED" : "NOT PROVED")}
			""";

		control.Dispose();
		current.Dispose();

		return report;
	}

	static async Task<IMauiContext> WaitForMauiContextAsync(Page hostPage)
	{
		for (var i = 0; i < 50; i++)
		{
			if (hostPage.Handler?.MauiContext is IMauiContext mauiContext)
				return mauiContext;

			await Task.Delay(100);
		}

		throw new InvalidOperationException("The host page did not receive a MAUI context.");
	}

	static async Task<ScenarioSnapshot> RunScenarioAsync(string name, IMauiContext mauiContext, bool clearNativeIconBeforeDisconnect)
	{
		var samples = new List<IterationSnapshot>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			samples.Add(await CreateIterationAsync(i, mauiContext, clearNativeIconBeforeDisconnect));

			if ((i + 1) % 16 == 0)
				await ForceCollectionsAsync();
		}

		return new ScenarioSnapshot(name, samples);
	}

	static async Task<IterationSnapshot> CreateIterationAsync(int index, IMauiContext mauiContext, bool clearNativeIconBeforeDisconnect)
	{
		var imageSource = new PayloadImageSource(index);
		var button = new Button
		{
			Text = $"Image button {index}",
			ImageSource = imageSource
		};

		var handler = new ButtonHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(button);

		var platformButton = handler.PlatformView;
		await WaitForIconAsync(platformButton, index);

		var nativeRoot = new NativePeerRoot(platformButton);
		var handlerWeak = new WeakReference(handler);
		var buttonWeak = new WeakReference(button);
		var sourceWeak = new WeakReference(imageSource);

		if (clearNativeIconBeforeDisconnect)
			platformButton.Icon = null;

		((IElementHandler)handler).DisconnectHandler();
		button.ImageSource = null;

		handler = null!;
		button = null!;
		imageSource = null!;
		platformButton = null!;

		return new IterationSnapshot(nativeRoot, handlerWeak, buttonWeak, sourceWeak);
	}

	static async Task WaitForIconAsync(MaterialButton platformButton, int index)
	{
		for (var attempt = 0; attempt < 50; attempt++)
		{
			if (GetPayloadBitmapDrawable(platformButton.Icon) is not null)
				return;

			await Task.Delay(20);
		}

		throw new InvalidOperationException($"Icon drawable was not assigned for iteration {index}.");
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeButtons = 0;
		var assignedIcons = 0;
		var payloadSizedIcons = 0;
		var retainedIconBytes = 0L;
		var managedHandlersAlive = 0;
		var managedButtonsAlive = 0;
		var managedImageSourcesAlive = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.HandlerWeak.IsAlive)
				managedHandlersAlive++;
			if (sample.ButtonWeak.IsAlive)
				managedButtonsAlive++;
			if (sample.ImageSourceWeak.IsAlive)
				managedImageSourcesAlive++;

			var button = sample.NativeRoot.Get<MaterialButton>();
			if (button == null)
				continue;

			nativeButtons++;

			var icon = button.Icon;
			if (icon == null)
				continue;

			assignedIcons++;

			if (GetPayloadBitmapDrawable(icon) is { Bitmap: { } bitmap } && !bitmap.IsRecycled)
			{
				retainedIconBytes += bitmap.ByteCount;

				if (bitmap.ByteCount >= PayloadBytes)
					payloadSizedIcons++;
			}
		}

		return new ScenarioResult(
			scenario.Name,
			nativeButtons,
			assignedIcons,
			payloadSizedIcons,
			retainedIconBytes,
			managedHandlersAlive,
			managedButtonsAlive,
			managedImageSourcesAlive);
	}

	static BitmapDrawable? GetPayloadBitmapDrawable(Drawable? drawable)
	{
		if (drawable is BitmapDrawable bitmapDrawable)
			return bitmapDrawable;

		if (drawable is LayerDrawable layerDrawable)
		{
			for (var i = 0; i < layerDrawable.NumberOfLayers; i++)
			{
				if (layerDrawable.GetDrawable(i) is BitmapDrawable nestedBitmapDrawable)
					return nestedBitmapDrawable;
			}
		}

		return null;
	}

	static async Task ForceCollectionsAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			await Task.Delay(100);
		}
	}

	static string FormatBytes(long bytes)
	{
		const double MiB = 1024 * 1024;
		return $"{bytes / MiB:0.0} MiB";
	}

	sealed record ScenarioSnapshot(string Name, List<IterationSnapshot> Samples) : IDisposable
	{
		public void Dispose()
		{
			foreach (var sample in Samples)
				sample.NativeRoot.Dispose();
		}
	}

	sealed record IterationSnapshot(NativePeerRoot NativeRoot, WeakReference HandlerWeak, WeakReference ButtonWeak, WeakReference ImageSourceWeak);

	sealed record ScenarioResult(
		string Name,
		int NativeButtonsRetained,
		int AssignedIcons,
		int PayloadSizedIcons,
		long RetainedIconBytes,
		int ManagedHandlersAlive,
		int ManagedButtonsAlive,
		int ManagedImageSourcesAlive);

	sealed class NativePeerRoot : IDisposable
	{
		IntPtr _handle;

		public NativePeerRoot(Java.Lang.Object peer)
		{
			_handle = JNIEnv.NewGlobalRef(peer.Handle);
		}

		public T? Get<T>() where T : Java.Lang.Object
		{
			if (_handle == IntPtr.Zero)
				return null;

			return Java.Lang.Object.GetObject<T>(_handle, JniHandleOwnership.DoNotTransfer);
		}

		public void Dispose()
		{
			if (_handle == IntPtr.Zero)
				return;

			JNIEnv.DeleteGlobalRef(_handle);
			_handle = IntPtr.Zero;
		}
	}
}

public sealed class PayloadImageSource : Microsoft.Maui.Controls.ImageSource
{
	public PayloadImageSource(int index)
	{
		Index = index;
	}

	public int Index { get; }
}

public sealed class PayloadImageSourceService : ImageSourceService, IImageSourceService<PayloadImageSource>
{
	public static int ResultsCreated { get; private set; }

	public static int ResultsDisposed { get; private set; }

	public static void ResetCounters()
	{
		ResultsCreated = 0;
		ResultsDisposed = 0;
	}

	public override Task<IImageSourceServiceResult<Drawable>?> GetDrawableAsync(
		IImageSource imageSource,
		Context context,
		CancellationToken cancellationToken = default)
	{
		var source = (PayloadImageSource)imageSource;
		var bitmap = Bitmap.CreateBitmap(ReproSession.ImageWidth, ReproSession.ImageHeight, Bitmap.Config.Argb8888!);
		bitmap.EraseColor(new Color(
			255,
			(source.Index * 37) % 255,
			(source.Index * 73) % 255,
			(source.Index * 109) % 255));

		var drawable = new BitmapDrawable(context.Resources, bitmap);
		ResultsCreated++;

		IImageSourceServiceResult<Drawable> result = new ImageSourceServiceResult(drawable, () =>
		{
			ResultsDisposed++;
		});

		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(result);
	}
}
