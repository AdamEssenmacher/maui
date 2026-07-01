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
using MauiSwipeViewControl = Microsoft.Maui.Controls.SwipeView;

namespace AndroidSwipeViewNativeContentRetentionRepro;

public static class ReproSession
{
	const int Iterations = 96;
	const int PayloadChars = 128 * 1024;
	const long PayloadBytes = PayloadChars * 2L;

	static readonly FieldInfo ContentViewField =
		typeof(MauiSwipeView).GetField("_contentView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(MauiSwipeView).FullName, "_contentView");

	static readonly FieldInfo ElementField =
		typeof(MauiSwipeView).GetField("<Element>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(MauiSwipeView).FullName, "<Element>k__BackingField");

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
			Android SwipeView native content retention repro
			Iterations: {Iterations}
			Per-swipe copied native label payload: {PayloadChars:n0} UTF-16 chars ~= {FormatBytes(PayloadBytes)}
			Expected retained native label text if every current content subtree survives: {FormatBytes(PayloadBytes * Iterations)}

			Non-candidate fields cleared in both runs:
			  MauiSwipeView.CrossPlatformLayout
			  MauiSwipeView.Element
			  Virtual SwipeView.Content after handler disconnect

			Control ({controlResult.Name})
			  Native MauiSwipeView peers retained by JNI global refs: {controlResult.NativeSwipeViewsRetained}/{Iterations}
			  Native swipe peers with private _contentView assigned: {controlResult.SwipeViewsWithContentViewField}/{Iterations}
			  Native swipe peers with _contentView attached: {controlResult.SwipeViewsWithAttachedContentView}/{Iterations}
			  Native TextView payload children retained: {controlResult.PayloadTextViews}/{Iterations}
			  Retained native label text payload: {FormatBytes(controlResult.RetainedTextBytes)}
			  Managed SwipeView wrappers alive: {controlResult.ManagedSwipeViewsAlive}/{Iterations}
			  Managed SwipeViewHandler wrappers alive: {controlResult.ManagedHandlersAlive}/{Iterations}
			  Managed Label children alive: {controlResult.ManagedLabelsAlive}/{Iterations}

			Current MAUI ({currentResult.Name})
			  Native MauiSwipeView peers retained by JNI global refs: {currentResult.NativeSwipeViewsRetained}/{Iterations}
			  Native swipe peers with private _contentView assigned: {currentResult.SwipeViewsWithContentViewField}/{Iterations}
			  Native swipe peers with _contentView attached: {currentResult.SwipeViewsWithAttachedContentView}/{Iterations}
			  Native TextView payload children retained: {currentResult.PayloadTextViews}/{Iterations}
			  Retained native label text payload: {FormatBytes(currentResult.RetainedTextBytes)}
			  Managed SwipeView wrappers alive: {currentResult.ManagedSwipeViewsAlive}/{Iterations}
			  Managed SwipeViewHandler wrappers alive: {currentResult.ManagedHandlersAlive}/{Iterations}
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

		var swipeView = new MauiSwipeViewControl
		{
			Content = label
		};

		var handler = new SwipeViewHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(swipeView);

		var platformView = handler.PlatformView;
		_ = GetContentView(platformView) ?? throw new InvalidOperationException($"Content view was not created for iteration {index}.");

		var nativeRoot = new NativePeerRoot(platformView);
		var handlerWeak = new WeakReference(handler);
		var swipeWeak = new WeakReference(swipeView);
		var labelWeak = new WeakReference(label);

		if (clearNativeContentBeforeDisconnect)
			ClearNativeContent(platformView);

		((IElementHandler)handler).DisconnectHandler();

		// Neutralize already-cataloged owner-field retention so the delta is native content cleanup.
		ClearCrossPlatformOwnerFields(platformView);

		swipeView.Content = null;

		handler = null!;
		swipeView = null!;
		label = null!;
		platformView = null!;

		return new IterationSnapshot(nativeRoot, handlerWeak, swipeWeak, labelWeak);
	}

	static string CreatePayloadText(int index)
	{
		var prefix = $"swipe-audit-row-{index:0000}:";
		var builder = new StringBuilder(PayloadChars);

		while (builder.Length < PayloadChars)
			builder.Append(prefix);

		return builder.ToString(0, PayloadChars);
	}

	static AView? GetContentView(MauiSwipeView swipeView)
	{
		return ContentViewField.GetValue(swipeView) as AView;
	}

	static void ClearNativeContent(MauiSwipeView swipeView)
	{
		var contentView = GetContentView(swipeView);
		if (contentView is not null)
		{
			contentView.RemoveFromParent();
			if (contentView.Handle != IntPtr.Zero)
				contentView.Dispose();
		}

		ContentViewField.SetValue(swipeView, null);
	}

	static void ClearCrossPlatformOwnerFields(MauiSwipeView swipeView)
	{
		swipeView.CrossPlatformLayout = null;
		ElementField.SetValue(swipeView, null);
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeSwipeViews = 0;
		var swipeViewsWithContentViewField = 0;
		var swipeViewsWithAttachedContentView = 0;
		var payloadTextViews = 0;
		var retainedTextBytes = 0L;
		var managedHandlersAlive = 0;
		var managedSwipeViewsAlive = 0;
		var managedLabelsAlive = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.HandlerWeak.IsAlive)
				managedHandlersAlive++;
			if (sample.SwipeViewWeak.IsAlive)
				managedSwipeViewsAlive++;
			if (sample.LabelWeak.IsAlive)
				managedLabelsAlive++;

			var swipeView = sample.NativeRoot.Get<MauiSwipeView>();
			if (swipeView == null)
				continue;

			nativeSwipeViews++;

			var contentView = GetContentView(swipeView);
			if (contentView is null)
				continue;

			swipeViewsWithContentViewField++;

			if (contentView.Parent is not null)
				swipeViewsWithAttachedContentView++;

			var textView = FindFirstTextView(contentView);
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
			nativeSwipeViews,
			swipeViewsWithContentViewField,
			swipeViewsWithAttachedContentView,
			payloadTextViews,
			retainedTextBytes,
			managedHandlersAlive,
			managedSwipeViewsAlive,
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

	sealed record IterationSnapshot(NativePeerRoot NativeRoot, WeakReference HandlerWeak, WeakReference SwipeViewWeak, WeakReference LabelWeak);

	sealed record ScenarioResult(
		string Name,
		int NativeSwipeViewsRetained,
		int SwipeViewsWithContentViewField,
		int SwipeViewsWithAttachedContentView,
		int PayloadTextViews,
		long RetainedTextBytes,
		int ManagedHandlersAlive,
		int ManagedSwipeViewsAlive,
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
