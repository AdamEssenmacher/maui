#if IOS || MACCATALYST
using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace IndicatorViewTemplateDisconnectRetentionRepro;

internal static class ReproSession
{
	const int Attempts = 80;
	const int ItemsPerIndicator = 8;
	const int PayloadBytes = 256 * 1024;

	public static readonly string ResultsPath = "/tmp/ios-indicatorview-template-disconnect-results.txt";

	public static Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = RunScenario(
			mauiContext,
			"control: disconnect logical template tree and dispose native template subtree",
			explicitTemplateCleanup: true);

		var current = RunScenario(
			mauiContext,
			"current: IndicatorView disconnect leaves native template root attached",
			explicitTemplateCleanup: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);

		return Task.FromResult(new ReproReport(
			Attempts,
			ItemsPerIndicator,
			PayloadBytes,
			baselineBytes,
			finalBytes,
			control,
			current));
	}

	static ScenarioResult RunScenario(IMauiContext mauiContext, string name, bool explicitTemplateCleanup)
	{
		var retainedPageControls = new List<MauiPageControl>(Attempts);
		var tracked = new List<TrackedCycle>(Attempts);

		for (var i = 0; i < Attempts; i++)
			CreateDisconnectedIndicatorCycle(mauiContext, i, explicitTemplateCleanup, retainedPageControls, tracked);

		ForceFullGc();
		GC.KeepAlive(retainedPageControls);

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisconnectedIndicatorCycle(
		IMauiContext mauiContext,
		int cycle,
		bool explicitTemplateCleanup,
		List<MauiPageControl> retainedPageControls,
		List<TrackedCycle> tracked)
	{
		var items = Enumerable.Range(0, ItemsPerIndicator)
			.Select(index => new PayloadItem(cycle, index, PayloadBytes))
			.ToArray();

		var indicatorView = new IndicatorView
		{
			MaximumVisible = ItemsPerIndicator,
			HideSingle = false,
			IndicatorTemplate = CreatePayloadTemplate(),
			ItemsSource = items
		};

		var handler = new IndicatorViewHandler();
		((IElementHandler)handler).SetMauiContext(mauiContext);
		((IElementHandler)handler).SetVirtualView(indicatorView);

		var pageControl = ((IElementHandler)handler).PlatformView as MauiPageControl
			?? throw new InvalidOperationException("Expected iOS MauiPageControl.");

		var templateLayout = ((ITemplatedIndicatorView)indicatorView).IndicatorsLayoutOverride as Microsoft.Maui.Controls.Layout
			?? throw new InvalidOperationException("Expected templated IndicatorStackLayout.");

		if (templateLayout.Handler is not IElementHandler templateLayoutHandler)
			throw new InvalidOperationException("Expected template layout handler.");

		var nativeTemplateRoot = pageControl.Subviews.LastOrDefault()
			?? throw new InvalidOperationException("Expected templated native child.");

		var nativePayloadViews = FindDescendants<PayloadNativeView>(nativeTemplateRoot).ToArray();
		if (nativePayloadViews.Length != ItemsPerIndicator)
			throw new InvalidOperationException($"Expected {ItemsPerIndicator} native payload views, found {nativePayloadViews.Length}.");

		tracked.Add(TrackedCycle.Create(
			pageControl,
			nativeTemplateRoot,
			nativePayloadViews,
			handler,
			templateLayoutHandler,
			templateLayout,
			indicatorView,
			items));

		if (explicitTemplateCleanup)
		{
			DisconnectLogicalTree(templateLayout);
			CleanupNativeTree(nativeTemplateRoot);
		}

		((IElementHandler)handler).DisconnectHandler();
		retainedPageControls.Add(pageControl);
	}

	static IEnumerable<T> FindDescendants<T>(UIView root)
		where T : UIView
	{
		if (root is T match)
			yield return match;

		foreach (var subview in root.Subviews)
		{
			foreach (var child in FindDescendants<T>(subview))
				yield return child;
		}
	}

	static void CleanupNativeTree(UIView root)
	{
		foreach (var subview in root.Subviews.ToArray())
			CleanupNativeTree(subview);

		if (root is PayloadNativeView payloadView)
			payloadView.Payload = null;

		root.RemoveFromSuperview();
		root.Dispose();
	}

	static void DisconnectLogicalTree(Element element)
	{
		foreach (var child in ((IElementController)element).LogicalChildren.ToArray())
			DisconnectLogicalTree(child);

		if (element is PayloadIndicatorView payloadView)
		{
			payloadView.RemoveBinding(PayloadIndicatorView.PayloadProperty);
			payloadView.Payload = null;
			payloadView.BindingContext = null;
		}

		if (element.Handler is IElementHandler handler)
			handler.DisconnectHandler();
	}

	static DataTemplate CreatePayloadTemplate()
	{
		return new DataTemplate(() =>
		{
			var view = new PayloadIndicatorView
			{
				WidthRequest = 52,
				HeightRequest = 28
			};
			view.SetBinding(PayloadIndicatorView.PayloadProperty, ".");

			return new Border
			{
				StrokeThickness = 0,
				StrokeShape = new RoundRectangle { CornerRadius = 6 },
				Content = view
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

	internal sealed record PayloadItem(int Cycle, int Index, int ByteCount)
	{
		public string DisplayName { get; } = $"{Cycle:00}-{Index:00}";

		public byte[] Bytes { get; } = CreateBytes(Cycle, Index, ByteCount);

		static byte[] CreateBytes(int cycle, int index, int byteCount)
		{
			var bytes = new byte[byteCount];
			for (var i = 0; i < bytes.Length; i += 4096)
				bytes[i] = (byte)((cycle + index + i) % 251);

			bytes[^1] = (byte)((cycle + index + 1) % 251);
			return bytes;
		}
	}

	internal sealed record TrackedCycle(
		WeakReference NativePageControl,
		WeakReference NativeTemplateRoot,
		IReadOnlyList<WeakReference> NativePayloadViews,
		WeakReference IndicatorHandler,
		WeakReference TemplateLayoutHandler,
		WeakReference TemplateLayout,
		WeakReference IndicatorView,
		IReadOnlyList<WeakReference> PayloadItems,
		IReadOnlyList<WeakReference> PayloadBytes)
	{
		public static TrackedCycle Create(
			MauiPageControl pageControl,
			UIView nativeTemplateRoot,
			IReadOnlyList<PayloadNativeView> nativePayloadViews,
			IndicatorViewHandler indicatorHandler,
			IElementHandler templateLayoutHandler,
			Microsoft.Maui.Controls.Layout templateLayout,
			IndicatorView indicatorView,
			IReadOnlyList<PayloadItem> payloadItems)
		{
			return new TrackedCycle(
				new WeakReference(pageControl),
				new WeakReference(nativeTemplateRoot),
				nativePayloadViews.Select(view => new WeakReference(view)).ToArray(),
				new WeakReference(indicatorHandler),
				new WeakReference(templateLayoutHandler),
				new WeakReference(templateLayout),
				new WeakReference(indicatorView),
				payloadItems.Select(item => new WeakReference(item)).ToArray(),
				payloadItems.Select(item => new WeakReference(item.Bytes)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int Attempts,
		int NativePageControlsAlive,
		int NativeTemplateRootsAlive,
		int NativePayloadViewsAlive,
		int IndicatorHandlersAlive,
		int TemplateLayoutHandlersAlive,
		int TemplateLayoutsAlive,
		int IndicatorViewsAlive,
		int PayloadItemsAlive,
		int PayloadBytesAlive,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePageControlsAlive = 0;
			var nativeTemplateRootsAlive = 0;
			var nativePayloadViewsAlive = 0;
			var indicatorHandlersAlive = 0;
			var templateLayoutHandlersAlive = 0;
			var templateLayoutsAlive = 0;
			var indicatorViewsAlive = 0;
			var payloadItemsAlive = 0;
			var payloadBytesAlive = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePageControl.IsAlive)
					nativePageControlsAlive++;
				if (cycle.NativeTemplateRoot.IsAlive)
					nativeTemplateRootsAlive++;
				if (cycle.IndicatorHandler.IsAlive)
					indicatorHandlersAlive++;
				if (cycle.TemplateLayoutHandler.IsAlive)
					templateLayoutHandlersAlive++;
				if (cycle.TemplateLayout.IsAlive)
					templateLayoutsAlive++;
				if (cycle.IndicatorView.IsAlive)
					indicatorViewsAlive++;

				nativePayloadViewsAlive += cycle.NativePayloadViews.Count(static reference => reference.IsAlive);
				payloadItemsAlive += cycle.PayloadItems.Count(static reference => reference.IsAlive);
				payloadBytesAlive += cycle.PayloadBytes.Count(static reference => reference.IsAlive);
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				nativePageControlsAlive,
				nativeTemplateRootsAlive,
				nativePayloadViewsAlive,
				indicatorHandlersAlive,
				templateLayoutHandlersAlive,
				templateLayoutsAlive,
				indicatorViewsAlive,
				payloadItemsAlive,
				payloadBytesAlive,
				(long)payloadBytesAlive * PayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Attempts,
		int ItemsPerIndicator,
		int PayloadBytesPerItem,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Current)
	{
		public string ToText()
		{
			var expectedPayloads = Attempts * ItemsPerIndicator;
			var proven = Control.PayloadItemsAlive == 0 &&
				Control.PayloadBytesAlive == 0 &&
				Current.NativePageControlsAlive == Attempts &&
				Current.NativeTemplateRootsAlive == Attempts &&
				Current.NativePayloadViewsAlive == expectedPayloads &&
				Current.PayloadItemsAlive == expectedPayloads &&
				Current.PayloadBytesAlive == expectedPayloads;

			var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

			return string.Join(Environment.NewLine, new[]
			{
				"IndicatorView native template disconnect retention repro",
				$"Attempts: {Attempts}",
				$"Items per IndicatorView: {ItemsPerIndicator}",
				$"Payload per item: {PayloadBytesPerItem / 1024} KiB",
				$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
				$"Final managed heap: {FinalManagedBytes:N0} bytes",
				$"Managed heap delta: {heapDeltaMiB:N1} MiB",
				string.Empty,
				FormatScenario(Control),
				string.Empty,
				FormatScenario(Current),
				string.Empty,
				$"Control retained payload: {Control.RetainedPayloadBytes / 1024d / 1024d:N1} MiB",
				$"Current retained payload: {Current.RetainedPayloadBytes / 1024d / 1024d:N1} MiB",
				$"RESULT: {(proven ? "PROVEN" : "NOT PROVEN")}"
			});
		}

		string FormatScenario(ScenarioResult result)
		{
			var totalPayloads = result.Attempts * ItemsPerIndicator;
			return string.Join(Environment.NewLine, new[]
			{
				result.Name,
				$"  retained native MauiPageControls: {result.NativePageControlsAlive}/{result.Attempts}",
				$"  native template roots alive: {result.NativeTemplateRootsAlive}/{result.Attempts}",
				$"  native payload views alive: {result.NativePayloadViewsAlive}/{totalPayloads}",
				$"  indicator handlers alive: {result.IndicatorHandlersAlive}/{result.Attempts}",
				$"  template layout handlers alive: {result.TemplateLayoutHandlersAlive}/{result.Attempts}",
				$"  template layouts alive: {result.TemplateLayoutsAlive}/{result.Attempts}",
				$"  IndicatorViews alive: {result.IndicatorViewsAlive}/{result.Attempts}",
				$"  payload items alive: {result.PayloadItemsAlive}/{totalPayloads}",
				$"  payload byte arrays alive: {result.PayloadBytesAlive}/{totalPayloads}",
				$"  retained payload bytes: {result.RetainedPayloadBytes:N0}"
			});
		}
	}
}

internal sealed class PayloadIndicatorView : Microsoft.Maui.Controls.View
{
	public static readonly BindableProperty PayloadProperty = BindableProperty.Create(
		nameof(Payload),
		typeof(ReproSession.PayloadItem),
		typeof(PayloadIndicatorView));

	public ReproSession.PayloadItem? Payload
	{
		get => (ReproSession.PayloadItem?)GetValue(PayloadProperty);
		set => SetValue(PayloadProperty, value);
	}
}

internal sealed class PayloadIndicatorViewHandler : ViewHandler<PayloadIndicatorView, PayloadNativeView>
{
	public static readonly IPropertyMapper<PayloadIndicatorView, PayloadIndicatorViewHandler> Mapper =
		new PropertyMapper<PayloadIndicatorView, PayloadIndicatorViewHandler>(ViewMapper)
		{
			[nameof(PayloadIndicatorView.Payload)] = MapPayload
		};

	public PayloadIndicatorViewHandler()
		: base(Mapper)
	{
	}

	protected override PayloadNativeView CreatePlatformView() => new();

	static void MapPayload(PayloadIndicatorViewHandler handler, PayloadIndicatorView view)
	{
		handler.PlatformView.Payload = view.Payload;
	}
}

internal sealed class PayloadNativeView : UIView
{
	ReproSession.PayloadItem? _payload;

	public ReproSession.PayloadItem? Payload
	{
		get => _payload;
		set
		{
			_payload = value;
			SetNeedsDisplay();
		}
	}

	public override CGSize SizeThatFits(CGSize size) => new(52, 28);

	public override void Draw(CGRect rect)
	{
		base.Draw(rect);
		UIColor.SystemIndigo.SetFill();
		UIBezierPath.FromRoundedRect(rect, 6).Fill();
	}
}
#endif
