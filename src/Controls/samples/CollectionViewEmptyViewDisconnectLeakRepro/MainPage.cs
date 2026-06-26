#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreGraphics;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using UIKit;

namespace CollectionViewEmptyViewDisconnectLeakRepro;

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
			Text = "Running CollectionView EmptyView disconnect leak repro...",
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
		var control = await RunScenarioAsync(explicitEmptyViewDisconnect: true);
		var current = await RunScenarioAsync(explicitEmptyViewDisconnect: false);

		Content = _status;
		return new ReproResult(control, current);
	}

	async Task<ScenarioResult> RunScenarioAsync(bool explicitEmptyViewDisconnect)
	{
		LeakProbeRegistry.Reset();

		var collectionView = new CollectionView
		{
			WidthRequest = 360,
			HeightRequest = 240,
			ItemsSource = Array.Empty<string>()
		};

		var retainedEmptyViews = new List<TrackedEmptyView>();
		TrackedEmptyView? currentEmptyView = null;

		Content = collectionView;
		await WaitForHandlerAsync(collectionView);
		await Task.Delay(150);

		for (var i = 0; i < Iterations; i++)
		{
			if (explicitEmptyViewDisconnect)
				currentEmptyView?.Handler?.DisconnectHandler();

			currentEmptyView = new TrackedEmptyView
			{
				Index = i,
				WidthRequest = 320,
				HeightRequest = 96
			};

			retainedEmptyViews.Add(currentEmptyView);
			collectionView.EmptyView = currentEmptyView;
			await Task.Yield();

			if (currentEmptyView.Handler is null)
				throw new InvalidOperationException($"EmptyView {i} did not get a handler.");
		}

		if (explicitEmptyViewDisconnect)
			currentEmptyView?.Handler?.DisconnectHandler();

		collectionView.EmptyView = null;
		await Task.Delay(50);

		Content = _status;
		collectionView.Handler?.DisconnectHandler();
		collectionView = null!;
		currentEmptyView = null;

		ForceGc();

		var result = new ScenarioResult(
			CountViewsWithHandlers(retainedEmptyViews),
			CountAlive(LeakProbeRegistry.HandlerReferences),
			LeakProbeRegistry.HandlerReferences.Count,
			CountAlive(LeakProbeRegistry.PayloadReferences),
			LeakProbeRegistry.PayloadReferences.Count);

		GC.KeepAlive(retainedEmptyViews);
		return result;
	}

	static async Task WaitForHandlerAsync(Element element)
	{
		for (var i = 0; i < 40 && element.Handler is null; i++)
			await Task.Delay(50);

		if (element.Handler is null)
			throw new InvalidOperationException($"{element.GetType().Name} did not get a handler.");
	}

	static int CountViewsWithHandlers(IEnumerable<TrackedEmptyView> views)
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
		int EmptyViewsWithHandlers,
		int HandlersAlive,
		int HandlersCreated,
		int PayloadsAlive,
		int PayloadsCreated);

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current)
	{
		public override string ToString()
		{
			var proven =
				Control.EmptyViewsWithHandlers == 0 &&
				Control.HandlersAlive == 0 &&
				Control.PayloadsAlive == 0 &&
				Current.EmptyViewsWithHandlers == Iterations &&
				Current.HandlersAlive == Iterations &&
				Current.PayloadsAlive == Iterations;

			var leakedBytes = Current.PayloadsAlive * TrackedEmptyViewHandler.PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine($"control-explicit-emptyview-disconnect: emptyViewsWithHandlers={Control.EmptyViewsWithHandlers}/{Iterations}, handlers={Control.HandlersAlive}/{Control.HandlersCreated}, payloads={Control.PayloadsAlive}/{Control.PayloadsCreated}");
			builder.AppendLine($"leak-current-emptyview-replacement: emptyViewsWithHandlers={Current.EmptyViewsWithHandlers}/{Iterations}, handlers={Current.HandlersAlive}/{Current.HandlersCreated}, payloads={Current.PayloadsAlive}/{Current.PayloadsCreated}, retainedBytes={leakedBytes}, retainedMiB={leakedBytes / 1024d / 1024d:0.0}");
			builder.AppendLine($"iterations={Iterations}");
			builder.AppendLine($"payloadBytesPerEmptyViewHandler={TrackedEmptyViewHandler.PayloadBytes}");
			builder.AppendLine($"dotnet-version={Environment.Version}");
			return builder.ToString();
		}
	}
}

public sealed class TrackedEmptyView : View
{
	public int Index { get; init; }
}

public sealed class TrackedEmptyViewHandler : ViewHandler<TrackedEmptyView, UIView>
{
	public const int PayloadBytes = 1024 * 1024;

	public static readonly IPropertyMapper<TrackedEmptyView, TrackedEmptyViewHandler> Mapper =
		new PropertyMapper<TrackedEmptyView, TrackedEmptyViewHandler>(ViewMapper);

	byte[]? _payload;

	public TrackedEmptyViewHandler()
		: base(Mapper)
	{
	}

	protected override UIView CreatePlatformView()
	{
		_payload = new byte[PayloadBytes];
		_payload[0] = (byte)(VirtualView?.Index ?? 0);
		LeakProbeRegistry.HandlerReferences.Add(new WeakReference<TrackedEmptyViewHandler>(this));
		LeakProbeRegistry.PayloadReferences.Add(new WeakReference<byte[]>(_payload));

		return new UIView(new CGRect(0, 0, 320, 96))
		{
			BackgroundColor = UIColor.FromRGBA(94, 113, 44, 255)
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
	public static List<WeakReference<TrackedEmptyViewHandler>> HandlerReferences { get; } = new();

	public static List<WeakReference<byte[]>> PayloadReferences { get; } = new();

	public static void Reset()
	{
		HandlerReferences.Clear();
		PayloadReferences.Clear();
	}
}
