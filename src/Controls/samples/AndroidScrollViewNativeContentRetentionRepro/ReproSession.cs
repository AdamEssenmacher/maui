#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Android.Runtime;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;
using MauiScrollViewControl = Microsoft.Maui.Controls.ScrollView;

namespace AndroidScrollViewNativeContentRetentionRepro;

public static class ReproSession
{
	const int Iterations = 96;
	const int PayloadChars = 128 * 1024;
	const long PayloadBytes = PayloadChars * 2L;
	const string InsetPanelTag = "MAUIContentInsetPanel";

	static readonly FieldInfo ContentField =
		typeof(MauiScrollView).GetField("_content", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(MauiScrollView).FullName, "_content");

	public static async Task<string> RunAsync(Page hostPage)
	{
		var mauiContext = await WaitForMauiContextAsync(hostPage);

		var control = await RunScenarioAsync("explicit native content clear", mauiContext, clearNativeContentBeforeDisconnect: true);
		await ForceCollectionsAsync();

		var current = await RunScenarioAsync("current MAUI disconnect", mauiContext, clearNativeContentBeforeDisconnect: false);
		await ForceCollectionsAsync();

		var controlResult = Inspect(control);
		var currentResult = Inspect(current);

		var report = $"""
			Android ScrollView native content retention repro
			Iterations: {Iterations}
			Per-scroll copied native label payload: {PayloadChars:n0} UTF-16 chars ~= {FormatBytes(PayloadBytes)}
			Expected retained native label text if every current content subtree survives: {FormatBytes(PayloadBytes * Iterations)}

			Non-candidate fields cleared in both runs:
			  MauiScrollView.CrossPlatformLayout
			  Inset ContentViewGroup.CrossPlatformLayout
			  Virtual ScrollView.Content after handler disconnect

			Control ({controlResult.Name})
			  Native MauiScrollView peers retained by JNI global refs: {controlResult.NativeScrollViewsRetained}/{Iterations}
			  Native scroll peers with child views: {controlResult.ScrollViewsWithChildren}/{Iterations}
			  Native scroll peers with private _content assigned: {controlResult.ScrollViewsWithContentField}/{Iterations}
			  Inset content panels still attached: {controlResult.AttachedInsetPanels}/{Iterations}
			  Native TextView payload children retained: {controlResult.PayloadTextViews}/{Iterations}
			  Retained native label text payload: {FormatBytes(controlResult.RetainedTextBytes)}
			  Managed ScrollView wrappers alive: {controlResult.ManagedScrollViewsAlive}/{Iterations}
			  Managed ScrollViewHandler wrappers alive: {controlResult.ManagedHandlersAlive}/{Iterations}
			  Managed Label children alive: {controlResult.ManagedLabelsAlive}/{Iterations}

			Current MAUI ({currentResult.Name})
			  Native MauiScrollView peers retained by JNI global refs: {currentResult.NativeScrollViewsRetained}/{Iterations}
			  Native scroll peers with child views: {currentResult.ScrollViewsWithChildren}/{Iterations}
			  Native scroll peers with private _content assigned: {currentResult.ScrollViewsWithContentField}/{Iterations}
			  Inset content panels still attached: {currentResult.AttachedInsetPanels}/{Iterations}
			  Native TextView payload children retained: {currentResult.PayloadTextViews}/{Iterations}
			  Retained native label text payload: {FormatBytes(currentResult.RetainedTextBytes)}
			  Managed ScrollView wrappers alive: {currentResult.ManagedScrollViewsAlive}/{Iterations}
			  Managed ScrollViewHandler wrappers alive: {currentResult.ManagedHandlersAlive}/{Iterations}
			  Managed Label children alive: {currentResult.ManagedLabelsAlive}/{Iterations}

			Verdict: {(currentResult.RetainedTextBytes > controlResult.RetainedTextBytes ? "PROVED" : "NOT PROVED")}
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

	static async Task<ScenarioSnapshot> RunScenarioAsync(string name, IMauiContext mauiContext, bool clearNativeContentBeforeDisconnect)
	{
		var samples = new List<IterationSnapshot>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			samples.Add(CreateIteration(i, mauiContext, clearNativeContentBeforeDisconnect));

			if ((i + 1) % 16 == 0)
				await ForceCollectionsAsync();
		}

		return new ScenarioSnapshot(name, samples);
	}

	static IterationSnapshot CreateIteration(int index, IMauiContext mauiContext, bool clearNativeContentBeforeDisconnect)
	{
		var label = new Label
		{
			Text = CreatePayloadText(index),
			LineBreakMode = LineBreakMode.NoWrap
		};

		var scrollView = new MauiScrollViewControl
		{
			Orientation = ScrollOrientation.Vertical,
			Padding = new Thickness(8),
			Content = label
		};

		var handler = new ScrollViewHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(scrollView);
		ScrollViewHandler.MapContent(handler, scrollView);

		var platformView = handler.PlatformView;
		_ = FindInsetPanel(platformView) ?? throw new InvalidOperationException($"Inset panel was not created for iteration {index}.");

		var nativeRoot = new NativePeerRoot(platformView);
		var handlerWeak = new WeakReference(handler);
		var scrollWeak = new WeakReference(scrollView);
		var labelWeak = new WeakReference(label);

		if (clearNativeContentBeforeDisconnect)
			ClearNativeContent(platformView);

		((IElementHandler)handler).DisconnectHandler();

		// Neutralize already-cataloged owner-field retention so the delta is native content cleanup.
		ClearCrossPlatformOwnerFields(platformView);
		if (!clearNativeContentBeforeDisconnect)
		{
			var panel = FindInsetPanel(platformView);
			if (panel is not null)
				panel.CrossPlatformLayout = null;
		}

		scrollView.Content = null;

		handler = null!;
		scrollView = null!;
		label = null!;
		platformView = null!;

		return new IterationSnapshot(nativeRoot, handlerWeak, scrollWeak, labelWeak);
	}

	static string CreatePayloadText(int index)
	{
		var prefix = $"scroll-audit-row-{index:0000}:";
		var builder = new StringBuilder(PayloadChars);

		while (builder.Length < PayloadChars)
			builder.Append(prefix);

		return builder.ToString(0, PayloadChars);
	}

	static ContentViewGroup? FindInsetPanel(MauiScrollView scrollView)
	{
		return scrollView.FindViewWithTag(InsetPanelTag) as ContentViewGroup;
	}

	static void ClearNativeContent(MauiScrollView scrollView)
	{
		var panel = FindInsetPanel(scrollView);
		if (panel is not null)
		{
			panel.CrossPlatformLayout = null;
			panel.RemoveAllViews();
			panel.RemoveFromParent();
		}

		scrollView.RemoveAllViews();
		scrollView.SetContent(null!);
	}

	static void ClearCrossPlatformOwnerFields(MauiScrollView scrollView)
	{
		scrollView.CrossPlatformLayout = null;

		var panel = FindInsetPanel(scrollView);
		if (panel is not null)
			panel.CrossPlatformLayout = null;
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeScrollViews = 0;
		var scrollViewsWithChildren = 0;
		var scrollViewsWithContentField = 0;
		var attachedInsetPanels = 0;
		var payloadTextViews = 0;
		var retainedTextBytes = 0L;
		var managedHandlersAlive = 0;
		var managedScrollViewsAlive = 0;
		var managedLabelsAlive = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.HandlerWeak.IsAlive)
				managedHandlersAlive++;
			if (sample.ScrollViewWeak.IsAlive)
				managedScrollViewsAlive++;
			if (sample.LabelWeak.IsAlive)
				managedLabelsAlive++;

			var scrollView = sample.NativeRoot.Get<MauiScrollView>();
			if (scrollView == null)
				continue;

			nativeScrollViews++;

			if (scrollView.ChildCount > 0)
				scrollViewsWithChildren++;

			if (ContentField.GetValue(scrollView) is not null)
				scrollViewsWithContentField++;

			var panel = FindInsetPanel(scrollView);
			if (panel is null)
				continue;

			attachedInsetPanels++;

			var textView = FindFirstTextView(panel);
			if (textView?.Text is not string text)
				continue;

			if (text.Length >= PayloadChars)
			{
				payloadTextViews++;
				retainedTextBytes += text.Length * 2L;
			}
		}

		return new ScenarioResult(
			scenario.Name,
			nativeScrollViews,
			scrollViewsWithChildren,
			scrollViewsWithContentField,
			attachedInsetPanels,
			payloadTextViews,
			retainedTextBytes,
			managedHandlersAlive,
			managedScrollViewsAlive,
			managedLabelsAlive);
	}

	static TextView? FindFirstTextView(AView view)
	{
		if (view is TextView textView)
			return textView;

		if (view is AViewGroup group)
		{
			for (var i = 0; i < group.ChildCount; i++)
			{
				var child = group.GetChildAt(i);
				if (child is null)
					continue;

				var result = FindFirstTextView(child);
				if (result is not null)
					return result;
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

	sealed record IterationSnapshot(NativePeerRoot NativeRoot, WeakReference HandlerWeak, WeakReference ScrollViewWeak, WeakReference LabelWeak);

	sealed record ScenarioResult(
		string Name,
		int NativeScrollViewsRetained,
		int ScrollViewsWithChildren,
		int ScrollViewsWithContentField,
		int AttachedInsetPanels,
		int PayloadTextViews,
		long RetainedTextBytes,
		int ManagedHandlersAlive,
		int ManagedScrollViewsAlive,
		int ManagedLabelsAlive);

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
