#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;

namespace AndroidFlyoutViewNativeContentRetentionRepro;

public static class ReproSession
{
	const int Iterations = 96;
	const int PayloadChars = 128 * 1024;
	const long PayloadBytes = PayloadChars * 2L;
	const int BindingContextPayloadBytes = 512 * 1024;

	static readonly FieldInfo FlyoutViewField =
		typeof(FlyoutViewHandler).GetField("_flyoutView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(FlyoutViewHandler).FullName, "_flyoutView");

	static readonly FieldInfo NavigationRootField =
		typeof(FlyoutViewHandler).GetField("_navigationRoot", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(FlyoutViewHandler).FullName, "_navigationRoot");

	static readonly FieldInfo SideBySideViewField =
		typeof(FlyoutViewHandler).GetField("_sideBySideView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(FlyoutViewHandler).FullName, "_sideBySideView");

	static readonly FieldInfo PendingFragmentField =
		typeof(FlyoutViewHandler).GetField("_pendingFragment", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(FlyoutViewHandler).FullName, "_pendingFragment");

	static readonly FieldInfo DetailViewFragmentField =
		typeof(FlyoutViewHandler).GetField("_detailViewFragment", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(FlyoutViewHandler).FullName, "_detailViewFragment");

	static readonly IPropertyMapper<IFlyoutView, IFlyoutViewHandler> NoFlyoutLayoutMapper =
		new PropertyMapper<IFlyoutView, IFlyoutViewHandler>(ViewHandler.ViewMapper);

	public static async Task<string> RunAsync(Page hostPage)
	{
		var mauiContext = await WaitForMauiContextAsync(hostPage);

		var control = await RunScenarioAsync("explicit drawer child clear", mauiContext, clearNativeChildrenBeforeDisconnect: true);
		await ForceCollectionsAsync();

		var current = await RunScenarioAsync("current MAUI disconnect", mauiContext, clearNativeChildrenBeforeDisconnect: false);
		await ForceCollectionsAsync();

		var controlResult = Inspect(control);
		var currentResult = Inspect(current);

		var report = $"""
			Android FlyoutView native content retention repro
			Iterations: {Iterations}
			Per-flyout copied native label payload: {PayloadChars:n0} UTF-16 chars ~= {FormatBytes(PayloadBytes)}
			Per-flyout BindingContext payload: {FormatBytes(BindingContextPayloadBytes)}
			Expected retained native label text if every current flyout subtree survives: {FormatBytes(PayloadBytes * Iterations)}
			Expected retained BindingContext payload if every current flyout page survives: {FormatBytes(BindingContextPayloadBytes * Iterations)}

			Non-candidate state cleared in both runs:
			  FlyoutViewHandler._pendingFragment
			  FlyoutViewHandler._detailViewFragment
			  Flyout and detail page Content after handler disconnect

			Control ({controlResult.Name})
			  Native DrawerLayout peers retained by JNI global refs: {controlResult.NativeDrawerLayoutsRetained}/{Iterations}
			  DrawerLayout peers with child views: {controlResult.DrawersWithChildren}/{Iterations}
			  Native payload TextView children retained: {controlResult.PayloadTextViews}/{Iterations}
			  Retained native label text payload: {FormatBytes(controlResult.RetainedTextBytes)}
			  Managed BindingContext payloads alive: {controlResult.ManagedBindingContextsAlive}/{Iterations}
			  Retained managed BindingContext payload: {FormatBytes(controlResult.RetainedBindingContextBytes)}
			  Managed FlyoutPage wrappers alive: {controlResult.ManagedFlyoutPagesAlive}/{Iterations}
			  Managed FlyoutViewHandler wrappers alive: {controlResult.ManagedHandlersAlive}/{Iterations}
			  Managed flyout content pages alive: {controlResult.ManagedFlyoutContentPagesAlive}/{Iterations}
			  Managed Label children alive: {controlResult.ManagedLabelsAlive}/{Iterations}

			Current MAUI ({currentResult.Name})
			  Native DrawerLayout peers retained by JNI global refs: {currentResult.NativeDrawerLayoutsRetained}/{Iterations}
			  DrawerLayout peers with child views: {currentResult.DrawersWithChildren}/{Iterations}
			  Native payload TextView children retained: {currentResult.PayloadTextViews}/{Iterations}
			  Retained native label text payload: {FormatBytes(currentResult.RetainedTextBytes)}
			  Managed BindingContext payloads alive: {currentResult.ManagedBindingContextsAlive}/{Iterations}
			  Retained managed BindingContext payload: {FormatBytes(currentResult.RetainedBindingContextBytes)}
			  Managed FlyoutPage wrappers alive: {currentResult.ManagedFlyoutPagesAlive}/{Iterations}
			  Managed FlyoutViewHandler wrappers alive: {currentResult.ManagedHandlersAlive}/{Iterations}
			  Managed flyout content pages alive: {currentResult.ManagedFlyoutContentPagesAlive}/{Iterations}
			  Managed Label children alive: {currentResult.ManagedLabelsAlive}/{Iterations}

			Verdict: {(currentResult.RetainedBindingContextBytes > controlResult.RetainedBindingContextBytes || currentResult.RetainedTextBytes > controlResult.RetainedTextBytes ? "PROVED" : "NOT PROVED")}
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

	static async Task<ScenarioSnapshot> RunScenarioAsync(string name, IMauiContext mauiContext, bool clearNativeChildrenBeforeDisconnect)
	{
		var samples = new List<IterationSnapshot>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			samples.Add(CreateIteration(i, mauiContext, clearNativeChildrenBeforeDisconnect));

			if ((i + 1) % 16 == 0)
				await ForceCollectionsAsync();
		}

		return new ScenarioSnapshot(name, samples);
	}

	static IterationSnapshot CreateIteration(int index, IMauiContext mauiContext, bool clearNativeChildrenBeforeDisconnect)
	{
		var label = new Label
		{
			Text = CreatePayloadText(index),
			LineBreakMode = LineBreakMode.NoWrap
		};
		var bindingContextPayload = new PayloadModel(index);

		var flyoutContentPage = new ContentPage
		{
			Title = $"Audit flyout {index:0000}",
			Content = label,
			BindingContext = bindingContextPayload
		};

		var detailPage = new ContentPage
		{
			Content = new Label { Text = "Detail page used only to exercise the real FlyoutPage handler path." }
		};

		var flyoutPage = new FlyoutPage
		{
			Flyout = flyoutContentPage,
			Detail = detailPage
		};

		var handler = new FlyoutViewHandler(NoFlyoutLayoutMapper);
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(flyoutPage);

		var drawerLayout = (DrawerLayout)handler.PlatformView;
		InstallFlyoutNativeChild(handler, flyoutPage, mauiContext, drawerLayout);

		if (FindPayloadTextView(drawerLayout) is null)
			throw new InvalidOperationException($"Flyout payload platform view was not created for iteration {index}.");

		var nativeRoot = new NativePeerRoot(drawerLayout);
		var handlerWeak = new WeakReference(handler);
		var flyoutPageWeak = new WeakReference(flyoutPage);
		var flyoutContentPageWeak = new WeakReference(flyoutContentPage);
		var bindingContextWeak = new WeakReference(bindingContextPayload);
		var labelWeak = new WeakReference(label);

		if (clearNativeChildrenBeforeDisconnect)
			ClearNativeChildren(handler, drawerLayout);

		((IElementHandler)handler).DisconnectHandler();

		ClearKnownFragmentRoots(handler);
		flyoutContentPage.Content = null;
		detailPage.Content = null;

		handler = null!;
		flyoutPage = null!;
		flyoutContentPage = null!;
		detailPage = null!;
		bindingContextPayload = null!;
		label = null!;
		drawerLayout = null!;

		return new IterationSnapshot(nativeRoot, handlerWeak, flyoutPageWeak, flyoutContentPageWeak, bindingContextWeak, labelWeak);
	}

	static void InstallFlyoutNativeChild(FlyoutViewHandler handler, FlyoutPage flyoutPage, IMauiContext mauiContext, DrawerLayout drawerLayout)
	{
		var flyout = (Microsoft.Maui.IElement)((IFlyoutView)flyoutPage).Flyout;
		var flyoutView = flyout.ToPlatform(mauiContext);

		FlyoutViewField.SetValue(handler, flyoutView);
		drawerLayout.AddView(
			flyoutView,
			new DrawerLayout.LayoutParams(
				AViewGroup.LayoutParams.MatchParent,
				AViewGroup.LayoutParams.MatchParent,
				(int)GravityFlags.Start));
	}

	static string CreatePayloadText(int index)
	{
		var prefix = $"flyout-audit-row-{index:0000}:";
		var builder = new StringBuilder(PayloadChars);

		while (builder.Length < PayloadChars)
			builder.Append(prefix);

		return builder.ToString(0, PayloadChars);
	}

	static void ClearNativeChildren(FlyoutViewHandler handler, DrawerLayout drawerLayout)
	{
		RemoveAndDispose(FlyoutViewField.GetValue(handler) as AView);

		if (SideBySideViewField.GetValue(handler) is AViewGroup sideBySideView)
		{
			sideBySideView.RemoveAllViews();
			RemoveAndDispose(sideBySideView);
		}

		RemoveAndDispose(NavigationRootField.GetValue(handler) as AView);

		drawerLayout.RemoveAllViews();

		FlyoutViewField.SetValue(handler, null);
		SideBySideViewField.SetValue(handler, null);
		NavigationRootField.SetValue(handler, null);
	}

	static void RemoveAndDispose(AView? view)
	{
		if (view is null)
			return;

		if (view.Parent is AViewGroup parent)
			parent.RemoveView(view);

		if (view is AViewGroup group)
			group.RemoveAllViews();

		if (view.Handle != IntPtr.Zero)
			view.Dispose();
	}

	static void ClearKnownFragmentRoots(FlyoutViewHandler handler)
	{
		if (PendingFragmentField.GetValue(handler) is IDisposable pendingFragment)
			pendingFragment.Dispose();

		PendingFragmentField.SetValue(handler, null);
		DetailViewFragmentField.SetValue(handler, null);
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeDrawerLayouts = 0;
		var drawersWithChildren = 0;
		var payloadTextViews = 0;
		var retainedTextBytes = 0L;
		var managedBindingContextsAlive = 0;
		var retainedBindingContextBytes = 0L;
		var managedHandlersAlive = 0;
		var managedFlyoutPagesAlive = 0;
		var managedFlyoutContentPagesAlive = 0;
		var managedLabelsAlive = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.HandlerWeak.IsAlive)
				managedHandlersAlive++;
			if (sample.FlyoutPageWeak.IsAlive)
				managedFlyoutPagesAlive++;
			if (sample.FlyoutContentPageWeak.IsAlive)
				managedFlyoutContentPagesAlive++;
			if (sample.BindingContextWeak.IsAlive)
			{
				managedBindingContextsAlive++;
				retainedBindingContextBytes += BindingContextPayloadBytes;
			}
			if (sample.LabelWeak.IsAlive)
				managedLabelsAlive++;

			var drawerLayout = sample.NativeRoot.Get<DrawerLayout>();
			if (drawerLayout == null)
				continue;

			nativeDrawerLayouts++;

			if (drawerLayout.ChildCount > 0)
				drawersWithChildren++;

			foreach (var textView in FindPayloadTextViews(drawerLayout))
			{
				if (textView.Text is not string text || text.Length < PayloadChars)
					continue;

				payloadTextViews++;
				retainedTextBytes += text.Length * 2L;
			}
		}

		return new ScenarioResult(
			scenario.Name,
			nativeDrawerLayouts,
			drawersWithChildren,
			payloadTextViews,
			retainedTextBytes,
			managedBindingContextsAlive,
			retainedBindingContextBytes,
			managedHandlersAlive,
			managedFlyoutPagesAlive,
			managedFlyoutContentPagesAlive,
			managedLabelsAlive);
	}

	static TextView? FindPayloadTextView(AView view)
	{
		foreach (var textView in FindPayloadTextViews(view))
		{
			if (textView.Text is string text && text.Length >= PayloadChars)
				return textView;
		}

		return null;
	}

	static IEnumerable<TextView> FindPayloadTextViews(AView view)
	{
		if (view is TextView textView)
			yield return textView;

		if (view is not AViewGroup group)
			yield break;

		for (var i = 0; i < group.ChildCount; i++)
		{
			var child = group.GetChildAt(i);
			if (child is null)
				continue;

			foreach (var result in FindPayloadTextViews(child))
				yield return result;
		}
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
		WeakReference FlyoutPageWeak,
		WeakReference FlyoutContentPageWeak,
		WeakReference BindingContextWeak,
		WeakReference LabelWeak);

	sealed record ScenarioResult(
		string Name,
		int NativeDrawerLayoutsRetained,
		int DrawersWithChildren,
		int PayloadTextViews,
		long RetainedTextBytes,
		int ManagedBindingContextsAlive,
		long RetainedBindingContextBytes,
		int ManagedHandlersAlive,
		int ManagedFlyoutPagesAlive,
		int ManagedFlyoutContentPagesAlive,
		int ManagedLabelsAlive);

	sealed class PayloadModel
	{
		readonly byte[] _buffer = new byte[BindingContextPayloadBytes];

		public PayloadModel(int index)
		{
			for (var i = 0; i < _buffer.Length; i += 4096)
				_buffer[i] = (byte)(index + i);
		}
	}

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
