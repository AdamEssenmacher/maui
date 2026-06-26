#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;

namespace HybridWebViewFailedInvokeCallbackLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;
	const string InvokeThrowsSwitch = "HybridWebView.InvokeJavaScriptThrowsExceptions";

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running HybridWebView failed invoke callback leak repro...",
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
		var control = await RunScenarioAsync(suppressInvokeExceptions: false);
		var current = await RunScenarioAsync(suppressInvokeExceptions: true);

		Content = _status;
		return new ReproResult(control, current);
	}

	async Task<ScenarioResult> RunScenarioAsync(bool suppressInvokeExceptions)
	{
		AppContext.SetSwitch(InvokeThrowsSwitch, !suppressInvokeExceptions);
		LeakProbeRegistry.Reset();

		var webView = new HybridWebView
		{
			DefaultFile = "index.html",
			HybridRoot = "wwwroot",
			WidthRequest = 640,
			HeightRequest = 360
		};

		Content = webView;
		await WaitForHandlerAsync(webView);
		await WaitForBridgeAsync(webView);

		var inspector = TaskManagerInspector.Create(webView);
		var initialCallbacks = inspector.CallbackCount;
		var completedFailures = 0;
		var pendingFailures = 0;
		var unexpectedCompletions = 0;

		for (var i = 0; i < Iterations; i++)
		{
			var outcome = await InvokeFailingJavaScriptAsync(
				webView,
				inspector,
				initialCallbacks + (suppressInvokeExceptions ? i + 1 : 0),
				i,
				suppressInvokeExceptions);

			switch (outcome)
			{
				case InvokeOutcome.CompletedFailure:
					completedFailures++;
					break;
				case InvokeOutcome.Pending:
					pendingFailures++;
					break;
				case InvokeOutcome.UnexpectedCompletion:
					unexpectedCompletions++;
					break;
			}
		}

		await Task.Delay(300);

		Content = _status;
		webView.Handler?.DisconnectHandler();
		webView = null!;

		ForceGc();

		var result = new ScenarioResult(
			inspector.CallbackCount - initialCallbacks,
			completedFailures,
			pendingFailures,
			unexpectedCompletions,
			CountAlive(LeakProbeRegistry.PayloadReferences),
			LeakProbeRegistry.PayloadReferences.Count);

		return result;
	}

	static async Task<InvokeOutcome> InvokeFailingJavaScriptAsync(
		HybridWebView webView,
		TaskManagerInspector inspector,
		int expectedCallbackCount,
		int index,
		bool suppressInvokeExceptions)
	{
		var payload = new InvokePayload
		{
			Tag = $"payload-{index}",
			Buffer = new byte[PayloadBytes]
		};
		payload.Buffer[0] = (byte)index;
		LeakProbeRegistry.PayloadReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		var invokeTask = webView.InvokeJavaScriptAsync<string>(
			"ThrowWithPayload",
			ReproJsonContext.Default.String,
			new object?[] { payload },
			new System.Text.Json.Serialization.Metadata.JsonTypeInfo?[] { ReproJsonContext.Default.InvokePayload });

		payload = null!;

		if (!suppressInvokeExceptions)
		{
			try
			{
				await invokeTask;
				return InvokeOutcome.UnexpectedCompletion;
			}
			catch
			{
				return InvokeOutcome.CompletedFailure;
			}
		}

		var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (invokeTask.IsCompleted)
				return InvokeOutcome.UnexpectedCompletion;

			if (inspector.CallbackCount >= expectedCallbackCount)
				return InvokeOutcome.Pending;

			await Task.Delay(25);
		}

		return invokeTask.IsCompleted
			? InvokeOutcome.UnexpectedCompletion
			: InvokeOutcome.Pending;
	}

	static async Task WaitForHandlerAsync(Element element)
	{
		for (var i = 0; i < 60 && element.Handler is null; i++)
			await Task.Delay(50);

		if (element.Handler is null)
			throw new InvalidOperationException($"{element.GetType().Name} did not get a handler.");
	}

	static async Task WaitForBridgeAsync(HybridWebView webView)
	{
		for (var i = 0; i < 100; i++)
		{
			try
			{
				var readyState = await webView.EvaluateJavaScriptAsync("document.readyState");
				var bridgeState = await webView.EvaluateJavaScriptAsync("typeof window.HybridWebView + ':' + typeof window.ThrowWithPayload");
				if (readyState?.Contains("complete", StringComparison.OrdinalIgnoreCase) == true &&
					bridgeState?.Contains("object:function", StringComparison.OrdinalIgnoreCase) == true)
				{
					return;
				}
			}
			catch
			{
				// The WebView may still be initializing. Keep polling until the bridge is available.
			}

			await Task.Delay(100);
		}

		throw new TimeoutException("HybridWebView JavaScript bridge did not initialize.");
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

	enum InvokeOutcome
	{
		CompletedFailure,
		Pending,
		UnexpectedCompletion
	}

	readonly record struct ScenarioResult(
		int RetainedCallbacks,
		int CompletedFailures,
		int PendingFailures,
		int UnexpectedCompletions,
		int PayloadsAlive,
		int PayloadsCreated);

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current)
	{
		public override string ToString()
		{
			var proven =
				Control.RetainedCallbacks == 0 &&
				Control.CompletedFailures == Iterations &&
				Control.PayloadsAlive == 0 &&
				Current.RetainedCallbacks == Iterations &&
				Current.PendingFailures == Iterations &&
				Current.PayloadsAlive == Iterations;

			var leakedBytes = Current.PayloadsAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine($"control-default-exception-path: callbacks={Control.RetainedCallbacks}, completedFailures={Control.CompletedFailures}/{Iterations}, pendingFailures={Control.PendingFailures}, unexpectedCompletions={Control.UnexpectedCompletions}, payloads={Control.PayloadsAlive}/{Control.PayloadsCreated}");
			builder.AppendLine($"leak-suppressed-exception-path: callbacks={Current.RetainedCallbacks}, completedFailures={Current.CompletedFailures}, pendingFailures={Current.PendingFailures}/{Iterations}, unexpectedCompletions={Current.UnexpectedCompletions}, payloads={Current.PayloadsAlive}/{Current.PayloadsCreated}, retainedBytes={leakedBytes}, retainedMiB={leakedBytes / 1024d / 1024d:0.0}");
			builder.AppendLine($"iterations={Iterations}");
			builder.AppendLine($"payloadBytesPerInvoke={PayloadBytes}");
			builder.AppendLine($"appContextSwitch={InvokeThrowsSwitch}=false");
			builder.AppendLine($"dotnet-version={Environment.Version}");
			return builder.ToString();
		}
	}
}

public sealed class InvokePayload
{
	public string? Tag { get; set; }

	public byte[] Buffer { get; set; } = [];
}

sealed class TaskManagerInspector
{
	readonly object _callbacks;
	readonly PropertyInfo _countProperty;

	TaskManagerInspector(object callbacks, PropertyInfo countProperty)
	{
		_callbacks = callbacks;
		_countProperty = countProperty;
	}

	public int CallbackCount => (int)_countProperty.GetValue(_callbacks)!;

	public static TaskManagerInspector Create(HybridWebView webView)
	{
		var services = webView.Handler?.MauiContext?.Services
			?? throw new InvalidOperationException("HybridWebView services were unavailable.");

		var handlerAssembly = typeof(HybridWebViewHandler).Assembly;
		var managerInterfaceType = handlerAssembly.GetType("Microsoft.Maui.Handlers.IHybridWebViewTaskManager")
			?? throw new InvalidOperationException("Could not find IHybridWebViewTaskManager.");

		var manager = services.GetService(managerInterfaceType)
			?? throw new InvalidOperationException("Could not resolve IHybridWebViewTaskManager.");

		var callbackField = manager.GetType().GetField("_asyncTaskCallbacks", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Could not find _asyncTaskCallbacks.");

		var callbacks = callbackField.GetValue(manager)
			?? throw new InvalidOperationException("The callback dictionary was null.");

		var countProperty = callbacks.GetType().GetProperty(nameof(ICollection<object>.Count), BindingFlags.Instance | BindingFlags.Public)
			?? throw new InvalidOperationException("Could not read callback count.");

		return new TaskManagerInspector(callbacks, countProperty);
	}
}

static class LeakProbeRegistry
{
	public static List<WeakReference<byte[]>> PayloadReferences { get; } = new();

	public static void Reset()
	{
		PayloadReferences.Clear();
	}
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(InvokePayload))]
internal partial class ReproJsonContext : JsonSerializerContext
{
}
