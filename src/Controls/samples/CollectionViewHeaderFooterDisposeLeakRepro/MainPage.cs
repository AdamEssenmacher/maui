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

namespace CollectionViewHeaderFooterDisposeLeakRepro;

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
			Text = "Running CollectionView Header/Footer dispose leak repro...",
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
		var control = await RunScenarioAsync(explicitChildDisconnect: true);
		var current = await RunScenarioAsync(explicitChildDisconnect: false);

		Content = _status;
		return new ReproResult(control, current);
	}

	async Task<ScenarioResult> RunScenarioAsync(bool explicitChildDisconnect)
	{
		LeakProbeRegistry.Reset();

		var retainedViews = new List<TrackedSupplementaryView>(Iterations * 2);

		for (var i = 0; i < Iterations; i++)
		{
			var header = new TrackedSupplementaryView
			{
				Index = i,
				Kind = "header",
				WidthRequest = 320,
				HeightRequest = 72
			};

			var footer = new TrackedSupplementaryView
			{
				Index = i,
				Kind = "footer",
				WidthRequest = 320,
				HeightRequest = 72
			};

			retainedViews.Add(header);
			retainedViews.Add(footer);

			var collectionView = new CollectionView
			{
				WidthRequest = 360,
				HeightRequest = 360,
				Header = header,
				Footer = footer,
				ItemsSource = Enumerable.Range(0, 12).Select(row => $"Row {row}").ToArray(),
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

			Content = collectionView;
			await WaitForHandlerAsync(collectionView);
			await WaitForHandlerAsync(header);
			await WaitForHandlerAsync(footer);
			await Task.Delay(10);

			if (explicitChildDisconnect)
			{
				header.Handler?.DisconnectHandler();
				footer.Handler?.DisconnectHandler();
			}

			Content = _status;
			collectionView.Handler?.DisconnectHandler();
			collectionView = null!;
		}

		ForceGc();

		var result = new ScenarioResult(
			CountViewsWithHandlers(retainedViews),
			CountAlive(LeakProbeRegistry.HandlerReferences),
			LeakProbeRegistry.HandlerReferences.Count,
			CountAlive(LeakProbeRegistry.PayloadReferences),
			LeakProbeRegistry.PayloadReferences.Count);

		GC.KeepAlive(retainedViews);
		return result;
	}

	static async Task WaitForHandlerAsync(Element element)
	{
		for (var i = 0; i < 80 && element.Handler is null; i++)
			await Task.Delay(50);

		if (element.Handler is null)
			throw new InvalidOperationException($"{element.GetType().Name} did not get a handler.");
	}

	static int CountViewsWithHandlers(IEnumerable<TrackedSupplementaryView> views)
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
		int ViewsWithHandlers,
		int HandlersAlive,
		int HandlersCreated,
		int PayloadsAlive,
		int PayloadsCreated);

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current)
	{
		public override string ToString()
		{
			var expectedViews = Iterations * 2;
			var proven =
				Control.ViewsWithHandlers == 0 &&
				Control.HandlersAlive == 0 &&
				Control.PayloadsAlive == 0 &&
				Current.ViewsWithHandlers == expectedViews &&
				Current.HandlersAlive == expectedViews &&
				Current.PayloadsAlive == expectedViews;

			var leakedBytes = Current.PayloadsAlive * TrackedSupplementaryViewHandler.PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine($"control-explicit-header-footer-disconnect: viewsWithHandlers={Control.ViewsWithHandlers}/{expectedViews}, handlers={Control.HandlersAlive}/{Control.HandlersCreated}, payloads={Control.PayloadsAlive}/{Control.PayloadsCreated}");
			builder.AppendLine($"leak-current-parent-collectionview-disconnect: viewsWithHandlers={Current.ViewsWithHandlers}/{expectedViews}, handlers={Current.HandlersAlive}/{Current.HandlersCreated}, payloads={Current.PayloadsAlive}/{Current.PayloadsCreated}, retainedBytes={leakedBytes}, retainedMiB={leakedBytes / 1024d / 1024d:0.0}");
			builder.AppendLine($"iterations={Iterations}");
			builder.AppendLine($"payloadBytesPerSupplementaryHandler={TrackedSupplementaryViewHandler.PayloadBytes}");
			builder.AppendLine($"dotnet-version={Environment.Version}");
			return builder.ToString();
		}
	}
}

public sealed class TrackedSupplementaryView : View
{
	public int Index { get; init; }

	public string Kind { get; init; } = string.Empty;
}

public sealed class TrackedSupplementaryViewHandler : ViewHandler<TrackedSupplementaryView, UIView>
{
	public const int PayloadBytes = 1024 * 1024;

	public static readonly IPropertyMapper<TrackedSupplementaryView, TrackedSupplementaryViewHandler> Mapper =
		new PropertyMapper<TrackedSupplementaryView, TrackedSupplementaryViewHandler>(ViewMapper);

	byte[]? _payload;

	public TrackedSupplementaryViewHandler()
		: base(Mapper)
	{
	}

	protected override UIView CreatePlatformView()
	{
		_payload = new byte[PayloadBytes];
		_payload[0] = (byte)(VirtualView?.Index ?? 0);
		LeakProbeRegistry.HandlerReferences.Add(new WeakReference<TrackedSupplementaryViewHandler>(this));
		LeakProbeRegistry.PayloadReferences.Add(new WeakReference<byte[]>(_payload));

		var color = VirtualView?.Kind == "header"
			? UIColor.FromRGBA(58, 102, 125, 255)
			: UIColor.FromRGBA(124, 82, 63, 255);

		return new UIView(new CGRect(0, 0, 320, 72))
		{
			BackgroundColor = color
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
	public static List<WeakReference<TrackedSupplementaryViewHandler>> HandlerReferences { get; } = new();

	public static List<WeakReference<byte[]>> PayloadReferences { get; } = new();

	public static void Reset()
	{
		HandlerReferences.Clear();
		PayloadReferences.Clear();
	}
}
