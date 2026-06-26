#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using WebKit;
using BlazorView = Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebView;
using BlazorViewHandler = Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler;

namespace BlazorWebViewRootComponentsHandlerRetentionLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running BlazorWebView RootComponents handler retention leak repro...",
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
			System.IO.File.WriteAllText(_resultsPath, text);

		await Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	async Task<ReproResult> RunScenariosAsync()
	{
		var rootServices = Handler?.MauiContext?.Services
			?? throw new InvalidOperationException("The page MauiContext was unavailable.");

		var control = await RunScenarioAsync(rootServices, removeRootComponentsSubscription: true);
		var current = await RunScenarioAsync(rootServices, removeRootComponentsSubscription: false);

		Content = _status;
		return new ReproResult(control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(IServiceProvider rootServices, bool removeRootComponentsSubscription)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedView = new BlazorView();
		retainedView.RootComponents.Add(new RootComponent
		{
			Selector = "#app",
			ComponentType = typeof(DummyComponent)
		});

		var handlerReferences = new List<WeakReference<TestBlazorWebViewHandler>>(Iterations);
		var contextReferences = new List<WeakReference<MauiContext>>(Iterations);
		var payloadReferences = new List<WeakReference<ScopedPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			var payload = new ScopedPayload($"window-scope-{i}", new byte[PayloadBytes]);
			payload.Buffer[0] = (byte)i;

			var serviceProvider = new PayloadServiceProvider(rootServices, payload);
			var mauiContext = new MauiContext(serviceProvider);
			var handler = new TestBlazorWebViewHandler();

			handler.SetMauiContext(mauiContext);
			handler.SetVirtualView(retainedView);
			((IElementHandler)handler).DisconnectHandler();

			if (removeRootComponentsSubscription)
				RemoveRootComponentsCollectionChangedSubscription(handler, retainedView.RootComponents);

			handlerReferences.Add(new(handler));
			contextReferences.Add(new(mauiContext));
			payloadReferences.Add(new(payload));
			payloadBufferReferences.Add(new(payload.Buffer));

			handler = null!;
			mauiContext = null!;
			serviceProvider = null!;
			payload = null!;

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(handlerReferences),
			CountAlive(contextReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedView);
		return result;
	}

	static void RemoveRootComponentsCollectionChangedSubscription(BlazorViewHandler handler, RootComponentsCollection rootComponents)
	{
		var callbackMethod = typeof(BlazorViewHandler).GetMethod(
			"OnRootComponentsCollectionChanged",
			BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Could not find BlazorWebViewHandler.OnRootComponentsCollectionChanged.");

		var callback = (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
			typeof(NotifyCollectionChangedEventHandler),
			handler,
			callbackMethod);

		rootComponents.CollectionChanged -= callback;
	}

	sealed class TestBlazorWebViewHandler : BlazorViewHandler
	{
		protected override WKWebView CreatePlatformView()
		{
			return new WKWebView(CoreGraphics.CGRect.Empty, new WKWebViewConfiguration());
		}

		protected override void DisconnectHandler(WKWebView platformView)
		{
		}
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

	sealed class DummyComponent : IComponent
	{
		public void Attach(RenderHandle renderHandle)
		{
		}

		public Task SetParametersAsync(ParameterView parameters) => Task.CompletedTask;
	}

	sealed class ScopedPayload
	{
		public ScopedPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	sealed class PayloadServiceProvider : IServiceProvider, IKeyedServiceProvider
	{
		readonly IServiceProvider _inner;
		readonly ScopedPayload _payload;

		public PayloadServiceProvider(IServiceProvider inner, ScopedPayload payload)
		{
			_inner = inner;
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(ScopedPayload))
				return _payload;

			return _inner.GetService(serviceType);
		}

		public object? GetKeyedService(Type serviceType, object? serviceKey)
		{
			if (_inner is IKeyedServiceProvider keyedServiceProvider)
				return keyedServiceProvider.GetKeyedService(serviceType, serviceKey);

			return null;
		}

		public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
		{
			if (_inner is IKeyedServiceProvider keyedServiceProvider)
				return keyedServiceProvider.GetRequiredKeyedService(serviceType, serviceKey);

			throw new InvalidOperationException($"No keyed service provider is available for {serviceType}.");
		}
	}

	readonly record struct ScenarioResult(
		int HandlersAlive,
		int MauiContextsAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current)
	{
		public override string ToString()
		{
			var proven =
				Control.HandlersAlive == 0 &&
				Control.MauiContextsAlive == 0 &&
				Control.PayloadsAlive == 0 &&
				Control.PayloadBuffersAlive == 0 &&
				Current.HandlersAlive == Iterations &&
				Current.MauiContextsAlive == Iterations &&
				Current.PayloadsAlive == Iterations &&
				Current.PayloadBuffersAlive == Iterations;

			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("BlazorWebViewRootComponentsHandlerRetentionLeakRepro");
			builder.AppendLine($"Iterations: {Iterations}");
			builder.AppendLine($"Payload per handler: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: neutral platform handler plus RootComponents.CollectionChanged unsubscribe after disconnect");
			builder.AppendLine($"  handlers alive after full GC: {Control.HandlersAlive}/{Iterations}");
			builder.AppendLine($"  MauiContexts alive after full GC: {Control.MauiContextsAlive}/{Iterations}");
			builder.AppendLine($"  scoped payloads alive after full GC: {Control.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  scoped payload buffers alive after full GC: {Control.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {Control.HeapDelta / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: leak: neutral platform handler, disconnected handlers remain subscribed to retained RootComponents");
			builder.AppendLine($"  handlers alive after full GC: {Current.HandlersAlive}/{Iterations}");
			builder.AppendLine($"  MauiContexts alive after full GC: {Current.MauiContextsAlive}/{Iterations}");
			builder.AppendLine($"  scoped payloads alive after full GC: {Current.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  scoped payload buffers alive after full GC: {Current.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine($"  managed heap delta: {Current.HeapDelta / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: retained BlazorWebView -> RootComponentsCollection.CollectionChanged -> disconnected BlazorWebViewHandler -> MauiContext -> service provider -> scoped payload");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}
	}
}
