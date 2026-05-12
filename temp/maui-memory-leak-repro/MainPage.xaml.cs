using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace MauiMemoryLeakRepro;

public partial class MainPage : ContentPage
{
	const string ResultsFileName = "maui-memory-leak-repro-results.txt";

	static IEnumerable<string> ResultsPaths
	{
		get
		{
			yield return System.IO.Path.Combine(System.IO.Path.GetTempPath(), ResultsFileName);
#if MACCATALYST
			yield return System.IO.Path.Combine("/tmp", ResultsFileName);
#endif
		}
	}

	readonly VerticalStackLayout _log = new()
	{
		Spacing = 6
	};

	readonly Grid _host = new()
	{
		HeightRequest = 140,
		WidthRequest = 180,
		BackgroundColor = Colors.Transparent
	};

	readonly Microsoft.Maui.Controls.Button _runButton = new()
	{
		Text = "Run leak probes"
	};

	bool _hasAutoRun;

	public MainPage()
	{
		Title = "MAUI Memory Leak Repro";
		_runButton.Clicked += async (_, _) => await RunAllScenariosAsync(quitWhenDone: false);

		Content = new Microsoft.Maui.Controls.ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(20),
				Spacing = 12,
				Children =
				{
					new Label
					{
						Text = "Shape Points event retention probes",
						FontAttributes = FontAttributes.Bold,
						FontSize = 18
					},
					new Label
					{
						Text = "The suspect case keeps the original PointCollection alive after Points is replaced.",
						FontSize = 13
					},
					_runButton,
					_host,
					_log
				}
			}
		};

		Loaded += async (_, _) =>
		{
			if (_hasAutoRun)
			{
				return;
			}

			_hasAutoRun = true;
			await RunAllScenariosAsync(quitWhenDone: true);
		};
	}

	async Task RunAllScenariosAsync(bool quitWhenDone)
	{
		_runButton.IsEnabled = false;
		_log.Children.Clear();
		foreach (var resultsPath in ResultsPaths.Distinct())
		{
			TryWriteResultsFile(resultsPath, () => File.WriteAllText(resultsPath, string.Empty));
		}
		Write("Running probes...");

		var results = new[]
		{
			await RunShapeScenarioAsync(ShapeKind.Polygon, replacePoints: false),
			await RunShapeScenarioAsync(ShapeKind.Polygon, replacePoints: true),
			await RunShapeScenarioAsync(ShapeKind.Polyline, replacePoints: false),
			await RunShapeScenarioAsync(ShapeKind.Polyline, replacePoints: true)
		};

		await ForceCollectAsync(results);

		_log.Children.Clear();
		foreach (var result in results)
		{
			Write(result.ToDisplayString());
		}

		Write("");
		Write(Analyze(results));
		_runButton.IsEnabled = true;

		if (quitWhenDone)
		{
			await Task.Delay(1000);
			Microsoft.Maui.Controls.Application.Current?.Quit();
		}
	}

	static string Analyze(IReadOnlyList<ProbeResult> results)
	{
		var polygonBaseline = results.Single(r => r.Kind == ShapeKind.Polygon && !r.ReplacePoints);
		var polygonReplace = results.Single(r => r.Kind == ShapeKind.Polygon && r.ReplacePoints);
		var polylineBaseline = results.Single(r => r.Kind == ShapeKind.Polyline && !r.ReplacePoints);
		var polylineReplace = results.Single(r => r.Kind == ShapeKind.Polyline && r.ReplacePoints);

		var polygonProven = polygonBaseline.CollectedAll && polygonReplace.HandlerAlive;
		var polylineProven = polylineBaseline.CollectedAll && polylineReplace.HandlerAlive;

		if (polygonProven && polylineProven)
		{
			return "PROVEN: PolygonHandler and PolylineHandler are retained when Points is replaced while the original PointCollection stays alive.";
		}

		if (polygonProven)
		{
			return "PROVEN: PolygonHandler is retained when Points is replaced while the original PointCollection stays alive.";
		}

		if (polylineProven)
		{
			return "PROVEN: PolylineHandler is retained when Points is replaced while the original PointCollection stays alive.";
		}

		return "NOT PROVEN: shape Points replacement did not isolate a leak. Continue with the next static candidates.";
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	async Task<ProbeResult> RunShapeScenarioAsync(ShapeKind kind, bool replacePoints)
	{
		var originalPoints = CreatePoints(0);
		var shape = CreateShape(kind, originalPoints);

		_host.Children.Add(shape);
		await WaitForHandlerAsync(shape);

		var handler = shape.Handler ?? throw new InvalidOperationException($"{kind} did not get a handler.");
		var platformView = handler.PlatformView ?? throw new InvalidOperationException($"{kind} handler did not get a platform view.");

		var result = new ProbeResult(
			kind,
			replacePoints,
			new WeakReference(shape),
			new WeakReference(handler),
			new WeakReference(platformView),
			originalPoints);

		if (replacePoints)
		{
			SetPoints(shape, CreatePoints(20));
		}

		_host.Children.Remove(shape);
		handler.DisconnectHandler();
		shape.Handler = null;

		return result;
	}

	async Task WaitForHandlerAsync(VisualElement element)
	{
		for (var i = 0; i < 50; i++)
		{
			if (element.Handler?.PlatformView is not null)
			{
				return;
			}

			await Task.Delay(50);
		}

		throw new TimeoutException($"{element.GetType().Name} did not receive a handler.");
	}

	static async Task ForceCollectAsync(IEnumerable<ProbeResult> results)
	{
		var resultList = results.ToArray();

		for (var i = 0; i < 30; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();

			if (resultList.All(result => !result.ShapeAlive && !result.HandlerAlive && !result.PlatformViewAlive))
			{
				break;
			}

			await Task.Delay(100);
		}
	}

	static Shape CreateShape(ShapeKind kind, PointCollection points)
	{
		return kind switch
		{
			ShapeKind.Polygon => new Polygon
			{
				Points = points,
				Fill = Colors.SteelBlue,
				Stroke = Colors.Black,
				StrokeThickness = 2,
				WidthRequest = 120,
				HeightRequest = 90
			},
			ShapeKind.Polyline => new Polyline
			{
				Points = points,
				Stroke = Colors.DarkRed,
				StrokeThickness = 4,
				WidthRequest = 120,
				HeightRequest = 90
			},
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	static void SetPoints(Shape shape, PointCollection points)
	{
		switch (shape)
		{
			case Polygon polygon:
				polygon.Points = points;
				break;
			case Polyline polyline:
				polyline.Points = points;
				break;
			default:
				throw new ArgumentException($"Unsupported shape: {shape.GetType().Name}", nameof(shape));
		}
	}

	static PointCollection CreatePoints(double offset)
	{
		return
		[
			new Point(10 + offset, 10),
			new Point(100 + offset, 20),
			new Point(75 + offset, 80),
			new Point(15 + offset, 70)
		];
	}

	void Write(string text)
	{
		Debug.WriteLine(text);
		Console.WriteLine(text);
		foreach (var resultsPath in ResultsPaths.Distinct())
		{
			TryWriteResultsFile(resultsPath, () => File.AppendAllText(resultsPath, text + Environment.NewLine));
		}
		_log.Children.Add(new Label { Text = text, FontSize = 13 });
	}

	static void TryWriteResultsFile(string resultsPath, Action write)
	{
		try
		{
			var directory = System.IO.Path.GetDirectoryName(resultsPath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			write();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Could not write results file '{resultsPath}': {ex}");
		}
	}

	enum ShapeKind
	{
		Polygon,
		Polyline
	}

	sealed record ProbeResult(
		ShapeKind Kind,
		bool ReplacePoints,
		WeakReference Shape,
		WeakReference Handler,
		WeakReference PlatformView,
		object RootedOriginalPoints)
	{
		public bool ShapeAlive => Shape.IsAlive;
		public bool HandlerAlive => Handler.IsAlive;
		public bool PlatformViewAlive => PlatformView.IsAlive;
		public bool CollectedAll => !ShapeAlive && !HandlerAlive && !PlatformViewAlive;

		public string ToDisplayString()
		{
			var mode = ReplacePoints ? "replace Points" : "baseline";
			return $"{Kind} {mode}: shape alive={ShapeAlive}, handler alive={HandlerAlive}, platform alive={PlatformViewAlive}";
		}
	}
}
