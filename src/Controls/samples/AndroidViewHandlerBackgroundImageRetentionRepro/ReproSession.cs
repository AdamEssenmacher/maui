#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;

namespace AndroidViewHandlerBackgroundImageRetentionRepro;

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

		var control = await RunScenarioAsync("explicit native background clear", mauiContext, clearNativeBackgroundBeforeDisconnect: true);
		await ForceCollectionsAsync();

		var current = await RunScenarioAsync("current MAUI disconnect", mauiContext, clearNativeBackgroundBeforeDisconnect: false);
		await ForceCollectionsAsync();

		var controlResult = Inspect(control);
		var currentResult = Inspect(current);

		var report = $"""
			Android ViewHandler/PageHandler background image retention repro
			Iterations: {Iterations}
			Per-view generated background: {ImageWidth}x{ImageHeight} ARGB_8888 = {FormatBytes(PayloadBytes)}
			Expected retained payload if every native background slot survives: {FormatBytes(PayloadBytes * Iterations)}

			Control ({controlResult.Name})
			  Native views retained by JNI global refs: {controlResult.NativeViewsRetained}/{Iterations}
			  Assigned native background slots: {controlResult.AssignedBackgrounds}/{Iterations}
			  Payload-sized native background slots: {controlResult.PayloadSizedBackgrounds}/{Iterations}
			  Retained native background payload: {FormatBytes(controlResult.RetainedBackgroundBytes)}
			  Managed Page wrappers alive: {controlResult.ManagedPagesAlive}/{Iterations}
			  Managed PageHandler wrappers alive: {controlResult.ManagedHandlersAlive}/{Iterations}
			  Managed image sources alive: {controlResult.ManagedImageSourcesAlive}/{Iterations}

			Current MAUI ({currentResult.Name})
			  Native views retained by JNI global refs: {currentResult.NativeViewsRetained}/{Iterations}
			  Assigned native background slots: {currentResult.AssignedBackgrounds}/{Iterations}
			  Payload-sized native background slots: {currentResult.PayloadSizedBackgrounds}/{Iterations}
			  Retained native background payload: {FormatBytes(currentResult.RetainedBackgroundBytes)}
			  Managed Page wrappers alive: {currentResult.ManagedPagesAlive}/{Iterations}
			  Managed PageHandler wrappers alive: {currentResult.ManagedHandlersAlive}/{Iterations}
			  Managed image sources alive: {currentResult.ManagedImageSourcesAlive}/{Iterations}

			Image service results created: {PayloadImageSourceService.ResultsCreated}
			Image service results disposed by MAUI: {PayloadImageSourceService.ResultsDisposed}

			Verdict: {(currentResult.RetainedBackgroundBytes > controlResult.RetainedBackgroundBytes ? "PROVED" : "NOT PROVED")}
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

	static async Task<ScenarioSnapshot> RunScenarioAsync(string name, IMauiContext mauiContext, bool clearNativeBackgroundBeforeDisconnect)
	{
		var samples = new List<IterationSnapshot>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			samples.Add(await CreateIterationAsync(i, mauiContext, clearNativeBackgroundBeforeDisconnect));

			if ((i + 1) % 16 == 0)
				await ForceCollectionsAsync();
		}

		return new ScenarioSnapshot(name, samples);
	}

	static async Task<IterationSnapshot> CreateIterationAsync(int index, IMauiContext mauiContext, bool clearNativeBackgroundBeforeDisconnect)
	{
		var imageSource = new PayloadImageSource(index);
		var page = new ContentPage
		{
			Title = $"Background page {index}",
			BackgroundImageSource = imageSource
		};

		var handler = new PageHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(page);

		var platformView = (AView)handler.PlatformView;
		await WaitForBackgroundAsync(platformView, index);

		var nativeRoot = new NativePeerRoot(platformView);
		var handlerWeak = new WeakReference(handler);
		var pageWeak = new WeakReference(page);
		var sourceWeak = new WeakReference(imageSource);

		if (clearNativeBackgroundBeforeDisconnect)
			platformView.Background = null;

		((IElementHandler)handler).DisconnectHandler();
		page.BackgroundImageSource = null;

		handler = null!;
		page = null!;
		imageSource = null!;
		platformView = null!;

		return new IterationSnapshot(nativeRoot, handlerWeak, pageWeak, sourceWeak);
	}

	static async Task WaitForBackgroundAsync(AView platformView, int index)
	{
		for (var attempt = 0; attempt < 50; attempt++)
		{
			if (platformView.Background is BitmapDrawable)
				return;

			await Task.Delay(20);
		}

		throw new InvalidOperationException($"Background drawable was not assigned for iteration {index}.");
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeViews = 0;
		var assignedBackgrounds = 0;
		var payloadSizedBackgrounds = 0;
		var retainedBackgroundBytes = 0L;
		var managedHandlersAlive = 0;
		var managedPagesAlive = 0;
		var managedImageSourcesAlive = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.HandlerWeak.IsAlive)
				managedHandlersAlive++;
			if (sample.PageWeak.IsAlive)
				managedPagesAlive++;
			if (sample.ImageSourceWeak.IsAlive)
				managedImageSourcesAlive++;

			var view = sample.NativeRoot.Get<AView>();
			if (view == null)
				continue;

			nativeViews++;

			var background = view.Background;
			if (background == null)
				continue;

			assignedBackgrounds++;

			if (background is BitmapDrawable { Bitmap: { } bitmap } && !bitmap.IsRecycled)
			{
				retainedBackgroundBytes += bitmap.ByteCount;

				if (bitmap.ByteCount >= PayloadBytes)
					payloadSizedBackgrounds++;
			}
		}

		return new ScenarioResult(
			scenario.Name,
			nativeViews,
			assignedBackgrounds,
			payloadSizedBackgrounds,
			retainedBackgroundBytes,
			managedHandlersAlive,
			managedPagesAlive,
			managedImageSourcesAlive);
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

	sealed record IterationSnapshot(NativePeerRoot NativeRoot, WeakReference HandlerWeak, WeakReference PageWeak, WeakReference ImageSourceWeak);

	sealed record ScenarioResult(
		string Name,
		int NativeViewsRetained,
		int AssignedBackgrounds,
		int PayloadSizedBackgrounds,
		long RetainedBackgroundBytes,
		int ManagedHandlersAlive,
		int ManagedPagesAlive,
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
