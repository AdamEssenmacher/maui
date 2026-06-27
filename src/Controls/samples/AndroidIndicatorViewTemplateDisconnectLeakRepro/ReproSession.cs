#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;

namespace AndroidIndicatorViewTemplateDisconnectLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveNativePageControls,
	int AliveNativeTemplateViews,
	int AliveIndicatorHandlers,
	int AliveTemplateLayoutHandlers,
	int AliveTemplateLayouts,
	int AliveIndicatorViews,
	int AlivePayloads,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int ItemsPerIndicator,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveTemplateLayoutHandlers == 0 &&
		Control.AliveTemplateLayouts == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveNativePageControls == Attempts &&
		Current.AliveNativeTemplateViews == Attempts &&
		Current.AliveTemplateLayoutHandlers == Attempts &&
		Current.AliveTemplateLayouts == Attempts &&
		Current.AlivePayloads == Attempts * ItemsPerIndicator;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidIndicatorViewTemplateDisconnectLeakRepro",
			$"Attempts: {Attempts}",
			$"Items per IndicatorView: {ItemsPerIndicator}",
			$"Payload per item: {PayloadBytes / 1024} KiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		var payloadBudget = (long)PayloadBytes * ItemsPerIndicator * stats.Attempts;
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native MauiPageControls: {stats.Attempts}",
			$"  native page controls alive after full GC: {stats.AliveNativePageControls}/{stats.Attempts}",
			$"  native template views alive after full GC: {stats.AliveNativeTemplateViews}/{stats.Attempts}",
			$"  indicator handlers alive after full GC: {stats.AliveIndicatorHandlers}/{stats.Attempts}",
			$"  template layout handlers alive after full GC: {stats.AliveTemplateLayoutHandlers}/{stats.Attempts}",
			$"  template layouts alive after full GC: {stats.AliveTemplateLayouts}/{stats.Attempts}",
			$"  indicator views alive after full GC: {stats.AliveIndicatorViews}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts * ItemsPerIndicator}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / payloadBudget:0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

internal static class ReproSession
{
	const int Attempts = 80;
	const int ItemsPerIndicator = 8;
	const int PayloadBytes = 256 * 1024;

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: explicitly remove and disconnect template layout before IndicatorView disconnect",
			explicitTemplateCleanup: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: IndicatorView disconnect leaves templated native child attached",
			explicitTemplateCleanup: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, ItemsPerIndicator, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool explicitTemplateCleanup)
	{
		var retainedNativePageControls = new List<MauiPageControl>(Attempts);
		var nativePageControlRefs = new List<WeakReference<MauiPageControl>>(Attempts);
		var nativeTemplateViewRefs = new List<WeakReference<AView>>(Attempts);
		var indicatorHandlerRefs = new List<WeakReference<IndicatorViewHandler>>(Attempts);
		var templateLayoutHandlerRefs = new List<WeakReference<IElementHandler>>(Attempts);
		var templateLayoutRefs = new List<WeakReference<Layout>>(Attempts);
		var indicatorViewRefs = new List<WeakReference<IndicatorView>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts * ItemsPerIndicator);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedIndicatorView(
				mauiContext,
				explicitTemplateCleanup,
				retainedNativePageControls,
				nativePageControlRefs,
				nativeTemplateViewRefs,
				indicatorHandlerRefs,
				templateLayoutHandlerRefs,
				templateLayoutRefs,
				indicatorViewRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Yield();
		ForceFullGc();
		GC.KeepAlive(retainedNativePageControls);

		var aliveNativePageControls = nativePageControlRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveNativeTemplateViews = nativeTemplateViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveIndicatorHandlers = indicatorHandlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveTemplateLayoutHandlers = templateLayoutHandlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveTemplateLayouts = templateLayoutRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveIndicatorViews = indicatorViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveNativePageControls,
			aliveNativeTemplateViews,
			aliveIndicatorHandlers,
			aliveTemplateLayoutHandlers,
			aliveTemplateLayouts,
			aliveIndicatorViews,
			alivePayloads,
			(long)alivePayloads * PayloadBytes);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisconnectedIndicatorView(
		IMauiContext mauiContext,
		bool explicitTemplateCleanup,
		List<MauiPageControl> retainedNativePageControls,
		List<WeakReference<MauiPageControl>> nativePageControlRefs,
		List<WeakReference<AView>> nativeTemplateViewRefs,
		List<WeakReference<IndicatorViewHandler>> indicatorHandlerRefs,
		List<WeakReference<IElementHandler>> templateLayoutHandlerRefs,
		List<WeakReference<Layout>> templateLayoutRefs,
		List<WeakReference<IndicatorView>> indicatorViewRefs,
		List<WeakReference<Payload>> payloadRefs,
		int attempt)
	{
		var payloads = Enumerable.Range(0, ItemsPerIndicator)
			.Select(index => new Payload(attempt, index, PayloadBytes))
			.ToArray();

		foreach (var payload in payloads)
			payloadRefs.Add(new WeakReference<Payload>(payload));

		var indicatorView = new IndicatorView
		{
			MaximumVisible = ItemsPerIndicator,
			HideSingle = false,
			IndicatorTemplate = CreatePayloadTemplate(),
			ItemsSource = payloads
		};
		indicatorViewRefs.Add(new WeakReference<IndicatorView>(indicatorView));

		var handler = new IndicatorViewHandler();
		((IElementHandler)handler).SetMauiContext(mauiContext);
		((IElementHandler)handler).SetVirtualView(indicatorView);
		indicatorHandlerRefs.Add(new WeakReference<IndicatorViewHandler>(handler));

		var pageControl = ((IElementHandler)handler).PlatformView as MauiPageControl
			?? throw new InvalidOperationException("Expected Android MauiPageControl.");

		pageControl.ResetIndicators();

		var templateLayout = ((ITemplatedIndicatorView)indicatorView).IndicatorsLayoutOverride as Layout
			?? throw new InvalidOperationException("Expected templated IndicatorStackLayout.");
		templateLayoutRefs.Add(new WeakReference<Layout>(templateLayout));

		if (templateLayout.Handler is not IElementHandler templateLayoutHandler)
			throw new InvalidOperationException("Expected template layout handler.");

		templateLayoutHandlerRefs.Add(new WeakReference<IElementHandler>(templateLayoutHandler));

		var nativeTemplateView = pageControl.ChildCount > 0
			? pageControl.GetChildAt(0)
			: null;
		if (nativeTemplateView is null)
			throw new InvalidOperationException("Expected templated native child.");

		nativeTemplateViewRefs.Add(new WeakReference<AView>(nativeTemplateView));

		if (explicitTemplateCleanup)
		{
			templateLayoutHandler.DisconnectHandler();
			pageControl.RemoveAllViews();
		}

		((IElementHandler)handler).DisconnectHandler();

		retainedNativePageControls.Add(pageControl);
		nativePageControlRefs.Add(new WeakReference<MauiPageControl>(pageControl));
	}

	static DataTemplate CreatePayloadTemplate()
	{
		return new DataTemplate(() =>
		{
			var label = new Label
			{
				FontSize = 10,
				TextColor = Colors.White,
				BackgroundColor = Colors.DarkSlateBlue,
				WidthRequest = 52,
				HeightRequest = 28,
				HorizontalTextAlignment = TextAlignment.Center,
				VerticalTextAlignment = TextAlignment.Center
			};
			label.SetBinding(Label.TextProperty, nameof(Payload.DisplayName));

			return new Border
			{
				StrokeThickness = 0,
				StrokeShape = new RoundRectangle { CornerRadius = 6 },
				Content = label
			};
		});
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed class Payload
	{
		public Payload(int attempt, int index, int byteCount)
		{
			DisplayName = $"{attempt:00}-{index:00}";
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)((attempt + index) % 251);
			Bytes[^1] = (byte)((attempt + index + 1) % 251);
		}

		public string DisplayName { get; }

		public byte[] Bytes { get; }
	}
}
