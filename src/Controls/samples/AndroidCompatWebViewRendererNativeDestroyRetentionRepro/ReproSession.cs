#nullable enable

#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Android.Webkit;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using AWebView = Android.Webkit.WebView;
using ControlsWebView = Microsoft.Maui.Controls.WebView;

namespace AndroidCompatWebViewRendererNativeDestroyRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 8;
	const int HtmlPayloadChars = 256 * 1024;
	const int HtmlPayloadBytes = HtmlPayloadChars * sizeof(char);

	static readonly List<object> RetainedNativePeerRoots = new();

	static readonly IntPtr WebViewClass = JNIEnv.FindClass("android/webkit/WebView");
	static readonly IntPtr SetWebViewClientMethod = JNIEnv.GetMethodID(WebViewClass, "setWebViewClient", "(Landroid/webkit/WebViewClient;)V");
	static readonly IntPtr SetWebChromeClientMethod = JNIEnv.GetMethodID(WebViewClass, "setWebChromeClient", "(Landroid/webkit/WebChromeClient;)V");
	static readonly IntPtr StopLoadingMethod = JNIEnv.GetMethodID(WebViewClass, "stopLoading", "()V");
	static readonly IntPtr DestroyMethod = JNIEnv.GetMethodID(WebViewClass, "destroy", "()V");
	static readonly IntPtr ViewGroupClass = JNIEnv.FindClass("android/view/ViewGroup");
	static readonly IntPtr RemoveAllViewsMethod = JNIEnv.GetMethodID(ViewGroupClass, "removeAllViews", "()V");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose WebViewRenderer, then explicitly clear/destroy retained native WebView peers",
			context,
			explicitNativeDestroy: true);

		var current = await RunScenarioAsync(
			"current: WebViewRenderer.Dispose() leaves retained native WebView peers not destroyed",
			context,
			explicitNativeDestroy: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(
			Cycles,
			HtmlPayloadChars,
			HtmlPayloadBytes,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool explicitNativeDestroy)
	{
		var nativePeers = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);
		var androidContext = context.Context ?? throw new InvalidOperationException("Android context is not available.");

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(androidContext, i, nativePeers, tracked, explicitNativeDestroy);

			if (i % 3 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(nativePeers);
		await Task.Delay(500);
		ForceFullGc();
		GC.KeepAlive(nativePeers);

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		Context androidContext,
		int cycle,
		List<NativePeerRoot> nativePeers,
		List<TrackedCycle> tracked,
		bool explicitNativeDestroy)
	{
		var html = CreateHtmlPayload(cycle);
		var payload = new PayloadHolder(cycle);
		var webView = new ControlsWebView
		{
			BindingContext = payload,
			Source = new HtmlWebViewSource
			{
				BaseUrl = WebViewRenderer.AssetBaseUrl,
				Html = html
			},
			WidthRequest = 320,
			HeightRequest = 240
		};

		var renderer = new TrackingWebViewRenderer(androidContext);
		((IVisualElementRenderer)renderer).SetElement(webView);

		var nativeWebView = renderer.TrackingWebView
			?? throw new InvalidOperationException("TrackingWebViewRenderer did not create a native WebView.");
		nativeWebView.RequestedHtmlChars = html.Length;

		var nativePeer = NativePeerRoot.Create(nativeWebView, html.Length);
		var destroyCallsBeforeDispose = nativeWebView.DestroyCallCount;

		webView.BindingContext = null;
		webView.Source = null;

		renderer.Dispose();

		var frameworkDestroyCalls = nativeWebView.DestroyCallCount - destroyCallsBeforeDispose;

		if (explicitNativeDestroy)
		{
			nativePeer.ClearAndDestroy();
		}

		nativePeers.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(
			cycle,
			nativePeer,
			renderer,
			webView,
			payload,
			renderer.WebViewClientWeakReference,
			renderer.WebChromeClientWeakReference,
			frameworkDestroyCalls));
	}

	static string CreateHtmlPayload(int cycle)
	{
		var prefix = $"<html><body><h1>Order review {cycle:D4}</h1><p>";
		var suffix = "</p></body></html>";
		var fillLength = HtmlPayloadChars - prefix.Length - suffix.Length;
		if (fillLength < 0)
			throw new InvalidOperationException("HTML payload prefix is longer than the configured payload size.");

		return prefix + new string((char)('A' + (cycle % 26)), fillLength) + suffix;
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

	internal sealed class TrackingWebViewRenderer : WebViewRenderer
	{
		public TrackingWebViewRenderer(Context context)
			: base(context)
		{
		}

		public TrackingWebView? TrackingWebView { get; private set; }
		public WeakReference<WebViewClient>? WebViewClientWeakReference { get; private set; }
		public WeakReference<FormsWebChromeClient>? WebChromeClientWeakReference { get; private set; }

		protected override AWebView CreateNativeControl()
		{
			var webView = new TrackingWebView(Context!);
			webView.Settings.SetSupportMultipleWindows(true);
			TrackingWebView = webView;
			return webView;
		}

		protected override WebViewClient GetWebViewClient()
		{
			var client = base.GetWebViewClient();
			WebViewClientWeakReference = new WeakReference<WebViewClient>(client);
			return client;
		}

		protected override FormsWebChromeClient GetFormsWebChromeClient()
		{
			var client = base.GetFormsWebChromeClient();
			WebChromeClientWeakReference = new WeakReference<FormsWebChromeClient>(client);
			return client;
		}
	}

	internal sealed class TrackingWebView : AWebView
	{
		public TrackingWebView(Context context)
			: base(context)
		{
		}

		public TrackingWebView(IntPtr javaReference, JniHandleOwnership transfer)
			: base(javaReference, transfer)
		{
		}

		public int DestroyCallCount { get; private set; }
		public int RequestedHtmlChars { get; set; }

		public override void Destroy()
		{
			DestroyCallCount++;
			base.Destroy();
		}
	}

	internal sealed class PayloadHolder
	{
		public PayloadHolder(int cycle)
		{
			Cycle = cycle;
			Data = new byte[1024 * 1024];
			Data[0] = (byte)(cycle % 251);
		}

		public int Cycle { get; }
		public byte[] Data { get; }
	}

	internal sealed record NativePeerRoot(IntPtr GlobalRef, int RequestedHtmlChars)
	{
		public bool ExplicitDestroyInvoked { get; private set; }

		public static NativePeerRoot Create(TrackingWebView webView, int requestedHtmlChars)
		{
			if (webView.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native WebView handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(webView.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native WebView.");

			return new NativePeerRoot(globalRef, requestedHtmlChars);
		}

		public void ClearAndDestroy()
		{
			if (GlobalRef == IntPtr.Zero)
				return;

			JNIEnv.CallVoidMethod(GlobalRef, SetWebViewClientMethod, new JValue(IntPtr.Zero));
			JNIEnv.CallVoidMethod(GlobalRef, SetWebChromeClientMethod, new JValue(IntPtr.Zero));
			JNIEnv.CallVoidMethod(GlobalRef, StopLoadingMethod);
			JNIEnv.CallVoidMethod(GlobalRef, RemoveAllViewsMethod);
			JNIEnv.CallVoidMethod(GlobalRef, DestroyMethod);
			ExplicitDestroyInvoked = true;
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativeWebView,
		WeakReference<TrackingWebViewRenderer> ManagedRenderer,
		WeakReference<ControlsWebView> VirtualView,
		WeakReference<PayloadHolder> Payload,
		WeakReference<WebViewClient>? WebViewClient,
		WeakReference<FormsWebChromeClient>? WebChromeClient,
		int FrameworkDestroyCalls)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeWebView,
			TrackingWebViewRenderer renderer,
			ControlsWebView webView,
			PayloadHolder payload,
			WeakReference<WebViewClient>? webViewClient,
			WeakReference<FormsWebChromeClient>? webChromeClient,
			int frameworkDestroyCalls)
		{
			return new TrackedCycle(
				cycle,
				nativeWebView,
				new WeakReference<TrackingWebViewRenderer>(renderer),
				new WeakReference<ControlsWebView>(webView),
				new WeakReference<PayloadHolder>(payload),
				webViewClient,
				webChromeClient,
				frameworkDestroyCalls);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeWebViews,
		int FrameworkDestroyCalls,
		int ExplicitDestroyInvocations,
		int NotDestroyedByFramework,
		long HtmlPayloadBytesLoadedIntoRetainedPeers,
		long HtmlPayloadBytesLeftInNotDestroyedPeers,
		int AliveManagedRenderers,
		int AliveVirtualViews,
		int AlivePayloads,
		int AliveWebViewClients,
		int AliveWebChromeClients)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeWebViews = 0;
			var frameworkDestroyCalls = 0;
			var explicitDestroyInvocations = 0;
			long htmlPayloadBytesLoadedIntoRetainedPeers = 0;
			long htmlPayloadBytesLeftInNotDestroyedPeers = 0;
			var aliveManagedRenderers = 0;
			var aliveVirtualViews = 0;
			var alivePayloads = 0;
			var aliveWebViewClients = 0;
			var aliveWebChromeClients = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeWebView.GlobalRef != IntPtr.Zero)
				{
					aliveNativeWebViews++;
					htmlPayloadBytesLoadedIntoRetainedPeers += (long)cycle.NativeWebView.RequestedHtmlChars * sizeof(char);

					if (!cycle.NativeWebView.ExplicitDestroyInvoked)
						htmlPayloadBytesLeftInNotDestroyedPeers += (long)cycle.NativeWebView.RequestedHtmlChars * sizeof(char);
				}

				frameworkDestroyCalls += cycle.FrameworkDestroyCalls;

				if (cycle.NativeWebView.ExplicitDestroyInvoked)
					explicitDestroyInvocations++;

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Payload.TryGetTarget(out _))
					alivePayloads++;

				if (cycle.WebViewClient?.TryGetTarget(out _) == true)
					aliveWebViewClients++;

				if (cycle.WebChromeClient?.TryGetTarget(out _) == true)
					aliveWebChromeClients++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeWebViews,
				frameworkDestroyCalls,
				explicitDestroyInvocations,
				tracked.Count - frameworkDestroyCalls,
				htmlPayloadBytesLoadedIntoRetainedPeers,
				htmlPayloadBytesLeftInNotDestroyedPeers,
				aliveManagedRenderers,
				aliveVirtualViews,
				alivePayloads,
				aliveWebViewClients,
				aliveWebChromeClients);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int HtmlPayloadChars,
	int HtmlPayloadBytes,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeWebViews == Cycles &&
		Current.AliveNativeWebViews == Cycles &&
		Control.FrameworkDestroyCalls == 0 &&
		Current.FrameworkDestroyCalls == 0 &&
		Control.ExplicitDestroyInvocations == Cycles &&
		Current.ExplicitDestroyInvocations == 0 &&
		Current.NotDestroyedByFramework == Cycles &&
		Current.HtmlPayloadBytesLeftInNotDestroyedPeers >= 3L * 1024 * 1024 &&
		Control.AlivePayloads == 0 &&
		Current.AlivePayloads == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidCompatWebViewRendererNativeDestroyRetentionRepro",
			$"Cycles per scenario: {Cycles}",
			$"HTML payload chars requested per WebView: {HtmlPayloadChars:N0}",
			$"HTML payload bytes requested per WebView: {HtmlPayloadBytes:N0}",
			"Source path exercised: obsolete Android WebViewRenderer.Dispose()",
			"Control cleanup clears native clients, stops loading, removes child views, and invokes native WebView.destroy() through the retained JNI peer.",
			"Current cleanup is the framework WebViewRenderer.Dispose() path only.",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native WebView HTML payload requested: {FormatBytes(Control.HtmlPayloadBytesLoadedIntoRetainedPeers)}",
			$"Current retained not-destroyed native WebView HTML payload requested: {FormatBytes(Current.HtmlPayloadBytesLeftInNotDestroyedPeers)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native WebViews: {result.AliveNativeWebViews}/{result.TrackedCycles}",
			$"  framework Destroy() calls observed during renderer disposal: {result.FrameworkDestroyCalls}/{result.TrackedCycles}",
			$"  explicit control Destroy() invocations: {result.ExplicitDestroyInvocations}/{result.TrackedCycles}",
			$"  native WebViews not destroyed by framework disposal: {result.NotDestroyedByFramework}/{result.TrackedCycles}",
			$"  HTML payload bytes requested by retained native peers: {result.HtmlPayloadBytesLoadedIntoRetainedPeers:N0}",
			$"  HTML payload bytes left in not-destroyed peers: {result.HtmlPayloadBytesLeftInNotDestroyedPeers:N0}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive MAUI WebViews after full GC: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive BindingContext payloads after full GC: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive FormsWebViewClient wrappers after full GC: {result.AliveWebViewClients}/{result.TrackedCycles}",
			$"  alive FormsWebChromeClient wrappers after full GC: {result.AliveWebChromeClients}/{result.TrackedCycles}");
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
