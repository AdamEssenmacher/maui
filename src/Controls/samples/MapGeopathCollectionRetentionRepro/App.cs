using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;

namespace MapGeopathCollectionRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new RunnerPage());
	}
}

sealed class RunnerPage : ContentPage
{
	bool _ran;

	public RunnerPage()
	{
		Content = new Label
		{
			Text = "Running Map Geopath collection retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await TryRunAsync();
	}

	protected override async void OnHandlerChanged()
	{
		base.OnHandlerChanged();
		await TryRunAsync();
	}

	async Task TryRunAsync()
	{
		if (_ran || Handler?.MauiContext is null)
			return;

		_ran = true;
		await Task.Delay(250);

		try
		{
			var report = ReproSession.Run();
			var text = report.ToText();
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(report.LeakProved ? 0 : 2);
		}
		catch (Exception ex)
		{
			var text = "MapGeopathCollectionRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/map-geopath-collection-retention-results.txt";

	const int Iterations = 80;
	const int RoutePointsPerShape = 256;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearGeopathHandlers: true);
		var current = RunScenario(clearGeopathHandlers: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearGeopathHandlers)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedRouteCollections = new List<IList<Location>>(Iterations * 2);
		var elementReferences = new List<WeakReference<MapElement>>(Iterations * 2);
		var payloadReferences = new List<WeakReference<RoutePayload>>(Iterations * 2);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations * 2);

		for (var i = 0; i < Iterations; i++)
		{
			CreateRetainedRoute(ShapeKind.Polyline, i, clearGeopathHandlers, retainedRouteCollections, elementReferences, payloadReferences, payloadBufferReferences);
			CreateRetainedRoute(ShapeKind.Polygon, i, clearGeopathHandlers, retainedRouteCollections, elementReferences, payloadReferences, payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(elementReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedRouteCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedRouteCollections);
		return result;
	}

	static void CreateRetainedRoute(
		ShapeKind kind,
		int iteration,
		bool clearGeopathHandlers,
		List<IList<Location>> retainedRouteCollections,
		List<WeakReference<MapElement>> elementReferences,
		List<WeakReference<RoutePayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new RoutePayload($"{kind}-route-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var element = CreateMapElement(kind);
		element.BindingContext = payload;

		var geopath = GetGeopath(element);
		for (var point = 0; point < RoutePointsPerShape; point++)
		{
			geopath.Add(new Location(47.6205 + point * 0.0001, -122.3493 - point * 0.0001));
		}

		if (clearGeopathHandlers)
			ClearCollectionChangedHandlers(geopath);

		retainedRouteCollections.Add(geopath);
		elementReferences.Add(new WeakReference<MapElement>(element));
		payloadReferences.Add(new WeakReference<RoutePayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static MapElement CreateMapElement(ShapeKind kind)
	{
		return kind switch
		{
			ShapeKind.Polyline => new Polyline(),
			ShapeKind.Polygon => new Polygon(),
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	static IList<Location> GetGeopath(MapElement element)
	{
		return element switch
		{
			Polyline polyline => polyline.Geopath,
			Polygon polygon => polygon.Geopath,
			_ => throw new ArgumentOutOfRangeException(nameof(element), element, null)
		};
	}

	static void ClearCollectionChangedHandlers(IList<Location> geopath)
	{
		if (geopath is not INotifyCollectionChanged)
			throw new InvalidOperationException($"Expected {geopath.GetType().FullName} to implement INotifyCollectionChanged.");

		var type = geopath.GetType();
		while (type is not null)
		{
			var field = type.GetField("CollectionChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (field is not null && typeof(NotifyCollectionChangedEventHandler).IsAssignableFrom(field.FieldType))
			{
				field.SetValue(geopath, null);
				return;
			}

			type = type.BaseType;
		}

		throw new InvalidOperationException($"Could not find the CollectionChanged backing field on {geopath.GetType().FullName}.");
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

	static void ForceGc()
	{
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}

	enum ShapeKind
	{
		Polyline,
		Polygon
	}

	sealed class RoutePayload
	{
		public RoutePayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int ElementsAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedRouteCollections,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.ElementsAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.ElementsAlive == Iterations * 2 &&
			Current.PayloadsAlive == Iterations * 2 &&
			Current.PayloadBuffersAlive == Iterations * 2;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("MapGeopathCollectionRetentionRepro");
			builder.AppendLine($"Iterations per shape type: {Iterations}");
			builder.AppendLine($"Shape types: Polyline, Polygon");
			builder.AppendLine($"Retained route collections per run: {Iterations * 2}");
			builder.AppendLine($"Route points per collection: {RoutePointsPerShape}");
			builder.AppendLine($"Payload per route overlay: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained route collections after clearing MAUI Geopath.CollectionChanged handlers");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained route collections with MAUI Geopath.CollectionChanged handlers intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app route cache -> Polyline/Polygon.Geopath ObservableCollection -> anonymous CollectionChanged handler -> Polyline/Polygon -> BindingContext payload");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  route collections retained by app cache: {result.RetainedRouteCollections}");
			builder.AppendLine($"  map elements alive after full GC: {result.ElementsAlive}/{Iterations * 2}");
			builder.AppendLine($"  route payloads alive after full GC: {result.PayloadsAlive}/{Iterations * 2}");
			builder.AppendLine($"  route payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations * 2}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
