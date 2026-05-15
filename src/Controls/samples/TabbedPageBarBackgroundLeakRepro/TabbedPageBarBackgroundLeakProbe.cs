using System.Reflection;

namespace Maui.Controls.Sample.TabbedPageBarBackgroundLeakRepro;

public static class TabbedPageBarBackgroundLeakProbe
{
	public const string LeakBrushKey = "SharedLeakyTabbedPageBarBackground";
	public const string ControlBrushKey = "SharedControlTabbedPageBarBackground";
	public const string LeakStyleKey = "LeakyTabbedPageStyle";
	public const string ControlStyleKey = "ControlTabbedPageStyle";

	static readonly FieldInfo s_gradientInvalidatedField =
		typeof(GradientBrush).GetField("InvalidateGradientBrushRequested", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(GradientBrush).FullName, "InvalidateGradientBrushRequested");

	public static LinearGradientBrush LeakBrush => GetResource<LinearGradientBrush>(LeakBrushKey);

	public static LinearGradientBrush ControlBrush => GetResource<LinearGradientBrush>(ControlBrushKey);

	public static Style LeakStyle => GetResource<Style>(LeakStyleKey);

	public static Style ControlStyle => GetResource<Style>(ControlStyleKey);

	public static async Task<ProbeRun> CreateRunAsync(
		ContentPage runnerPage,
		string name,
		Style style,
		LinearGradientBrush brush,
		bool clearBarBackgroundBeforeDisconnect)
	{
		ClearBrushState(brush);

		var window = Application.Current?.Windows.FirstOrDefault()
			?? throw new InvalidOperationException("No active Window.");

		var tabbedPage = CreateTabbedPage(style, brush, name);
		window.Page = tabbedPage;

		await WaitForHandlerAsync(tabbedPage);
		tabbedPage.Handler?.UpdateValue(nameof(TabbedPage.BarBackground));
		await Task.Delay(500);

		var subscriberTarget = GetSubscriberTarget(brush);
		var previousChildPage = tabbedPage.CurrentPage;
		var run = new ProbeRun(
			name,
			brush,
			tabbedPage,
			subscriberTarget,
			previousChildPage);

		if (clearBarBackgroundBeforeDisconnect)
		{
			tabbedPage.BarBackground = null;
			tabbedPage.Handler?.UpdateValue(nameof(TabbedPage.BarBackground));
			await Task.Delay(150);
		}

		window.Page = runnerPage;
		await WaitForHandlerAsync(runnerPage);
		await Task.Delay(700);

		return run;
	}

	public static async Task<ProbeSnapshot> CollectAsync(ProbeRun run, int cycles = 8)
	{
		for (var n = 0; n < cycles; n++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			await Task.Delay(80);
		}

		return run.Snapshot();
	}

	static TabbedPage CreateTabbedPage(Style style, LinearGradientBrush expectedBrush, string name)
	{
		var firstPage = new ContentPage
		{
			Title = $"{name} A",
			Content = new Label
			{
				Text = "This page should be collectible after Window.Page is replaced.",
				Margin = 24
			}
		};

		var secondPage = new ContentPage
		{
			Title = $"{name} B",
			Content = new Label
			{
				Text = "Second tab",
				Margin = 24
			}
		};

		var tabbedPage = new TabbedPage
		{
			Title = name,
			Style = style,
			Children =
			{
				firstPage,
				secondPage
			}
		};

		if (!ReferenceEquals(tabbedPage.BarBackground, expectedBrush))
			throw new InvalidOperationException("TabbedPage style did not apply the expected app resource brush.");

		return tabbedPage;
	}

	static async Task WaitForHandlerAsync(Element element)
	{
		for (var n = 0; n < 30 && element.Handler is null; n++)
			await Task.Delay(100);
	}

	static T GetResource<T>(string key)
	{
		var resources = Application.Current?.Resources
			?? throw new InvalidOperationException("Application resources are not available.");

		return resources.TryGetValue(key, out var value) && value is T typed
			? typed
			: throw new InvalidOperationException($"Resource '{key}' was not found or was not a {typeof(T).Name}.");
	}

	static void ClearBrushState(LinearGradientBrush brush)
	{
		s_gradientInvalidatedField.SetValue(brush, null);
		brush.Parent = null;
	}

	static int GetSubscriberCount(GradientBrush brush) =>
		(s_gradientInvalidatedField.GetValue(brush) as MulticastDelegate)?.GetInvocationList().Length ?? 0;

	static object? GetSubscriberTarget(GradientBrush brush) =>
		(s_gradientInvalidatedField.GetValue(brush) as MulticastDelegate)
			?.GetInvocationList()
			.Select(d => d.Target)
			.FirstOrDefault(target => target is not null);

	static string GetSubscriberTargets(GradientBrush brush)
	{
		var subscribers = s_gradientInvalidatedField.GetValue(brush) as MulticastDelegate;
		if (subscribers is null)
			return "<none>";

		return string.Join(", ",
			subscribers
				.GetInvocationList()
				.Select(d => d.Target?.GetType().FullName ?? "<static>")
				.Distinct()
				.OrderBy(s => s));
	}

	public static bool HasLiveParent(GradientBrush brush) => brush.Parent is not null;

	public static int SubscriberCount(GradientBrush brush) => GetSubscriberCount(brush);

	public static string SubscriberTargets(GradientBrush brush) => GetSubscriberTargets(brush);
}

public sealed class ProbeRun
{
	readonly LinearGradientBrush _brush;
	readonly WeakReference _previousChildPageReference;
	readonly WeakReference? _subscriberTargetReference;
	readonly WeakReference _tabbedPageReference;

	public ProbeRun(
		string name,
		LinearGradientBrush brush,
		TabbedPage tabbedPage,
		object? subscriberTarget,
		Page? previousChildPage)
	{
		Name = name;
		_brush = brush;
		_tabbedPageReference = new WeakReference(tabbedPage);
		SubscriberTargetType = subscriberTarget?.GetType().FullName ?? "<none>";
		_subscriberTargetReference = subscriberTarget is null ? null : new WeakReference(subscriberTarget);
		_previousChildPageReference = new WeakReference(previousChildPage);
	}

	public string Name { get; }

	public string SubscriberTargetType { get; }

	public ProbeSnapshot Snapshot()
	{
		var subscriberCount = TabbedPageBarBackgroundLeakProbe.SubscriberCount(_brush);

		return new ProbeSnapshot(
			Name,
			subscriberCount,
			TabbedPageBarBackgroundLeakProbe.SubscriberTargets(_brush),
			TabbedPageBarBackgroundLeakProbe.HasLiveParent(_brush),
			SubscriberTargetType,
			_subscriberTargetReference?.IsAlive ?? false,
			_previousChildPageReference.IsAlive,
			_tabbedPageReference.IsAlive);
	}
}

public readonly record struct ProbeSnapshot(
	string Name,
	int BrushSubscriberCount,
	string BrushSubscriberTargets,
	bool BrushParentAlive,
	string SubscriberTargetType,
	bool SubscriberTargetAlive,
	bool PreviousChildPageAlive,
	bool TabbedPageAlive)
{
	public string Verdict
	{
		get
		{
			if (BrushSubscriberCount == 0 && !BrushParentAlive)
				return "CONTROL PASSED: brush was detached before disconnect";

			if (BrushSubscriberCount > 0 && SubscriberTargetAlive)
				return "LEAK REPRODUCED: app resource brush still has a live renderer/manager subscriber";

			return "NO LEAK OBSERVED";
		}
	}
}
