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

namespace AndroidRefreshViewNativeContentRetentionRepro;

public static class ReproSession
{
	const int Iterations = 96;
	const int PayloadChars = 128 * 1024;
	const long PayloadBytes = PayloadChars * 2L;

	static readonly FieldInfo ContentViewField =
		typeof(MauiSwipeRefreshLayout).GetField("_contentView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(MauiSwipeRefreshLayout).FullName, "_contentView");

	public static async Task<string> RunAsync(Page hostPage)
	{
		var mauiContext = await WaitForMauiContextAsync(hostPage);

		var control = await RunScenarioAsync("explicit _contentView clear after disconnect", mauiContext, clearNativeContentAfterDisconnect: true);
		await ForceCollectionsAsync();

		var current = await RunScenarioAsync("current MAUI disconnect", mauiContext, clearNativeContentAfterDisconnect: false);
		await ForceCollectionsAsync();

		var controlResult = Inspect(control);
		var currentResult = Inspect(current);
		var leakProved =
			controlResult.NativeRefreshLayoutsRetained == Iterations &&
			controlResult.RefreshLayoutsWithContentField == 0 &&
			controlResult.PayloadTextViews == 0 &&
			controlResult.RetainedTextBytes == 0 &&
			currentResult.NativeRefreshLayoutsRetained == Iterations &&
			currentResult.RefreshLayoutsWithContentField == Iterations &&
			currentResult.PayloadTextViews == Iterations &&
			currentResult.RetainedTextBytes >= PayloadBytes * Iterations * 0.95 &&
			currentResult.ManagedRefreshViewsAlive <= 1 &&
			currentResult.ManagedHandlersAlive <= 1;

		var report = $"""
			Android RefreshView native content retention repro
			Iterations: {Iterations}
			Per-refresh copied native label payload: {PayloadChars:n0} UTF-16 chars ~= {FormatBytes(PayloadBytes)}
			Expected retained native label text if every current content subtree survives: {FormatBytes(PayloadBytes * Iterations)}

			Non-candidate state neutralized in both runs:
			  Normal RefreshViewHandler.DisconnectHandler() runs in both scenarios
			  MauiSwipeRefreshLayout.CrossPlatformLayout is cleared after disconnect
			  Virtual RefreshView.Content is cleared after handler disconnect
			  Native MauiSwipeRefreshLayout peers are retained identically by JNI global refs

			Control ({controlResult.Name})
			  Native MauiSwipeRefreshLayout peers retained by JNI global refs: {controlResult.NativeRefreshLayoutsRetained}/{Iterations}
			  Native refresh peers with private _contentView assigned: {controlResult.RefreshLayoutsWithContentField}/{Iterations}
			  Assigned _contentView peers still attached to parent: {controlResult.AttachedContentViews}/{Iterations}
			  Native TextView payload children retained through _contentView: {controlResult.PayloadTextViews}/{Iterations}
			  Retained native label text payload: {FormatBytes(controlResult.RetainedTextBytes)}
			  Managed RefreshView wrappers alive: {controlResult.ManagedRefreshViewsAlive}/{Iterations}
			  Managed RefreshViewHandler wrappers alive: {controlResult.ManagedHandlersAlive}/{Iterations}
			  Managed Label children alive: {controlResult.ManagedLabelsAlive}/{Iterations}
			  Managed LabelHandler wrappers alive: {controlResult.ManagedLabelHandlersAlive}/{Iterations}

			Current MAUI ({currentResult.Name})
			  Native MauiSwipeRefreshLayout peers retained by JNI global refs: {currentResult.NativeRefreshLayoutsRetained}/{Iterations}
			  Native refresh peers with private _contentView assigned: {currentResult.RefreshLayoutsWithContentField}/{Iterations}
			  Assigned _contentView peers still attached to parent: {currentResult.AttachedContentViews}/{Iterations}
			  Native TextView payload children retained through _contentView: {currentResult.PayloadTextViews}/{Iterations}
			  Retained native label text payload: {FormatBytes(currentResult.RetainedTextBytes)}
			  Managed RefreshView wrappers alive: {currentResult.ManagedRefreshViewsAlive}/{Iterations}
			  Managed RefreshViewHandler wrappers alive: {currentResult.ManagedHandlersAlive}/{Iterations}
			  Managed Label children alive: {currentResult.ManagedLabelsAlive}/{Iterations}
			  Managed LabelHandler wrappers alive: {currentResult.ManagedLabelHandlersAlive}/{Iterations}

			Verdict: {(leakProved ? "PROVED" : "NOT PROVED")}
			RESULT: {(leakProved ? "PROVEN" : "NOT PROVEN")}
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

	static async Task<ScenarioSnapshot> RunScenarioAsync(string name, IMauiContext mauiContext, bool clearNativeContentAfterDisconnect)
	{
		var samples = new List<IterationSnapshot>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			samples.Add(CreateIteration(i, mauiContext, clearNativeContentAfterDisconnect));

			if ((i + 1) % 16 == 0)
				await ForceCollectionsAsync();
		}

		return new ScenarioSnapshot(name, samples);
	}

	static IterationSnapshot CreateIteration(int index, IMauiContext mauiContext, bool clearNativeContentAfterDisconnect)
	{
		var label = new Label
		{
			Text = CreatePayloadText(index),
			LineBreakMode = LineBreakMode.NoWrap,
			WidthRequest = 720,
			HeightRequest = 48
		};

		var refreshView = new RefreshView
		{
			Content = label,
			IsRefreshEnabled = false,
			WidthRequest = 720,
			HeightRequest = 64
		};

		var handler = new RefreshViewHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(refreshView);
		RefreshViewHandler.MapContent(handler, refreshView);

		var platformView = handler.PlatformView;
		var contentView = GetContentView(platformView)
			?? throw new InvalidOperationException($"MauiSwipeRefreshLayout._contentView was not assigned for iteration {index}.");

		var nativeTextView = FindFirstTextView(contentView)
			?? throw new InvalidOperationException($"RefreshView content did not map to a native TextView for iteration {index}.");

		var nativeRoot = new NativePeerRoot(platformView);
		var handlerWeak = new WeakReference(handler);
		var refreshViewWeak = new WeakReference(refreshView);
		var labelWeak = new WeakReference(label);
		var labelHandlerWeak = new WeakReference(label.Handler);
		var nativeTextViewWeak = new WeakReference(nativeTextView);

		((IElementHandler)handler).DisconnectHandler();
		platformView.CrossPlatformLayout = null;

		if (clearNativeContentAfterDisconnect)
			ClearNativeContent(platformView);

		refreshView.Content = null;
		refreshView.BindingContext = null;
		label.BindingContext = null;

		handler = null!;
		refreshView = null!;
		label = null!;
		platformView = null!;
		contentView = null!;
		nativeTextView = null!;

		return new IterationSnapshot(nativeRoot, handlerWeak, refreshViewWeak, labelWeak, labelHandlerWeak, nativeTextViewWeak);
	}

	static string CreatePayloadText(int index)
	{
		var prefix = $"refresh-audit-row-{index:0000}: customer ledger delta, approvals, attachment summaries, offline notes, and sync diagnostics. ";
		var builder = new StringBuilder(PayloadChars);

		while (builder.Length < PayloadChars)
			builder.Append(prefix);

		return builder.ToString(0, PayloadChars);
	}

	static AView? GetContentView(MauiSwipeRefreshLayout refreshLayout)
	{
		return ContentViewField.GetValue(refreshLayout) as AView;
	}

	static void ClearNativeContent(MauiSwipeRefreshLayout refreshLayout)
	{
		var contentView = GetContentView(refreshLayout);
		contentView?.RemoveFromParent();
		refreshLayout.RemoveAllViews();
		ContentViewField.SetValue(refreshLayout, null);
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeRefreshLayouts = 0;
		var refreshLayoutsWithContentField = 0;
		var attachedContentViews = 0;
		var payloadTextViews = 0;
		var retainedTextBytes = 0L;
		var managedHandlersAlive = 0;
		var managedRefreshViewsAlive = 0;
		var managedLabelsAlive = 0;
		var managedLabelHandlersAlive = 0;
		var managedNativeTextViewWrappersAlive = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.HandlerWeak.IsAlive)
				managedHandlersAlive++;
			if (sample.RefreshViewWeak.IsAlive)
				managedRefreshViewsAlive++;
			if (sample.LabelWeak.IsAlive)
				managedLabelsAlive++;
			if (sample.LabelHandlerWeak.IsAlive)
				managedLabelHandlersAlive++;
			if (sample.NativeTextViewWeak.IsAlive)
				managedNativeTextViewWrappersAlive++;

			var refreshLayout = sample.NativeRoot.Get<MauiSwipeRefreshLayout>();
			if (refreshLayout == null)
				continue;

			nativeRefreshLayouts++;

			var contentView = GetContentView(refreshLayout);
			if (contentView is null)
				continue;

			refreshLayoutsWithContentField++;

			if (refreshLayout.IndexOfChild(contentView) >= 0)
				attachedContentViews++;

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
			nativeRefreshLayouts,
			refreshLayoutsWithContentField,
			attachedContentViews,
			payloadTextViews,
			retainedTextBytes,
			managedHandlersAlive,
			managedRefreshViewsAlive,
			managedLabelsAlive,
			managedLabelHandlersAlive,
			managedNativeTextViewWrappersAlive);
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

	sealed record IterationSnapshot(
		NativePeerRoot NativeRoot,
		WeakReference HandlerWeak,
		WeakReference RefreshViewWeak,
		WeakReference LabelWeak,
		WeakReference LabelHandlerWeak,
		WeakReference NativeTextViewWeak);

	sealed record ScenarioResult(
		string Name,
		int NativeRefreshLayoutsRetained,
		int RefreshLayoutsWithContentField,
		int AttachedContentViews,
		int PayloadTextViews,
		long RetainedTextBytes,
		int ManagedHandlersAlive,
		int ManagedRefreshViewsAlive,
		int ManagedLabelsAlive,
		int ManagedLabelHandlersAlive,
		int ManagedNativeTextViewWrappersAlive);

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
