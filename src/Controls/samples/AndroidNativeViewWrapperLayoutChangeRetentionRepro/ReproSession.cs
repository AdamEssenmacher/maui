#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Platform;
using AView = Android.Views.View;

namespace AndroidNativeViewWrapperLayoutChangeRetentionRepro;

public static class ReproSession
{
	const int Iterations = 2048;

	public static async Task<string> RunAsync(Page hostPage)
	{
		var mauiContext = await WaitForMauiContextAsync(hostPage);

		var control = await RunScenarioAsync("explicit native listener cleanup", mauiContext, useCurrentRenderer: false);
		await ForceCollectionsAsync();

		var current = await RunScenarioAsync("current MAUI NativeViewWrapperRenderer", mauiContext, useCurrentRenderer: true);
		await ForceCollectionsAsync();

		var controlResult = Inspect(control);
		var currentResult = Inspect(current);
		var proved = controlResult.NativeViewsRetained == Iterations &&
			controlResult.RenderersAlive == 0 &&
			controlResult.WrappersAlive == 0 &&
			currentResult.NativeViewsRetained == Iterations &&
			currentResult.RenderersAlive == Iterations &&
			currentResult.WrappersAlive == 0;

		var report = $"""
			Android NativeViewWrapper layout/focus listener retention repro
			Iterations: {Iterations}
			App-retained native View peers per scenario: {Iterations}

			Control ({controlResult.Name})
			  Native app-provided View peers retained by JNI global refs: {controlResult.NativeViewsRetained}/{Iterations}
			  Renderers alive: {controlResult.RenderersAlive}/{Iterations}
			  NativeViewWrappers alive: {controlResult.WrappersAlive}/{Iterations}

			Current MAUI ({currentResult.Name})
			  Native app-provided View peers retained by JNI global refs: {currentResult.NativeViewsRetained}/{Iterations}
			  Renderers alive: {currentResult.RenderersAlive}/{Iterations}
			  NativeViewWrappers alive: {currentResult.WrappersAlive}/{Iterations}
			  Extra disposed renderers retained by native listeners: {currentResult.RenderersAlive - controlResult.RenderersAlive}

			Verdict: {(proved ? "PROVED" : "NOT PROVED")}
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

	static async Task<ScenarioSnapshot> RunScenarioAsync(string name, IMauiContext mauiContext, bool useCurrentRenderer)
	{
		var samples = new List<IterationSnapshot>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			samples.Add(CreateIteration(mauiContext, useCurrentRenderer));

			if ((i + 1) % 128 == 0)
				await ForceCollectionsAsync();
		}

		return new ScenarioSnapshot(name, samples);
	}

	static IterationSnapshot CreateIteration(IMauiContext mauiContext, bool useCurrentRenderer)
	{
		var nativeView = new AView(Android.App.Application.Context);
		var baseContext = mauiContext.Context ?? throw new InvalidOperationException("The MAUI context has no Android context.");
		var wrapper = new NativeViewWrapper(nativeView);

		ViewRenderer<NativeViewWrapper, AView> renderer = useCurrentRenderer
			? new NativeViewWrapperRenderer(baseContext)
			: new ListenerCleanupNativeViewWrapperRenderer(baseContext);

		renderer.SetElement(wrapper);

		var nativeRoot = new NativePeerRoot(nativeView);
		var rendererWeak = new WeakReference(renderer);
		var wrapperWeak = new WeakReference(wrapper);

		renderer.Dispose();

		nativeView = null!;
		wrapper = null!;
		renderer = null!;

		return new IterationSnapshot(nativeRoot, rendererWeak, wrapperWeak);
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeViewsRetained = 0;
		var renderersAlive = 0;
		var wrappersAlive = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.NativeRoot.Get<AView>() is not null)
				nativeViewsRetained++;
			if (sample.RendererWeak.IsAlive)
				renderersAlive++;
			if (sample.WrapperWeak.IsAlive)
				wrappersAlive++;
		}

		return new ScenarioResult(
			scenario.Name,
			nativeViewsRetained,
			renderersAlive,
			wrappersAlive);
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

	sealed class ListenerCleanupNativeViewWrapperRenderer : ViewRenderer<NativeViewWrapper, AView>
	{
		public ListenerCleanupNativeViewWrapperRenderer(Context context)
			: base(context)
		{
		}

		protected override bool ManageNativeControlLifetime => false;

		protected override AView CreateNativeControl() => new(Context);

		protected override void OnElementChanged(ElementChangedEventArgs<NativeViewWrapper> e)
		{
			base.OnElementChanged(e);

			if (e.OldElement == null)
				SetNativeControl(Element.NativeView);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && Control is not null)
				Control.OnFocusChangeListener = null;

			base.Dispose(disposing);
		}
	}

	sealed record ScenarioSnapshot(string Name, List<IterationSnapshot> Samples) : IDisposable
	{
		public void Dispose()
		{
			foreach (var sample in Samples)
				sample.NativeRoot.Dispose();
		}
	}

	sealed record IterationSnapshot(
		NativePeerRoot NativeRoot,
		WeakReference RendererWeak,
		WeakReference WrapperWeak);

	sealed record ScenarioResult(
		string Name,
		int NativeViewsRetained,
		int RenderersAlive,
		int WrappersAlive);

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
