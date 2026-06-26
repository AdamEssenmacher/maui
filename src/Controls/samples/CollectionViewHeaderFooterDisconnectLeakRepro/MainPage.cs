#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreGraphics;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using UIKit;

namespace CollectionViewHeaderFooterDisconnectLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running CollectionView Header/Footer disconnect leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		ReproResult? result = null;
		Exception? exception = null;

		try
		{
			result = await RunScenariosAsync();
		}
		catch (Exception ex)
		{
			exception = ex;
		}

		var text = exception is null
			? result!.ToString()
			: "RESULT: FAILED" + Environment.NewLine + exception;

		_status.Text = text;

		if (!string.IsNullOrWhiteSpace(_resultsPath))
			File.WriteAllText(_resultsPath, text);

		await Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	async Task<ReproResult> RunScenariosAsync()
	{
		var control = await RunScenarioAsync(explicitHeaderDisconnect: true);
		var current = await RunScenarioAsync(explicitHeaderDisconnect: false);

		Content = _status;
		return new ReproResult(control, current);
	}

	async Task<ScenarioResult> RunScenarioAsync(bool explicitHeaderDisconnect)
	{
		LeakProbeRegistry.Reset();

		var collectionView = new CollectionView
		{
			WidthRequest = 360,
			HeightRequest = 360,
			ItemsSource = Enumerable.Range(0, 20).Select(i => $"Row {i}").ToArray(),
			ItemTemplate = new DataTemplate(() =>
			{
				var label = new Label
				{
					HeightRequest = 44,
					VerticalTextAlignment = TextAlignment.Center
				};

				label.SetBinding(Label.TextProperty, ".");
				return label;
			})
		};

		var retainedHeaders = new List<TrackedHeaderView>();
		TrackedHeaderView? currentHeader = null;

		Content = collectionView;
		await WaitForHandlerAsync(collectionView);
		await Task.Delay(150);

		for (var i = 0; i < Iterations; i++)
		{
			if (explicitHeaderDisconnect)
				currentHeader?.Handler?.DisconnectHandler();

			currentHeader = new TrackedHeaderView
			{
				Index = i,
				WidthRequest = 320,
				HeightRequest = 72
			};

			retainedHeaders.Add(currentHeader);
			collectionView.Header = currentHeader;
			await Task.Yield();

			if (currentHeader.Handler is null)
				throw new InvalidOperationException($"Header {i} did not get a handler.");
		}

		if (explicitHeaderDisconnect)
			currentHeader?.Handler?.DisconnectHandler();

		Content = _status;
		collectionView.Handler?.DisconnectHandler();
		collectionView = null!;
		currentHeader = null;

		ForceGc();

		var result = new ScenarioResult(
			CountViewsWithHandlers(retainedHeaders),
			CountAlive(LeakProbeRegistry.HandlerReferences),
			LeakProbeRegistry.HandlerReferences.Count,
			CountAlive(LeakProbeRegistry.PayloadReferences),
			LeakProbeRegistry.PayloadReferences.Count);

		GC.KeepAlive(retainedHeaders);
		return result;
	}

	static async Task WaitForHandlerAsync(Element element)
	{
		for (var i = 0; i < 40 && element.Handler is null; i++)
			await Task.Delay(50);

		if (element.Handler is null)
			throw new InvalidOperationException($"{element.GetType().Name} did not get a handler.");
	}

	static int CountViewsWithHandlers(IEnumerable<TrackedHeaderView> views)
	{
		var count = 0;

		foreach (var view in views)
		{
			if (view.Handler is not null)
				count++;
		}

		return count;
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
		for (var i = 0; i < 5; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			Thread.Sleep(75);
		}
	}

	readonly record struct ScenarioResult(
		int HeadersWithHandlers,
		int HandlersAlive,
		int HandlersCreated,
		int PayloadsAlive,
		int PayloadsCreated);

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current)
	{
		public override string ToString()
		{
			var proven =
				Control.HeadersWithHandlers == 0 &&
				Control.HandlersAlive == 0 &&
				Control.PayloadsAlive == 0 &&
				Current.HeadersWithHandlers == Iterations &&
				Current.HandlersAlive == Iterations &&
				Current.PayloadsAlive == Iterations;

			var leakedBytes = Current.PayloadsAlive * TrackedHeaderViewHandler.PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine($"control-explicit-header-disconnect: headersWithHandlers={Control.HeadersWithHandlers}/{Iterations}, handlers={Control.HandlersAlive}/{Control.HandlersCreated}, payloads={Control.PayloadsAlive}/{Control.PayloadsCreated}");
			builder.AppendLine($"leak-current-header-replacement: headersWithHandlers={Current.HeadersWithHandlers}/{Iterations}, handlers={Current.HandlersAlive}/{Current.HandlersCreated}, payloads={Current.PayloadsAlive}/{Current.PayloadsCreated}, retainedBytes={leakedBytes}, retainedMiB={leakedBytes / 1024d / 1024d:0.0}");
			builder.AppendLine($"iterations={Iterations}");
			builder.AppendLine($"payloadBytesPerHeaderHandler={TrackedHeaderViewHandler.PayloadBytes}");
			builder.AppendLine($"dotnet-version={Environment.Version}");
			return builder.ToString();
		}
	}
}

public sealed class TrackedHeaderView : View
{
	public int Index { get; init; }
}

public sealed class TrackedHeaderViewHandler : ViewHandler<TrackedHeaderView, UIView>
{
	public const int PayloadBytes = 1024 * 1024;

	public static readonly IPropertyMapper<TrackedHeaderView, TrackedHeaderViewHandler> Mapper =
		new PropertyMapper<TrackedHeaderView, TrackedHeaderViewHandler>(ViewMapper);

	byte[]? _payload;

	public TrackedHeaderViewHandler()
		: base(Mapper)
	{
	}

	protected override UIView CreatePlatformView()
	{
		_payload = new byte[PayloadBytes];
		_payload[0] = (byte)(VirtualView?.Index ?? 0);
		LeakProbeRegistry.HandlerReferences.Add(new WeakReference<TrackedHeaderViewHandler>(this));
		LeakProbeRegistry.PayloadReferences.Add(new WeakReference<byte[]>(_payload));

		return new UIView(new CGRect(0, 0, 320, 72))
		{
			BackgroundColor = UIColor.FromRGBA(129, 68, 38, 255)
		};
	}

	protected override void DisconnectHandler(UIView platformView)
	{
		_payload = null;
		base.DisconnectHandler(platformView);
	}
}

static class LeakProbeRegistry
{
	public static List<WeakReference<TrackedHeaderViewHandler>> HandlerReferences { get; } = new();

	public static List<WeakReference<byte[]>> PayloadReferences { get; } = new();

	public static void Reset()
	{
		HandlerReferences.Clear();
		PayloadReferences.Clear();
	}
}
