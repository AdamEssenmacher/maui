using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.Widget;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using AView = Android.Views.View;
using MauiView = Microsoft.Maui.Controls.View;

namespace AndroidShellFlyoutViewHolderRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AlivePayloads,
	int AliveTemplateViews,
	int AliveViewHolders,
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
		Control.AliveTemplateViews == 0 &&
		Control.AliveViewHolders == 0 &&
		Current.AlivePayloads == Attempts &&
		Current.AliveTemplateViews == Attempts &&
		Current.AliveViewHolders == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellFlyoutViewHolderRetentionLeakRepro",
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
			$"  retained Shell flyout items: {stats.Attempts}",
			$"  template views alive after full GC: {stats.AliveTemplateViews}/{stats.Attempts}",
			$"  view holders alive after full GC: {stats.AliveViewHolders}/{stats.Attempts}",
			$"  template payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
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

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear holder Element and dispose active holder",
			cleanupHolder: true);

		var current = await RunScenarioAsync(
			"current: adapter disposal leaves active flyout holders uncleared",
			cleanupHolder: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool cleanupHolder)
	{
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);
		var templateViewRefs = new List<WeakReference<MauiView>>(Attempts);
		var holderRefs = new List<WeakReference<ShellFlyoutRecyclerAdapter.ElementViewHolder>>(Attempts);
		var retainedFlyoutItems = new List<ShellContent>(Attempts);
		var shell = new Shell();
		var context = Android.App.Application.Context;

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedFlyoutHolder(
				context,
				shell,
				cleanupHolder,
				retainedFlyoutItems,
				payloadRefs,
				templateViewRefs,
				holderRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(shell);
		GC.KeepAlive(retainedFlyoutItems);

		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveTemplateViews = templateViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHolders = holderRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			alivePayloads,
			aliveTemplateViews,
			aliveHolders,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateDisposedFlyoutHolder(
		Android.Content.Context context,
		Shell shell,
		bool cleanupHolder,
		List<ShellContent> retainedFlyoutItems,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<MauiView>> templateViewRefs,
		List<WeakReference<ShellFlyoutRecyclerAdapter.ElementViewHolder>> holderRefs,
		int index)
	{
		var shellItem = new ShellContent
		{
			Title = $"Retained flyout item {index}"
		};
		retainedFlyoutItems.Add(shellItem);

		var payload = new Payload(index, PayloadBytes);
		payloadRefs.Add(new WeakReference<Payload>(payload));

		var templateView = new Grid
		{
			HeightRequest = 48,
			BackgroundColor = Colors.LightSkyBlue,
			Resources =
			{
				["Payload"] = payload
			},
			Children =
			{
				new Label
				{
					Text = $"Flyout payload {index}",
					VerticalOptions = LayoutOptions.Center,
					HorizontalOptions = LayoutOptions.Center
				}
			}
		};
		templateViewRefs.Add(new WeakReference<MauiView>(templateView));

		var itemView = new LinearLayout(context)
		{
			Orientation = Orientation.Vertical
		};
		var bar = new AView(context);

		var holder = new ShellFlyoutRecyclerAdapter.ElementViewHolder(
			templateView,
			itemView,
			bar,
			static _ => { },
			shell);
		holderRefs.Add(new WeakReference<ShellFlyoutRecyclerAdapter.ElementViewHolder>(holder));

		holder.Element = shellItem;

		if (cleanupHolder)
		{
			holder.Element = null;
			holder.Dispose();
		}
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
