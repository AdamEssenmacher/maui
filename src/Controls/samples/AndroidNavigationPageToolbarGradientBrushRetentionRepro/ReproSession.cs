#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace AndroidNavigationPageToolbarGradientBrushRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int BrushSubscriberCount,
	string BrushSubscriberTargets,
	bool BrushParentAlive,
	int AliveToolbars,
	int AliveNavigationPages,
	int AliveRootPages,
	int AliveViewModels,
	int AlivePayloads,
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
		Control.BrushSubscriberCount == 0 &&
		Control.AliveToolbars == 0 &&
		Control.AliveNavigationPages == 0 &&
		Control.AliveRootPages == 0 &&
		Control.AliveViewModels == 0 &&
		Control.AlivePayloads == 0 &&
		Current.BrushSubscriberCount == Attempts &&
		Current.AliveToolbars == Attempts &&
		Current.AliveNavigationPages == Attempts &&
		Current.AliveRootPages == Attempts &&
		Current.AliveViewModels == Attempts &&
		Current.AlivePayloads == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidNavigationPageToolbarGradientBrushRetentionRepro",
			LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
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
			$"  brush subscribers after full GC: {stats.BrushSubscriberCount}",
			$"  brush subscriber targets: {stats.BrushSubscriberTargets}",
			$"  brush parent alive: {(stats.BrushParentAlive ? "yes" : "no")}",
			$"  toolbars alive after full GC: {stats.AliveToolbars}/{stats.Attempts}",
			$"  NavigationPages alive after full GC: {stats.AliveNavigationPages}/{stats.Attempts}",
			$"  root pages alive after full GC: {stats.AliveRootPages}/{stats.Attempts}",
			$"  view models alive after full GC: {stats.AliveViewModels}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
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
	const int Attempts = 64;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo GradientInvalidatedField =
		typeof(GradientBrush).GetField("InvalidateGradientBrushRequested", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(GradientBrush).FullName, "InvalidateGradientBrushRequested");

	static readonly MethodInfo FindMyToolbarMethod =
		typeof(NavigationPage).GetMethod("FindMyToolbar", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(NavigationPage), "FindMyToolbar");

	public static async Task<ReproReport> RunAsync(ContentPage runnerPage)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var controlBrush = CreateSharedBrush();
		var currentBrush = CreateSharedBrush();

		var control = await RunScenarioAsync(
			runnerPage,
			"control: clear NavigationPage toolbar BarBackground before Window.Page replacement",
			controlBrush,
			clearToolbarBrushBeforeReplacement: true);

		var current = await RunScenarioAsync(
			runnerPage,
			"current: shared GradientBrush remains subscribed to each removed NavigationPageToolbar",
			currentBrush,
			clearToolbarBrushBeforeReplacement: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		ContentPage runnerPage,
		string name,
		LinearGradientBrush brush,
		bool clearToolbarBrushBeforeReplacement)
	{
		ClearBrushState(brush);

		var window = Application.Current?.Windows.FirstOrDefault()
			?? throw new InvalidOperationException("No active Window.");

		var toolbarRefs = new List<WeakReference<Toolbar>>(Attempts);
		var navigationPageRefs = new List<WeakReference<NavigationPage>>(Attempts);
		var rootPageRefs = new List<WeakReference<ContentPage>>(Attempts);
		var viewModelRefs = new List<WeakReference<PageViewModel>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			var beforeSubscribers = GetSubscriberCount(brush);
			var payload = new Payload(i, PayloadBytes);
			var viewModel = new PageViewModel(i, payload);
			var rootPage = CreateRootPage(viewModel);
			var navigationPage = new NavigationPage(rootPage)
			{
				Title = $"Retained navigation page {i}",
				BarBackground = brush,
				BarTextColor = Colors.White
			};

			navigationPageRefs.Add(new WeakReference<NavigationPage>(navigationPage));
			rootPageRefs.Add(new WeakReference<ContentPage>(rootPage));
			viewModelRefs.Add(new WeakReference<PageViewModel>(viewModel));
			payloadRefs.Add(new WeakReference<Payload>(payload));

			window.Page = navigationPage;
			await WaitForHandlerAsync(navigationPage);
			await WaitForHandlerAsync(rootPage);
			await WaitForToolbarSubscriptionAsync(navigationPage, brush, beforeSubscribers + 1);

			var toolbar = FindToolbar(navigationPage);
			if (toolbar is not null)
				toolbarRefs.Add(new WeakReference<Toolbar>(toolbar));

			if (clearToolbarBrushBeforeReplacement)
			{
				navigationPage.BarBackground = null;
				if (toolbar is not null)
				{
					toolbar.BarBackground = null;
					toolbar.Handler?.UpdateValue(nameof(Toolbar.BarBackground));
				}

				await WaitForSubscriberCountAsync(brush, beforeSubscribers);
			}

			window.Page = runnerPage;
			await WaitForHandlerAsync(runnerPage);

			if (i % 8 == 0)
				await Task.Delay(100);
		}

		await Task.Delay(800);
		ForceFullGc();

		return new RunStats(
			name,
			Attempts,
			GetSubscriberCount(brush),
			GetSubscriberTargets(brush),
			brush.Parent is not null,
			toolbarRefs.Count(static wr => wr.TryGetTarget(out _)),
			navigationPageRefs.Count(static wr => wr.TryGetTarget(out _)),
			rootPageRefs.Count(static wr => wr.TryGetTarget(out _)),
			viewModelRefs.Count(static wr => wr.TryGetTarget(out _)),
			payloadRefs.Count(static wr => wr.TryGetTarget(out _)),
			(long)payloadRefs.Count(static wr => wr.TryGetTarget(out _)) * PayloadBytes);
	}

	static ContentPage CreateRootPage(PageViewModel viewModel)
	{
		return new ContentPage
		{
			Title = $"Customer detail {viewModel.Id}",
			BindingContext = viewModel,
			Content = new VerticalStackLayout
			{
				Padding = 24,
				Spacing = 12,
				Children =
				{
					new Label
					{
						Text = $"Customer detail {viewModel.Id}",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold
					},
					new Label
					{
						Text = "Each page view model carries a 1 MiB cached document/image payload."
					}
				}
			}
		};
	}

	static async Task WaitForToolbarSubscriptionAsync(NavigationPage navigationPage, LinearGradientBrush brush, int expectedCount)
	{
		for (var n = 0; n < 30; n++)
		{
			var toolbar = FindToolbar(navigationPage);
			toolbar?.Handler?.UpdateValue(nameof(Toolbar.BarBackground));

			if (GetSubscriberCount(brush) >= expectedCount)
				return;

			await Task.Delay(100);
		}

		throw new InvalidOperationException($"Timed out waiting for NavigationPageToolbar to subscribe to the shared brush. Subscribers: {GetSubscriberCount(brush)}.");
	}

	static async Task WaitForSubscriberCountAsync(LinearGradientBrush brush, int expectedCount)
	{
		for (var n = 0; n < 20; n++)
		{
			if (GetSubscriberCount(brush) == expectedCount)
				return;

			await Task.Delay(50);
		}
	}

	static async Task WaitForHandlerAsync(Element element)
	{
		for (var n = 0; n < 40 && element.Handler is null; n++)
			await Task.Delay(100);
	}

	static Toolbar? FindToolbar(NavigationPage navigationPage)
	{
		return FindMyToolbarMethod.Invoke(navigationPage, null) as Toolbar;
	}

	static LinearGradientBrush CreateSharedBrush()
	{
		return new LinearGradientBrush
		{
			StartPoint = new Point(0, 0),
			EndPoint = new Point(1, 1),
			GradientStops =
			{
				new GradientStop(Colors.DeepSkyBlue, 0),
				new GradientStop(Colors.MediumVioletRed, 1)
			}
		};
	}

	static void ClearBrushState(LinearGradientBrush brush)
	{
		GradientInvalidatedField.SetValue(brush, null);
		brush.Parent = null;
	}

	static int GetSubscriberCount(GradientBrush brush) =>
		(GradientInvalidatedField.GetValue(brush) as MulticastDelegate)?.GetInvocationList().Length ?? 0;

	static string GetSubscriberTargets(GradientBrush brush)
	{
		var subscribers = GradientInvalidatedField.GetValue(brush) as MulticastDelegate;
		if (subscribers is null)
			return "<none>";

		return string.Join(", ",
			subscribers
				.GetInvocationList()
				.Select(static d => d.Target?.GetType().FullName ?? "<static>")
				.GroupBy(static name => name)
				.OrderBy(static group => group.Key)
				.Select(static group => $"{group.Key} x{group.Count()}"));
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

	sealed class PageViewModel
	{
		public PageViewModel(int id, Payload payload)
		{
			Id = id;
			Payload = payload;
		}

		public int Id { get; }

		public Payload Payload { get; }
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
