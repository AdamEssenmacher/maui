using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;
using MauiPath = Microsoft.Maui.Controls.Shapes.Path;

namespace PathGeometryFiguresClearLeakRepro;

public enum LeakScenarioKind
{
	Control,
	LeakySharedFigureClear,
	MitigationSharedFigureRemoveAt
}

public sealed record LeakRunOptions(
	int PageCount,
	int PathsPerPage,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds);

public sealed record LeakScenarioResult(
	LeakScenarioKind Kind,
	string Name,
	int TotalPages,
	int RetainedPages,
	int TotalPayloads,
	int RetainedPayloads,
	long RetainedPayloadBytes,
	int TotalPaths,
	int RetainedPaths,
	int TotalGeometries,
	int RetainedGeometries,
	long ManagedBytesBefore,
	long ManagedBytesAfter,
	long GcHeapBytesBefore,
	long GcHeapBytesAfter,
	TimeSpan Elapsed)
{
	public long ManagedBytesDelta => ManagedBytesAfter - ManagedBytesBefore;

	public long GcHeapBytesDelta => GcHeapBytesAfter - GcHeapBytesBefore;
}

public static class LeakScenarioRunner
{
	static readonly List<PathFigure> s_sharedFigureRoots = new();

	public static async Task<IReadOnlyList<LeakScenarioResult>> RunAsync(
		INavigation navigation,
		LeakRunOptions options,
		IReadOnlyList<LeakScenarioKind> scenarios,
		Action<string>? progress,
		CancellationToken cancellationToken)
	{
		var results = new List<LeakScenarioResult>(scenarios.Count);

		foreach (var scenario in scenarios)
		{
			cancellationToken.ThrowIfCancellationRequested();

			progress?.Invoke($"Preparing {GetScenarioName(scenario)}...");
			await ForceFullGcAsync(cancellationToken);

			var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
			var gcHeapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;
			var stopwatch = Stopwatch.StartNew();
			var references = new ScenarioReferences();
			var sharedFigure = CreateSharedFigureRootIfNeeded(scenario);

			for (var pageIndex = 0; pageIndex < options.PageCount; pageIndex++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await PushAndPopScenarioPageAsync(
					navigation,
					scenario,
					sharedFigure,
					options,
					pageIndex,
					references,
					cancellationToken);

				if ((pageIndex + 1) % 10 == 0 || pageIndex + 1 == options.PageCount)
					progress?.Invoke($"{GetScenarioName(scenario)}: {pageIndex + 1}/{options.PageCount} pages");
			}

			await ForceFullGcAsync(cancellationToken);
			stopwatch.Stop();

			var managedAfter = GC.GetTotalMemory(forceFullCollection: true);
			var gcHeapAfter = GC.GetGCMemoryInfo().HeapSizeBytes;

			results.Add(new LeakScenarioResult(
				scenario,
				GetScenarioName(scenario),
				references.Pages.Count,
				CountAlive(references.Pages),
				references.Payloads.Count,
				CountAlive(references.Payloads),
				SumAlivePayloadBytes(references.Payloads),
				references.Paths.Count,
				CountAlive(references.Paths),
				references.Geometries.Count,
				CountAlive(references.Geometries),
				managedBefore,
				managedAfter,
				gcHeapBefore,
				gcHeapAfter,
				stopwatch.Elapsed));
		}

		return results;
	}

	static PathFigure? CreateSharedFigureRootIfNeeded(LeakScenarioKind scenario)
	{
		if (scenario == LeakScenarioKind.Control)
			return null;

		var figure = CreateFigure(-1, -1);
		s_sharedFigureRoots.Add(figure);
		return figure;
	}

	static async Task PushAndPopScenarioPageAsync(
		INavigation navigation,
		LeakScenarioKind scenario,
		PathFigure? sharedFigure,
		LeakRunOptions options,
		int pageIndex,
		ScenarioReferences references,
		CancellationToken cancellationToken)
	{
		var page = CreateScenarioPage(scenario, sharedFigure, options, pageIndex, references);

		await navigation.PushAsync(page, animated: false);
		await Task.Delay(options.DwellMilliseconds, cancellationToken);

		var popped = await navigation.PopAsync(animated: false);

		if (!ReferenceEquals(page, popped))
			throw new InvalidOperationException($"Unexpected popped page for {scenario} page {pageIndex}.");

		page = null;
		popped = null;
		await Task.Yield();
	}

	static ContentPage CreateScenarioPage(
		LeakScenarioKind scenario,
		PathFigure? sharedFigure,
		LeakRunOptions options,
		int pageIndex,
		ScenarioReferences references)
	{
		var payload = new PayloadViewModel(pageIndex, options.PayloadMegabytesPerPage);
		var layout = new Grid
		{
			BindingContext = payload,
			WidthRequest = 1,
			HeightRequest = 1,
			Opacity = 0.01
		};

		var page = new ContentPage
		{
			Title = $"{scenario} {pageIndex}",
			BindingContext = payload,
			Content = layout
		};

		references.Pages.Add(new WeakReference<Page>(page));
		references.Payloads.Add(new WeakReference<PayloadViewModel>(payload));

		for (var pathIndex = 0; pathIndex < options.PathsPerPage; pathIndex++)
		{
			var geometry = new PathGeometry();
			var figure = scenario == LeakScenarioKind.Control
				? CreateFigure(pageIndex, pathIndex)
				: sharedFigure ?? throw new InvalidOperationException("Shared figure was not created.");

			geometry.Figures.Add(figure);

			var path = new MauiPath
			{
				BindingContext = payload,
				Data = geometry,
				HeightRequest = 1,
				WidthRequest = 1,
				Stroke = Colors.CornflowerBlue,
				StrokeThickness = 1
			};

			layout.Add(path);
			references.Paths.Add(new WeakReference<MauiPath>(path));
			references.Geometries.Add(new WeakReference<PathGeometry>(geometry));

			if (scenario == LeakScenarioKind.MitigationSharedFigureRemoveAt)
				geometry.Figures.RemoveAt(0);
			else
				geometry.Figures.Clear();
		}

		return page;
	}

	static PathFigure CreateFigure(int pageIndex, int pathIndex)
	{
		var x = Math.Max(0, pathIndex);
		var y = Math.Max(0, pageIndex % 64);

		return new PathFigure
		{
			StartPoint = new Point(x, y),
			Segments =
			{
				new LineSegment { Point = new Point(x + 1, y + 1) }
			}
		};
	}

	static async Task ForceFullGcAsync(CancellationToken cancellationToken)
	{
		for (var i = 0; i < 6; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
			GC.WaitForPendingFinalizers();
			await Task.Delay(50, cancellationToken);
		}
	}

	static int CountAlive<T>(IReadOnlyList<WeakReference<T>> references)
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

	static long SumAlivePayloadBytes(IReadOnlyList<WeakReference<PayloadViewModel>> references)
	{
		long bytes = 0;

		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out var payload))
				bytes += payload.PayloadBytes;
		}

		return bytes;
	}

	static string GetScenarioName(LeakScenarioKind scenario)
	{
		return scenario switch
		{
			LeakScenarioKind.Control => "control: page-local PathFigure + Figures.Clear()",
			LeakScenarioKind.LeakySharedFigureClear => "leaky: shared PathFigure + Figures.Clear()",
			LeakScenarioKind.MitigationSharedFigureRemoveAt => "mitigation: shared PathFigure + RemoveAt(0)",
			_ => scenario.ToString()
		};
	}

	sealed class ScenarioReferences
	{
		public List<WeakReference<Page>> Pages { get; } = new();

		public List<WeakReference<PayloadViewModel>> Payloads { get; } = new();

		public List<WeakReference<MauiPath>> Paths { get; } = new();

		public List<WeakReference<PathGeometry>> Geometries { get; } = new();
	}

	sealed class PayloadViewModel
	{
		readonly byte[] _payload;

		public PayloadViewModel(int pageIndex, int payloadMegabytes)
		{
			PageIndex = pageIndex;
			_payload = new byte[payloadMegabytes * 1024 * 1024];
			_payload[0] = (byte)(pageIndex % byte.MaxValue);
		}

		public int PageIndex { get; }

		public long PayloadBytes => _payload.LongLength;
	}
}
