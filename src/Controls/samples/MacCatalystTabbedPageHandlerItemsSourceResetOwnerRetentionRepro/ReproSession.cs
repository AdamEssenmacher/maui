#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MacCatalystTabbedPageHandlerItemsSourceResetOwnerRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int ChildrenPerTabbedPage = 3;
	internal const int PayloadKiBPerOwner = 1024;

	const long PayloadBytesPerOwner = PayloadKiBPerOwner * 1024L;

	static readonly List<IReadOnlyList<Page>> RetainedChildPages = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "maccatalyst-tabbedpage-handler-itemssource-reset-owner-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting Mac Catalyst TabbedPage handler ItemsSource reset owner retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: remove handler-local generated child subscriptions before ItemsSource.Clear()",
			context,
			removeHandlerLocalSubscriptions: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ItemsSource.Clear() leaves handler-local generated child subscriptions",
			context,
			removeHandlerLocalSubscriptions: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedChildPages);

		return new ReproReport(
			Cycles,
			ChildrenPerTabbedPage,
			PayloadKiBPerOwner,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext baseContext,
		bool removeHandlerLocalSubscriptions)
	{
		var retainedChildren = new List<Page>(Cycles * ChildrenPerTabbedPage);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateDiscardedOwnerCycle(i, baseContext, retainedChildren, tracked, removeHandlerLocalSubscriptions);

			if (i % 8 == 0)
				await DrainMainQueueAsync();
		}

		RetainedChildPages.Add(retainedChildren);
		await DrainMainQueueAsync();
		ForceFullGc();

		return ScenarioResult.From(name, retainedChildren, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDiscardedOwnerCycle(
		int cycle,
		IMauiContext baseContext,
		List<Page> retainedChildren,
		List<TrackedCycle> tracked,
		bool removeHandlerLocalSubscriptions)
	{
		using var pool = new NSAutoreleasePool();

		var generatedPages = new List<Page>(ChildrenPerTabbedPage);
		var items = new ObservableCollection<PayloadTabItem>();
		var tabbedPage = new PayloadTabbedPage(cycle, checked((int)PayloadBytesPerOwner))
		{
			ItemTemplate = new DataTemplate(() =>
			{
				var pageIndex = generatedPages.Count;
				var page = new PayloadChildPage(cycle, pageIndex);
				generatedPages.Add(page);
				return page;
			})
		};

		for (var i = 0; i < ChildrenPerTabbedPage; i++)
			items.Add(new PayloadTabItem(cycle, i));

		tabbedPage.ItemsSource = items;

		if (generatedPages.Count != ChildrenPerTabbedPage || tabbedPage.Children.Count != ChildrenPerTabbedPage)
			throw new InvalidOperationException($"ItemsSource did not generate {ChildrenPerTabbedPage} child pages.");

		var handler = new StubElementHandler(baseContext);
		tabbedPage.Handler = handler;

		var childPages = generatedPages.ToArray();
		var subscriptionsAfterAttach = CountHandlerLocalSubscriptions(childPages);

		if (subscriptionsAfterAttach != ChildrenPerTabbedPage)
			throw new InvalidOperationException($"Handler attach created {subscriptionsAfterAttach} child subscriptions; expected {ChildrenPerTabbedPage}.");

		tracked.Add(TrackedCycle.Create(cycle, tabbedPage, handler, childPages, tabbedPage.OwnerPayload, tabbedPage.OwnerPayload.Buffer));

		if (removeHandlerLocalSubscriptions)
		{
			var removed = 0;

			foreach (var page in childPages)
				removed += RemoveHandlerLocalSubscriptions(page);

			if (removed != ChildrenPerTabbedPage)
				throw new InvalidOperationException($"Control removed {removed} handler-local child subscriptions; expected {ChildrenPerTabbedPage}.");
		}

		items.Clear();

		if (tabbedPage.Children.Count != 0)
			throw new InvalidOperationException("ItemsSource.Clear() did not remove the generated child pages.");

		tabbedPage.ClearLogicalChildren();
		tabbedPage.Handler = null;
		handler.ClearContext();

		retainedChildren.AddRange(childPages);
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(50);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.02));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	static int CountHandlerLocalSubscriptions(IReadOnlyList<Page> pages)
	{
		var count = 0;

		foreach (var page in pages)
		{
			var handler = PropertyChangedField(page);
			if (handler is null)
				continue;

			foreach (var subscriber in handler.GetInvocationList().OfType<PropertyChangedEventHandler>())
			{
				if (IsHandlerLocalTabbedPageSubscriber(subscriber))
					count++;
			}
		}

		return count;
	}

	static int RemoveHandlerLocalSubscriptions(Page page)
	{
		ref var handler = ref PropertyChangedField(page);
		if (handler is null)
			return 0;

		PropertyChangedEventHandler? kept = null;
		var removed = 0;

		foreach (var subscriber in handler.GetInvocationList().OfType<PropertyChangedEventHandler>())
		{
			if (IsHandlerLocalTabbedPageSubscriber(subscriber))
			{
				removed++;
				continue;
			}

			kept += subscriber;
		}

		handler = kept;
		return removed;
	}

	static bool IsHandlerLocalTabbedPageSubscriber(PropertyChangedEventHandler subscriber)
	{
		var method = subscriber.Method;
		var methodName = method.Name;
		var declaringTypeName = method.DeclaringType?.FullName ?? string.Empty;

		return methodName.Contains("OnHandlerChangingCore", StringComparison.Ordinal) &&
			methodName.Contains("OnPagePropertyChanged", StringComparison.Ordinal) &&
			declaringTypeName.Contains("TabbedPage", StringComparison.Ordinal);
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "PropertyChanged")]
	static extern ref PropertyChangedEventHandler? PropertyChangedField(BindableObject bindable);

	internal sealed record OwnerPayload(int Cycle, byte[] Buffer)
	{
		public OwnerPayload(int cycle, int payloadBytes)
			: this(cycle, CreateBuffer(cycle, payloadBytes))
		{
		}

		public int Touch()
		{
			var checksum = Cycle + 1;

			for (var i = 0; i < Buffer.Length; i += 4096)
				checksum += Buffer[i] + 1;

			return checksum;
		}

		static byte[] CreateBuffer(int cycle, int payloadBytes)
		{
			var buffer = new byte[payloadBytes];

			for (var i = 0; i < buffer.Length; i += 4096)
				buffer[i] = unchecked((byte)(cycle + i));

			return buffer;
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TabbedPage> TabbedPage,
		WeakReference<StubElementHandler> Handler,
		IReadOnlyList<WeakReference<Page>> ChildPages,
		WeakReference<OwnerPayload> OwnerPayload,
		WeakReference<byte[]> PayloadBuffer)
	{
		public static TrackedCycle Create(
			int cycle,
			TabbedPage tabbedPage,
			StubElementHandler handler,
			IReadOnlyList<Page> childPages,
			OwnerPayload ownerPayload,
			byte[] payloadBuffer)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TabbedPage>(tabbedPage),
				new WeakReference<StubElementHandler>(handler),
				childPages.Select(static page => new WeakReference<Page>(page)).ToArray(),
				new WeakReference<OwnerPayload>(ownerPayload),
				new WeakReference<byte[]>(payloadBuffer));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedChildPages,
		int HandlerLocalChildSubscriptions,
		int AliveTabbedPages,
		int AliveHandlers,
		int AliveChildPages,
		int AliveOwnerPayloads,
		int AlivePayloadBuffers,
		long EstimatedOwnerPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<Page> retainedChildPages,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var subscriptions = CountHandlerLocalSubscriptions(retainedChildPages);
			var aliveTabbedPages = 0;
			var aliveHandlers = 0;
			var aliveChildPages = 0;
			var aliveOwnerPayloads = 0;
			var alivePayloadBuffers = 0;
			long estimatedOwnerPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.TabbedPage.TryGetTarget(out _))
					aliveTabbedPages++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				foreach (var childPage in cycle.ChildPages)
				{
					if (childPage.TryGetTarget(out _))
						aliveChildPages++;
				}

				if (cycle.OwnerPayload.TryGetTarget(out var ownerPayload))
				{
					aliveOwnerPayloads++;
					estimatedOwnerPayloadBytes += Math.Min(ownerPayload.Buffer.Length, PayloadBytesPerOwner);
				}

				if (cycle.PayloadBuffer.TryGetTarget(out _))
					alivePayloadBuffers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedChildPages.Count,
				subscriptions,
				aliveTabbedPages,
				aliveHandlers,
				aliveChildPages,
				aliveOwnerPayloads,
				alivePayloadBuffers,
				estimatedOwnerPayloadBytes);
		}
	}
}

sealed class PayloadTabbedPage : TabbedPage
{
	public PayloadTabbedPage(int cycle, int payloadBytes)
	{
		Title = $"Regional operations tabs {cycle + 1}";
		AutomationId = $"tabbed-page-handler-owner-retention-{cycle + 1}";
		BarBackgroundColor = Colors.White;
		BarTextColor = Colors.Black;
		OwnerPayload = new ReproSession.OwnerPayload(cycle, payloadBytes);

		if (OwnerPayload.Buffer.Length != payloadBytes || OwnerPayload.Touch() == 0)
			throw new InvalidOperationException("The synthetic owner payload was not initialized.");
	}

	public ReproSession.OwnerPayload OwnerPayload { get; }
}

sealed record PayloadTabItem(int Cycle, int Child);

sealed class PayloadChildPage : ContentPage
{
	public PayloadChildPage(int cycle, int child)
	{
		Title = $"Territory {cycle + 1}-{child + 1}";
		AutomationId = $"retained-tab-child-{cycle + 1}-{child + 1}";
		Content = new Label { Text = Title };
	}
}

sealed class StubElementHandler : IViewHandler
{
	IMauiContext? _mauiContext;
	IView? _virtualView;

	public StubElementHandler(IMauiContext mauiContext)
	{
		_mauiContext = mauiContext;
	}

	public object? PlatformView => null;

	public object? ContainerView => null;

	public bool HasContainer { get; set; }

	public IView? VirtualView => _virtualView;

	IElement? IElementHandler.VirtualView => _virtualView;

	public IMauiContext? MauiContext => _mauiContext;

	public void ClearContext()
	{
		_mauiContext = null;
	}

	public void SetMauiContext(IMauiContext mauiContext)
	{
		_mauiContext = mauiContext;
	}

	public void SetVirtualView(IElement view)
	{
		_virtualView = (IView)view;

		if (view is Element element && element.Handler != this)
			element.Handler = this;
	}

	public void UpdateValue(string property)
	{
	}

	public void Invoke(string command, object? args = null)
	{
	}

	public void DisconnectHandler()
	{
		var view = _virtualView;
		_virtualView = null;

		if (view is Element element && element.Handler == this)
			element.Handler = null;
	}

	public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

	public void PlatformArrange(Rect frame)
	{
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ChildrenPerTabbedPage,
	int PayloadKiBPerOwner,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedChildPages == Cycles * ChildrenPerTabbedPage &&
		Control.HandlerLocalChildSubscriptions == 0 &&
		Control.AliveTabbedPages <= 1 &&
		Control.AliveHandlers <= 1 &&
		Control.AliveOwnerPayloads <= 1 &&
		Control.AlivePayloadBuffers <= 1 &&
		Control.AliveChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.RetainedChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.HandlerLocalChildSubscriptions == Cycles * ChildrenPerTabbedPage &&
		Current.AliveTabbedPages == Cycles &&
		Current.AliveOwnerPayloads == Cycles &&
		Current.AlivePayloadBuffers == Cycles &&
		Current.AliveChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.EstimatedOwnerPayloadBytes >= Cycles * PayloadKiBPerOwner * 1024L * 0.95;

	public string ToText()
	{
		var currentMiB = Current.EstimatedOwnerPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedOwnerPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"MacCatalystTabbedPageHandlerItemsSourceResetOwnerRetentionRepro",
			$"Cycles: {Cycles}",
			$"Generated children per ItemsSource reset retained in both runs: {ChildrenPerTabbedPage}",
			$"Payload per TabbedPage owner: {PayloadKiBPerOwner} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control, ChildrenPerTabbedPage),
			string.Empty,
			Format(Current, ChildrenPerTabbedPage),
			string.Empty,
			$"Control estimated retained owner payload: {controlMiB:N1} MiB",
			$"Current estimated retained owner payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result, int childrenPerTabbedPage)
	{
		var payloadMiB = result.EstimatedOwnerPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  app-retained child pages: {result.RetainedChildPages}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  handler-local child page subscriptions: {result.HandlerLocalChildSubscriptions}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  alive TabbedPage owners: {result.AliveTabbedPages}/{result.TrackedCycles}",
			$"  alive stub handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive child pages: {result.AliveChildPages}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  alive owner payloads: {result.AliveOwnerPayloads}/{result.TrackedCycles}",
			$"  alive owner payload byte arrays: {result.AlivePayloadBuffers}/{result.TrackedCycles}",
			$"  estimated retained owner payload bytes: {result.EstimatedOwnerPayloadBytes:N0}",
			$"  estimated retained owner payload MiB: {payloadMiB:N1}");
	}
}
