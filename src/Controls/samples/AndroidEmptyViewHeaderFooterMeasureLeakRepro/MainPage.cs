#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace AndroidEmptyViewHeaderFooterMeasureLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;
	const int ItemCount = 800;

	readonly string _resultsPath;
	readonly Label _status;

	public MainPage(string resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running Android EmptyView header/footer measurement leak repro...",
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
		Directory.CreateDirectory(Path.GetDirectoryName(_resultsPath)!);
		File.WriteAllText(_resultsPath, text);

		await Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	async Task<ReproResult> RunScenariosAsync()
	{
		var control = await RunScenarioAsync(explicitFooterDisconnect: true);
		var current = await RunScenarioAsync(explicitFooterDisconnect: false);

		Content = _status;
		return new ReproResult(control, current);
	}

	async Task<ScenarioResult> RunScenarioAsync(bool explicitFooterDisconnect)
	{
		LeakProbeRegistry.Reset();

		var collectionView = CreateCollectionView();
		var retainedFooters = new List<TrackedFooterView>();

		Content = collectionView;
		await WaitForHandlerAsync(collectionView);
		await Task.Delay(150);

		for (var i = 0; i < Iterations; i++)
		{
			var footer = new TrackedFooterView
			{
				Index = i,
				WidthRequest = 320,
				HeightRequest = 56
			};

			retainedFooters.Add(footer);
			collectionView.Footer = footer;

			// Changing EmptyView uses the public mapper and refreshes the hidden
			// EmptyViewAdapter even though the CollectionView has items.
			collectionView.EmptyView = $"No matching records {i}";

			if (footer.Handler is null)
				throw new InvalidOperationException($"Footer {i} was not measured by EmptyViewAdapter.");

			if (explicitFooterDisconnect)
				footer.Handler.DisconnectHandler();

			collectionView.Footer = null;
			await Task.Yield();
		}

		collectionView.Footer = null;
		collectionView.EmptyView = "No matching records final";
		await Task.Delay(50);

		Content = _status;
		collectionView.Handler?.DisconnectHandler();
		collectionView = null!;

		ForceGc();

		var result = new ScenarioResult(
			CountViewsWithHandlers(retainedFooters),
			CountAlive(LeakProbeRegistry.HandlerReferences),
			LeakProbeRegistry.HandlerReferences.Count,
			CountAlive(LeakProbeRegistry.PayloadReferences),
			LeakProbeRegistry.PayloadReferences.Count);

		GC.KeepAlive(retainedFooters);
		return result;
	}

	static CollectionView CreateCollectionView()
	{
		return new CollectionView
		{
			HeightRequest = 140,
			ItemsSource = Enumerable.Range(0, ItemCount).Select(i => $"Order row {i}").ToArray(),
			EmptyView = "No matching records",
			ItemTemplate = new DataTemplate(() =>
			{
				var label = new Label
				{
					HeightRequest = 44,
					Padding = new Thickness(12, 4),
					VerticalTextAlignment = TextAlignment.Center
				};

				label.SetBinding(Label.TextProperty, ".");
				return label;
			})
		};
	}

	static async Task WaitForHandlerAsync(Element element)
	{
		for (var i = 0; i < 40 && element.Handler is null; i++)
			await Task.Delay(50);

		if (element.Handler is null)
			throw new InvalidOperationException($"{element.GetType().Name} did not get a handler.");
	}

	static int CountViewsWithHandlers(IEnumerable<TrackedFooterView> views)
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
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(75);
		}
	}

	readonly record struct ScenarioResult(
		int FootersWithHandlers,
		int HandlersAlive,
		int HandlersCreated,
		int PayloadsAlive,
		int PayloadsCreated);

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current)
	{
		public override string ToString()
		{
			var proven =
				Control.FootersWithHandlers == 0 &&
				Control.HandlersAlive == 0 &&
				Control.PayloadsAlive == 0 &&
				Current.FootersWithHandlers == Iterations &&
				Current.HandlersAlive == Iterations &&
				Current.PayloadsAlive == Iterations;

			var leakedBytes = Current.PayloadsAlive * TrackedFooterViewHandler.PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine($"control-explicit-footer-disconnect: footersWithHandlers={Control.FootersWithHandlers}/{Iterations}, handlers={Control.HandlersAlive}/{Control.HandlersCreated}, payloads={Control.PayloadsAlive}/{Control.PayloadsCreated}");
			builder.AppendLine($"leak-current-emptyview-footer-measurement: footersWithHandlers={Current.FootersWithHandlers}/{Iterations}, handlers={Current.HandlersAlive}/{Current.HandlersCreated}, payloads={Current.PayloadsAlive}/{Current.PayloadsCreated}, retainedBytes={leakedBytes}, retainedMiB={leakedBytes / 1024d / 1024d:0.0}");
			builder.AppendLine($"iterations={Iterations}");
			builder.AppendLine($"collectionItemCount={ItemCount}");
			builder.AppendLine($"payloadBytesPerFooterHandler={TrackedFooterViewHandler.PayloadBytes}");
			builder.AppendLine($"dotnet-version={Environment.Version}");
			return builder.ToString();
		}
	}
}

public sealed class TrackedFooterView : View
{
	public int Index { get; init; }
}

public sealed class TrackedFooterViewHandler : ViewHandler<TrackedFooterView, AView>
{
	public const int PayloadBytes = 1024 * 1024;

	public static readonly IPropertyMapper<TrackedFooterView, TrackedFooterViewHandler> Mapper =
		new PropertyMapper<TrackedFooterView, TrackedFooterViewHandler>(ViewMapper);

	byte[]? _payload;

	public TrackedFooterViewHandler()
		: base(Mapper)
	{
	}

	protected override AView CreatePlatformView()
	{
		_payload = new byte[PayloadBytes];
		_payload[0] = (byte)(VirtualView?.Index ?? 0);
		LeakProbeRegistry.HandlerReferences.Add(new WeakReference<TrackedFooterViewHandler>(this));
		LeakProbeRegistry.PayloadReferences.Add(new WeakReference<byte[]>(_payload));

		var view = new AView(Context);
		view.SetBackgroundColor(AColor.Rgb(33, 115, 70));
		return view;
	}

	protected override void DisconnectHandler(AView platformView)
	{
		_payload = null;
		base.DisconnectHandler(platformView);
	}
}

static class LeakProbeRegistry
{
	public static List<WeakReference<TrackedFooterViewHandler>> HandlerReferences { get; } = new();

	public static List<WeakReference<byte[]>> PayloadReferences { get; } = new();

	public static void Reset()
	{
		HandlerReferences.Clear();
		PayloadReferences.Clear();
	}
}
