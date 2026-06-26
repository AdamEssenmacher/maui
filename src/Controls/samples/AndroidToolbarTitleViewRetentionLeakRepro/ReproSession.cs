using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace AndroidToolbarTitleViewRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AlivePayloads,
	int AliveRemovedTitleViews,
	int AliveRemovedTitleHandlers,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AlivePayloads == 0 &&
		Control.AliveRemovedTitleViews == 0 &&
		Current.AlivePayloads == Attempts &&
		Current.AliveRemovedTitleViews == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidToolbarTitleViewRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
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
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained live toolbars: {stats.Attempts}",
			$"  removed title views alive after full GC: {stats.AliveRemovedTitleViews}/{stats.Attempts}",
			$"  removed title handlers alive after full GC: {stats.AliveRemovedTitleHandlers}/{stats.Attempts}",
			$"  title-view payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		EnsureToolbarMapper();

		var mauiContext = Application.Current?.Windows.FirstOrDefault()?.Page?.Handler?.MauiContext
			?? throw new InvalidOperationException("MauiContext is not available.");

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: explicitly disconnect removed title-view handler",
			mauiContext,
			disconnectRemovedTitleView: true);

		var current = await RunScenarioAsync(
			"current: Toolbar.TitleView = null removes container but keeps old handler fields",
			mauiContext,
			disconnectRemovedTitleView: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		string name,
		IMauiContext mauiContext,
		bool disconnectRemovedTitleView)
	{
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);
		var titleViewRefs = new List<WeakReference<View>>(Attempts);
		var titleHandlerRefs = new List<WeakReference<IElementHandler>>(Attempts);
		var retainedToolbars = new List<Microsoft.Maui.Controls.Toolbar>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateLiveToolbarAfterTitleViewRemoval(
				mauiContext,
				disconnectRemovedTitleView,
				payloadRefs,
				titleViewRefs,
				titleHandlerRefs,
				retainedToolbars,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveTitleViews = titleViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveTitleHandlers = titleHandlerRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			alivePayloads,
			aliveTitleViews,
			aliveTitleHandlers,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateLiveToolbarAfterTitleViewRemoval(
		IMauiContext mauiContext,
		bool disconnectRemovedTitleView,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<View>> titleViewRefs,
		List<WeakReference<IElementHandler>> titleHandlerRefs,
		List<Microsoft.Maui.Controls.Toolbar> retainedToolbars,
		int index)
	{
		var page = new ContentPage { Title = $"Page {index}" };
		var toolbar = new Microsoft.Maui.Controls.Toolbar(page)
		{
			IsVisible = true,
			Title = $"Toolbar {index}"
		};

		var toolbarHandler = new ToolbarHandler();
		toolbarHandler.SetMauiContext(mauiContext);
		toolbarHandler.SetVirtualView(toolbar);

		var payload = new Payload(index, PayloadBytes);
		payloadRefs.Add(new WeakReference<Payload>(payload));

		var titleView = new Grid
		{
			BindingContext = payload,
			WidthRequest = 240,
			HeightRequest = 48,
			BackgroundColor = Colors.CornflowerBlue,
			Children =
			{
				new Label
				{
					Text = $"Title payload {index}",
					TextColor = Colors.White,
					VerticalOptions = LayoutOptions.Center,
					HorizontalOptions = LayoutOptions.Center
				}
			}
		};
		titleViewRefs.Add(new WeakReference<View>(titleView));

		toolbar.TitleView = titleView;

		var titleHandler = titleView.Handler
			?? throw new InvalidOperationException("TitleView handler was not created.");
		titleHandlerRefs.Add(new WeakReference<IElementHandler>(titleHandler));

		toolbar.TitleView = null;

		if (disconnectRemovedTitleView)
		{
			ClearRetainedTitleViewState(toolbar);
			titleHandler.DisconnectHandler();
		}

		retainedToolbars.Add(toolbar);
	}

	static void ClearRetainedTitleViewState(Microsoft.Maui.Controls.Toolbar toolbar)
	{
		var toolbarType = typeof(Microsoft.Maui.Controls.Toolbar);
		var titleViewHandlerField = toolbarType.GetField("_platformTitleViewHandler", BindingFlags.Instance | BindingFlags.NonPublic);
		var platformTitleViewField = toolbarType.GetField("_platformTitleView", BindingFlags.Instance | BindingFlags.NonPublic);

		if (platformTitleViewField?.GetValue(toolbar) is object container)
		{
			var childProperty = container.GetType().GetProperty("Child", BindingFlags.Instance | BindingFlags.Public);
			childProperty?.SetValue(container, null);
		}

		titleViewHandlerField?.SetValue(toolbar, null);
	}

	static void EnsureToolbarMapper()
	{
		var method = typeof(Microsoft.Maui.Controls.Toolbar).GetMethod(
			"RemapForControls",
			BindingFlags.Static | BindingFlags.NonPublic);
		method?.Invoke(null, null);
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
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id % 251);
			Bytes[^1] = (byte)((id + 1) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}
