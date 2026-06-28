#nullable enable

using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using ObjCRuntime;
using UIKit;

namespace IosPageTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerTitle = 256;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedNativeViewController>> RetainedNativeViewControllers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-page-title-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS page title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear UIViewController.Title before retaining native page controller",
			context,
			clearNativeTitle: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: PageHandler leaves UIViewController.Title assigned",
			context,
			clearNativeTitle: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeViewControllers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerTitle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeTitle)
	{
		var retainedControllers = new List<RetainedNativeViewController>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeTitle);
			retainedControllers.Add(cycleResult.RetainedViewController);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeViewControllers.Add(retainedControllers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedControllers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeTitle)
	{
		var content = new Label
		{
			Text = $"Content for generated workflow page {cycle:0000}"
		};

		var page = new ContentPage
		{
			Title = CreateLargePageTitle(cycle),
			Content = content
		};

		var handler = page.ToHandler(context);

		if (handler is not IPlatformViewHandler platformViewHandler || platformViewHandler.ViewController is not UIViewController viewController)
			throw new InvalidOperationException("ContentPage handler did not expose a native UIViewController.");

		if (EstimateTitleBytes(viewController) < PayloadBytesPerTitle * 0.95)
			throw new InvalidOperationException("PageHandler did not assign the expected payload-sized UIViewController.Title.");

		if (clearNativeTitle)
			viewController.Title = string.Empty;

		var retainedViewController = RetainNativeViewController(viewController);
		var tracked = TrackedCycle.Create(cycle, page, content, handler);

		handler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(retainedViewController, tracked);
	}

	static string CreateLargePageTitle(int cycle)
	{
		var header = $"Generated workspace page {cycle:0000}. ";
		var sentence = "Offline customer history, compliance packet, workflow transcript, and review queue summary. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static long EstimateAssignedTitleBytes(UIViewController viewController)
	{
		return Math.Min(EstimateTitleBytes(viewController), PayloadBytesPerTitle);
	}

	static long EstimateTitleBytes(UIViewController viewController)
	{
		return string.IsNullOrEmpty(viewController.Title) ? 0 : viewController.Title.Length * 2L;
	}

	static RetainedNativeViewController RetainNativeViewController(UIViewController viewController)
	{
		var handle = viewController.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UIViewController with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeViewController(retained);
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

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed record CycleResult(RetainedNativeViewController RetainedViewController, TrackedCycle Tracked);

	internal sealed class RetainedNativeViewController
	{
		public RetainedNativeViewController(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UIViewController? TryGetViewController()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UIViewController>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<ContentPage> Page,
		WeakReference<Label> Content,
		WeakReference<IElementHandler> Handler)
	{
		public static TrackedCycle Create(
			int cycle,
			ContentPage page,
			Label content,
			IElementHandler handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<ContentPage>(page),
				new WeakReference<Label>(content),
				new WeakReference<IElementHandler>(handler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeViewControllers,
		int ViewControllersWithAssignedTitles,
		long EstimatedAssignedTitleBytes,
		int AlivePages,
		int AliveContent,
		int AliveHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeViewController> retainedViewControllers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeViewControllers = 0;
			var viewControllersWithAssignedTitles = 0;
			long estimatedAssignedTitleBytes = 0;

			foreach (var retainedViewController in retainedViewControllers)
			{
				var viewController = retainedViewController.TryGetViewController();
				if (viewController is null)
					continue;

				retainedNativeViewControllers++;
				if (EstimateTitleBytes(viewController) >= PayloadBytesPerTitle * 0.95)
					viewControllersWithAssignedTitles++;

				estimatedAssignedTitleBytes += EstimateAssignedTitleBytes(viewController);
			}

			var alivePages = 0;
			var aliveContent = 0;
			var aliveHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Page.TryGetTarget(out _))
					alivePages++;

				if (cycle.Content.TryGetTarget(out _))
					aliveContent++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeViewControllers,
				viewControllersWithAssignedTitles,
				estimatedAssignedTitleBytes,
				alivePages,
				aliveContent,
				aliveHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerTitle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeViewControllers == Cycles &&
		Control.ViewControllersWithAssignedTitles == 0 &&
		Current.RetainedNativeViewControllers == Cycles &&
		Current.ViewControllersWithAssignedTitles == Cycles &&
		Current.EstimatedAssignedTitleBytes >= Cycles * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AlivePages <= 1 &&
		Current.AliveContent <= 1 &&
		Current.AliveHandlers <= 1;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosPageTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per page title: {PayloadKiBPerTitle} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native title payload: {controlMiB:N1} MiB",
			$"Current estimated retained native title payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTitleMiB = result.EstimatedAssignedTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native view controllers: {result.RetainedNativeViewControllers}/{result.TrackedCycles}",
			$"  view controllers with assigned titles: {result.ViewControllersWithAssignedTitles}/{result.TrackedCycles}",
			$"  estimated retained native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native title MiB: {nativeTitleMiB:N1}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive content views: {result.AliveContent}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}");
	}
}
