#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Primitives;

namespace AndroidStackNavigationOnResumeRequestRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveManagers,
	int DelayedRequestsStillAssigned,
	int AliveNavigationRequests,
	int AlivePayloadPages,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current)
{
	public bool LeakProved =>
		Control.AliveManagers == Attempts &&
		Control.DelayedRequestsStillAssigned == 0 &&
		Control.AliveNavigationRequests == 0 &&
		Control.AlivePayloadPages == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveManagers == Attempts &&
		Current.DelayedRequestsStillAssigned == Attempts &&
		Current.AliveNavigationRequests == Attempts &&
		Current.AlivePayloadPages == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadBuffers == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidStackNavigationOnResumeRequestRetentionLeakRepro",
			$"Disconnected StackNavigationManagers kept alive: {Attempts}",
			$"Payload per pending navigation page: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current));
	}

	string Format(RunStats stats)
	{
		var retainedPayloadBytes = (long)stats.AlivePayloadBuffers * PayloadBytes;
		var totalPayloadBytes = (long)stats.Attempts * PayloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  managers alive after full GC: {stats.AliveManagers}/{stats.Attempts}",
			$"  OnResumeRequestedArgs still assigned: {stats.DelayedRequestsStillAssigned}/{stats.Attempts}",
			$"  NavigationRequest objects alive after full GC: {stats.AliveNavigationRequests}/{stats.Attempts}",
			$"  pending payload pages alive after full GC: {stats.AlivePayloadPages}/{stats.Attempts}",
			$"  payload view models alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadBuffers}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / totalPayloadBytes:0.0}%)",
			$"  managed heap before: {FormatBytes(stats.HeapBefore)}",
			$"  managed heap after: {FormatBytes(stats.HeapAfter)}",
			$"  managed heap delta: {FormatBytes(stats.HeapAfter - stats.HeapBefore)}");
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
	const int PayloadBytes = 1024 * 1024;

	static readonly List<StackNavigationManager> RetainedManagers = new();

	static readonly FieldInfo ActiveRequestedArgsField =
		typeof(StackNavigationManager).GetField("<ActiveRequestedArgs>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(StackNavigationManager).FullName, "<ActiveRequestedArgs>k__BackingField");

	static readonly FieldInfo OnResumeRequestedArgsField =
		typeof(StackNavigationManager).GetField("<OnResumeRequestedArgs>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(StackNavigationManager).FullName, "<OnResumeRequestedArgs>k__BackingField");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		RetainedManagers.Clear();

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear OnResumeRequestedArgs during disconnect cleanup",
			clearDelayedRequestAfterDisconnect: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnected manager keeps OnResumeRequestedArgs",
			clearDelayedRequestAfterDisconnect: false);

		GC.KeepAlive(RetainedManagers);
		return new ReproReport(Attempts, PayloadBytes, control, current);
	}

	static async Task<RunStats> RunScenarioAsync(
		IMauiContext mauiContext,
		string name,
		bool clearDelayedRequestAfterDisconnect)
	{
		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedStartIndex = RetainedManagers.Count;
		var managerRefs = new List<WeakReference<StackNavigationManager>>(Attempts);
		var requestRefs = new List<WeakReference<NavigationRequest>>(Attempts);
		var pageRefs = new List<WeakReference<PayloadView>>(Attempts);
		var payloadRefs = new List<WeakReference<PayloadViewModel>>(Attempts);
		var bufferRefs = new List<WeakReference<byte[]>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedManagerWithDelayedRequest(
				mauiContext,
				clearDelayedRequestAfterDisconnect,
				managerRefs,
				requestRefs,
				pageRefs,
				payloadRefs,
				bufferRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var delayedRequestsStillAssigned = RetainedManagers
			.Skip(retainedStartIndex)
			.Count(static manager => OnResumeRequestedArgsField.GetValue(manager) is not null);

		GC.KeepAlive(RetainedManagers);

		return new RunStats(
			name,
			Attempts,
			CountAlive(managerRefs),
			delayedRequestsStillAssigned,
			CountAlive(requestRefs),
			CountAlive(pageRefs),
			CountAlive(payloadRefs),
			CountAlive(bufferRefs),
			heapBefore,
			heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisconnectedManagerWithDelayedRequest(
		IMauiContext mauiContext,
		bool clearDelayedRequestAfterDisconnect,
		List<WeakReference<StackNavigationManager>> managerRefs,
		List<WeakReference<NavigationRequest>> requestRefs,
		List<WeakReference<PayloadView>> pageRefs,
		List<WeakReference<PayloadViewModel>> payloadRefs,
		List<WeakReference<byte[]>> bufferRefs,
		int index)
	{
		var manager = new StackNavigationManager(mauiContext);
		var payload = new PayloadViewModel(index, PayloadBytes);
		var pendingPage = new PayloadView($"Pending customer workspace {index + 1:000}", payload);
		var rootPage = new PayloadView($"Root {index + 1:000}", payload: null);
		var request = new NavigationRequest(new List<IView> { rootPage, pendingPage }, animated: true);

		// This mirrors ApplyNavigationRequest() after FragmentManager.IsStateSaved:
		// ActiveRequestedArgs is set first, then OnResumeRequestedArgs keeps the same request until resume.
		ActiveRequestedArgsField.SetValue(manager, request);
		OnResumeRequestedArgsField.SetValue(manager, request);

		manager.Disconnect();

		if (clearDelayedRequestAfterDisconnect)
			OnResumeRequestedArgsField.SetValue(manager, null);

		RetainedManagers.Add(manager);
		managerRefs.Add(new WeakReference<StackNavigationManager>(manager));
		requestRefs.Add(new WeakReference<NavigationRequest>(request));
		pageRefs.Add(new WeakReference<PayloadView>(pendingPage));
		payloadRefs.Add(new WeakReference<PayloadViewModel>(payload));
		bufferRefs.Add(new WeakReference<byte[]>(payload.Bytes));

		manager = null!;
		request = null!;
		rootPage = null!;
		pendingPage = null!;
		payload = null!;
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Task.Delay(25).Wait();
		}
	}

	sealed class PayloadViewModel
	{
		public PayloadViewModel(int index, int payloadBytes)
		{
			Title = $"Customer workspace {index + 1:000}";
			Description = "Pending navigation target with cached invoices, filters, and draft edits.";
			Bytes = new byte[payloadBytes];

			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)(index + i);

			RecentDocuments = Enumerable.Range(1, 50)
				.Select(document => $"INV-{index + 1:000}-{document:000}")
				.ToArray();
		}

		public string Title { get; }

		public string Description { get; }

		public byte[] Bytes { get; }

		public IReadOnlyList<string> RecentDocuments { get; }
	}

	sealed class PayloadView : IView
	{
		public PayloadView(string automationId, PayloadViewModel? payload)
		{
			AutomationId = automationId;
			Payload = payload;
		}

		public PayloadViewModel? Payload { get; }

		public string AutomationId { get; }

		public FlowDirection FlowDirection => FlowDirection.LeftToRight;

		public LayoutAlignment HorizontalLayoutAlignment => LayoutAlignment.Fill;

		public LayoutAlignment VerticalLayoutAlignment => LayoutAlignment.Fill;

		public Semantics? Semantics => null;

		public IShape? Clip => null;

		public IShadow? Shadow => null;

		public bool IsEnabled => true;

		public bool IsFocused { get; set; }

		public Visibility Visibility => Visibility.Visible;

		public double Opacity => 1;

		public Paint? Background => null;

		public Rect Frame { get; set; }

		public double Width => Frame.Width;

		public double MinimumWidth => 0;

		public double MaximumWidth => double.PositiveInfinity;

		public double Height => Frame.Height;

		public double MinimumHeight => 0;

		public double MaximumHeight => double.PositiveInfinity;

		public Thickness Margin => Thickness.Zero;

		public Size DesiredSize => Size.Zero;

		public int ZIndex => 0;

		public IViewHandler? Handler { get; set; }

		IElementHandler? IElement.Handler
		{
			get => Handler;
			set => Handler = (IViewHandler?)value;
		}

		public IElement? Parent => null;

		public double TranslationX => 0;

		public double TranslationY => 0;

		public double Scale => 1;

		public double ScaleX => 1;

		public double ScaleY => 1;

		public double Rotation => 0;

		public double RotationX => 0;

		public double RotationY => 0;

		public double AnchorX => 0.5;

		public double AnchorY => 0.5;

		public bool InputTransparent => false;

		public Size Arrange(Rect bounds)
		{
			Frame = bounds;
			return new Size(bounds.Width, bounds.Height);
		}

		public Size Measure(double widthConstraint, double heightConstraint) => Size.Zero;

		public void InvalidateMeasure()
		{
		}

		public void InvalidateArrange()
		{
		}

		public bool Focus() => false;

		public void Unfocus()
		{
		}
	}
}
