#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using CoreGraphics;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace ShellTitleViewReplacementLeakRepro;

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
			Text = "Running Shell TitleView replacement leak repro...",
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
			if (Handler?.MauiContext is not IMauiContext mauiContext)
				throw new InvalidOperationException("MainPage does not have a MauiContext.");

			result = RunScenarios(mauiContext);
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

		await System.Threading.Tasks.Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	static ReproResult RunScenarios(IMauiContext mauiContext)
	{
		var control = RunControlScenario(mauiContext);
		var current = RunCurrentScenario(mauiContext);

		return new ReproResult(control, current);
	}

	static ScenarioResult RunControlScenario(IMauiContext mauiContext)
	{
		LeakProbeRegistry.Reset();

		var retainedTitleViews = new List<TrackedTitleView>();
		ShellPageRendererTracker.TitleViewContainer? currentContainer = null;
		TrackedTitleView? currentTitleView = null;

		for (var i = 0; i < Iterations; i++)
		{
			currentTitleView?.Handler?.DisconnectHandler();
			currentContainer?.Dispose();

			currentTitleView = CreateTitleView(i, mauiContext);
			retainedTitleViews.Add(currentTitleView);
			currentContainer = new ShellPageRendererTracker.TitleViewContainer(currentTitleView);
		}

		currentTitleView?.Handler?.DisconnectHandler();
		currentContainer?.Dispose();
		currentContainer = null;
		currentTitleView = null;

		ForceGc();

		var result = new ScenarioResult(
			CountAlive(LeakProbeRegistry.HandlerReferences),
			LeakProbeRegistry.HandlerReferences.Count,
			CountAlive(LeakProbeRegistry.PayloadReferences),
			LeakProbeRegistry.PayloadReferences.Count);

		GC.KeepAlive(retainedTitleViews);
		return result;
	}

	static ScenarioResult RunCurrentScenario(IMauiContext mauiContext)
	{
		LeakProbeRegistry.Reset();

		var retainedTitleViews = new List<TrackedTitleView>();
		ShellPageRendererTracker.TitleViewContainer? currentContainer = null;

		for (var i = 0; i < Iterations; i++)
		{
			currentContainer?.Dispose();

			var titleView = CreateTitleView(i, mauiContext);
			retainedTitleViews.Add(titleView);
			currentContainer = new ShellPageRendererTracker.TitleViewContainer(titleView);
		}

		currentContainer?.Dispose();
		currentContainer = null;

		ForceGc();

		var result = new ScenarioResult(
			CountAlive(LeakProbeRegistry.HandlerReferences),
			LeakProbeRegistry.HandlerReferences.Count,
			CountAlive(LeakProbeRegistry.PayloadReferences),
			LeakProbeRegistry.PayloadReferences.Count);

		GC.KeepAlive(retainedTitleViews);
		return result;
	}

	static TrackedTitleView CreateTitleView(int index, IMauiContext mauiContext)
	{
		var titleView = new TrackedTitleView
		{
			WidthRequest = 260,
			HeightRequest = 44,
			Index = index
		};

		titleView.ToHandler(mauiContext);
		return titleView;
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
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			Thread.Sleep(50);
		}
	}

	readonly record struct ScenarioResult(
		int HandlersAlive,
		int HandlersCreated,
		int PayloadsAlive,
		int PayloadsCreated);

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current)
	{
		public override string ToString()
		{
			var proven =
				Control.HandlersAlive == 0 &&
				Control.PayloadsAlive == 0 &&
				Current.HandlersAlive == Iterations &&
				Current.PayloadsAlive == Iterations;

			var leakedBytes = Current.PayloadsAlive * TrackedTitleViewHandler.PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine($"control-explicit-titleview-disconnect: handlers={Control.HandlersAlive}/{Control.HandlersCreated}, payloads={Control.PayloadsAlive}/{Control.PayloadsCreated}");
			builder.AppendLine($"leak-current-titleview-replacement: handlers={Current.HandlersAlive}/{Current.HandlersCreated}, payloads={Current.PayloadsAlive}/{Current.PayloadsCreated}, retainedBytes={leakedBytes}, retainedMiB={leakedBytes / 1024d / 1024d:0.0}");
			builder.AppendLine($"iterations={Iterations}");
			builder.AppendLine($"dotnet-version={Environment.Version}");
			return builder.ToString();
		}
	}
}

public sealed class TrackedTitleView : View
{
	public int Index { get; init; }
}

public sealed class TrackedTitleViewHandler : ViewHandler<TrackedTitleView, UIView>
{
	public const int PayloadBytes = 1024 * 1024;

	public static readonly IPropertyMapper<TrackedTitleView, TrackedTitleViewHandler> Mapper =
		new PropertyMapper<TrackedTitleView, TrackedTitleViewHandler>(ViewMapper);

	byte[]? _payload;

	public TrackedTitleViewHandler()
		: base(Mapper)
	{
	}

	protected override UIView CreatePlatformView()
	{
		_payload = new byte[PayloadBytes];
		_payload[0] = (byte)(VirtualView?.Index ?? 0);
		LeakProbeRegistry.HandlerReferences.Add(new WeakReference<TrackedTitleViewHandler>(this));
		LeakProbeRegistry.PayloadReferences.Add(new WeakReference<byte[]>(_payload));

		var view = new UIView(new CGRect(0, 0, 260, 44))
		{
			BackgroundColor = UIColor.FromRGBA(30, 96, 145, 255)
		};

		return view;
	}

	protected override void DisconnectHandler(UIView platformView)
	{
		_payload = null;
		base.DisconnectHandler(platformView);
	}
}

static class LeakProbeRegistry
{
	public static List<WeakReference<TrackedTitleViewHandler>> HandlerReferences { get; } = new();

	public static List<WeakReference<byte[]>> PayloadReferences { get; } = new();

	public static void Reset()
	{
		HandlerReferences.Clear();
		PayloadReferences.Clear();
	}
}
