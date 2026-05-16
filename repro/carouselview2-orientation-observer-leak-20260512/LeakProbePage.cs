using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

#if IOS || MACCATALYST
using Foundation;
using UIKit;
#endif

namespace CarouselView2OrientationObserverLeakRepro;

public class LeakProbePage : ContentPage
{
	const string ResultFileName = "carouselview2-orientation-observer-leak-result.txt";
	readonly Label _statusLabel;
	bool _started;

	public LeakProbePage()
	{
		Title = "CarouselView2 observer leak repro";

		_statusLabel = new Label
		{
			Text = "Preparing repro...",
			HorizontalTextAlignment = TextAlignment.Center,
			VerticalTextAlignment = TextAlignment.Center
		};

		Content = new Grid
		{
			Padding = 24,
			Children = { _statusLabel }
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;

		try
		{
			await RunAsync();
		}
		catch (Exception ex)
		{
			await WriteResultAndExitAsync(
				"CarouselView2 orientation observer leak repro failed before producing a result." + Environment.NewLine +
				ex,
				exitCode: 3);
		}
	}

	async Task RunAsync()
	{
		_statusLabel.Text = "Running CarouselView2 scenario...";

		var carousel = await CreateAttachDetachScenarioAsync(
			"carousel",
			() => new CarouselView
			{
				ItemsSource = Enumerable.Range(1, 8).Select(static i => $"Carousel item {i}").ToArray(),
				ItemTemplate = new DataTemplate(static () => new Grid
				{
					HeightRequest = 120,
					Children =
					{
						new Label
						{
							HorizontalTextAlignment = TextAlignment.Center,
							VerticalTextAlignment = TextAlignment.Center
						}
					}
				})
			});

		_statusLabel.Text = "Running CollectionView control scenario...";

		var collection = await CreateAttachDetachScenarioAsync(
			"collection-control",
			() => new CollectionView
			{
				ItemsSource = Enumerable.Range(1, 8).Select(static i => $"Collection item {i}").ToArray(),
				ItemTemplate = new DataTemplate(static () => new Grid
				{
					HeightRequest = 44,
					Children =
					{
						new Label
						{
							HorizontalTextAlignment = TextAlignment.Center,
							VerticalTextAlignment = TextAlignment.Center
						}
					}
				})
			});

#if IOS || MACCATALYST
		NSNotificationCenter.DefaultCenter.PostNotificationName(UIDevice.OrientationDidChangeNotification, UIDevice.CurrentDevice);
#endif

		await WaitForCollectionAsync(carousel.AllReferences.Concat(collection.AllReferences).ToArray());

		var result = BuildResult(carousel, collection);
		var leakProved = carousel.HasOnlyControllerAlive && collection.IsFullyCollected;
		var exitCode = leakProved ? 0 : 1;

		await WriteResultAndExitAsync(result, exitCode);
	}

	async Task<ScenarioResult> CreateAttachDetachScenarioAsync(string name, Func<View> createView)
	{
		var page = new ContentPage
		{
			Title = name
		};

		var view = createView();
		page.Content = view;

		await Navigation.PushAsync(page, animated: false);
		await WaitForHandlerAsync(view);
		await Task.Delay(500);

		var handler = view.Handler ?? throw new InvalidOperationException($"{name}: handler was not created.");
		var controller = GetController(handler) ?? throw new InvalidOperationException($"{name}: controller was not found.");
		var platformView = handler.PlatformView ?? throw new InvalidOperationException($"{name}: platform view was not created.");

		var result = new ScenarioResult(
			name,
			view.GetType().FullName ?? view.GetType().Name,
			handler.GetType().FullName ?? handler.GetType().Name,
			controller.GetType().FullName ?? controller.GetType().Name,
			platformView.GetType().FullName ?? platformView.GetType().Name,
			"not detached yet",
			new WeakReference(view),
			new WeakReference(handler),
			new WeakReference(controller),
			new WeakReference(platformView));

		await Navigation.PopAsync(animated: false);
		await Task.Delay(500);

		result = result with
		{
			PostDetachState = GetPostDetachState(controller)
		};

		page.Content = null;
		handler.DisconnectHandler();

		return result;
	}

	static async Task WaitForHandlerAsync(View view)
	{
		for (var i = 0; i < 50; i++)
		{
			if (view.Handler?.PlatformView is not null && GetController(view.Handler) is not null)
				return;

			await Task.Delay(100);
		}

		throw new TimeoutException($"Timed out waiting for a handler for {view.GetType().Name}.");
	}

	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The repro intentionally reflects the handler's non-public Controller property to track the suspected leaked object.")]
	static object? GetController(IElementHandler handler)
	{
		for (var type = handler.GetType(); type is not null; type = type.BaseType)
		{
			var property = type.GetProperty("Controller", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property is not null)
				return property.GetValue(handler);
		}

		return null;
	}

	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The repro intentionally reflects non-public state to show the detach cleanup path ran.")]
	static string GetPostDetachState(object controller)
	{
		var type = controller.GetType();
		var values = new List<string>();

		var initialPositionSet = type.GetProperty("InitialPositionSet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (initialPositionSet is not null)
			values.Add($"InitialPositionSet={initialPositionSet.GetValue(controller)}");

		var loopManager = type.GetField("_carouselViewLoopManager", BindingFlags.Instance | BindingFlags.NonPublic);
		if (loopManager is not null)
			values.Add($"LoopManagerCleared={loopManager.GetValue(controller) is null}");

		var oldViews = type.GetField("_oldViews", BindingFlags.Instance | BindingFlags.NonPublic);
		if (oldViews is not null)
			values.Add($"OldViewsCleared={oldViews.GetValue(controller) is null}");

		return values.Count == 0 ? "not applicable" : string.Join(", ", values);
	}

	static async Task WaitForCollectionAsync(params WeakReference[] references)
	{
		for (var i = 0; i < 16; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();

			if (references.All(static reference => !reference.IsAlive))
				return;

			await Task.Delay(150);
		}
	}

	static string BuildResult(ScenarioResult carousel, ScenarioResult collection)
	{
		var leakProved = carousel.HasOnlyControllerAlive && collection.IsFullyCollected;
		var lines = new List<string>
		{
			"CarouselView2 orientation observer leak repro",
			$"Timestamp: {DateTimeOffset.Now:O}",
			$"Leak proved: {leakProved}",
			"",
			"Expected signal:",
			"- carousel controller remains alive after detach and forced GC",
			"- collection control controller collects under the same attach/detach path",
			"",
			carousel.ToReport(),
			"",
			collection.ToReport(),
			"",
			"Suspect code:",
			"src/Controls/src/Core/Handlers/Items2/iOS/CarouselViewController2.cs subscribes with NSNotificationCenter.DefaultCenter.AddObserver(UIDevice.OrientationDidChangeNotification, DeviceOrientationChanged) but does not keep the returned NSObject token. TearDown later calls RemoveObserver(this, UIDevice.OrientationDidChangeNotification, null), which does not remove the block observer token."
		};

		return string.Join(Environment.NewLine, lines);
	}

	async Task WriteResultAndExitAsync(string result, int exitCode)
	{
		var path = Path.Combine(FileSystem.AppDataDirectory, ResultFileName);
		File.WriteAllText(path, result);

		_statusLabel.Text = result + Environment.NewLine + Environment.NewLine + $"Result file: {path}";

		await Task.Delay(1000);
		Environment.Exit(exitCode);
	}

	readonly record struct ScenarioResult(
		string Name,
		string ViewType,
		string HandlerType,
		string ControllerType,
		string PlatformViewType,
		string PostDetachState,
		WeakReference View,
		WeakReference Handler,
		WeakReference Controller,
		WeakReference PlatformView)
	{
		public IEnumerable<WeakReference> AllReferences
		{
			get
			{
				yield return View;
				yield return Handler;
				yield return Controller;
				yield return PlatformView;
			}
		}

		public bool HasOnlyControllerAlive =>
			!View.IsAlive &&
			!Handler.IsAlive &&
			Controller.IsAlive &&
			!PlatformView.IsAlive;

		public bool IsFullyCollected =>
			!View.IsAlive &&
			!Handler.IsAlive &&
			!Controller.IsAlive &&
			!PlatformView.IsAlive;

		public string ToReport()
		{
			return string.Join(Environment.NewLine, new[]
			{
				$"Scenario: {Name}",
				$"ViewType: {ViewType}",
				$"HandlerType: {HandlerType}",
				$"ControllerType: {ControllerType}",
				$"PlatformViewType: {PlatformViewType}",
				$"PostDetachState: {PostDetachState}",
				$"ViewAlive: {View.IsAlive}",
				$"HandlerAlive: {Handler.IsAlive}",
				$"ControllerAlive: {Controller.IsAlive}",
				$"PlatformViewAlive: {PlatformView.IsAlive}"
			});
		}
	}
}
