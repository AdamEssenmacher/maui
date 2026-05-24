using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Maui.Handlers;

namespace Maui.Controls.HybridWebViewRepro;

public partial class MainPage : ContentPage
{
	static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(5);

	readonly List<Task> _pendingHangingInvokes = [];
	int _hangingAttemptCount;

	public MainPage()
	{
		InitializeComponent();
		UpdateRetainedCount();
	}

	async void OnRunWorkingInvokeClicked(object sender, EventArgs e)
	{
		WorkingStatusLabel.Text = "Running invoke...";

		try
		{
			await WaitForDocumentReadyAsync(WorkingHybridWebView);

			var result = await WorkingHybridWebView.InvokeJavaScriptAsync<string>(
				"ReturnDemoValue",
				ReproJsonContext.Default.String,
				["working"],
				[ReproJsonContext.Default.String]);

			WorkingStatusLabel.Text = $"Completed: {result}";
		}
		catch (Exception ex)
		{
			WorkingStatusLabel.Text = $"Failed: {ex.GetType().Name}: {ex.Message}";
		}

		UpdateRetainedCount();
	}

	async void OnRunHangingInvokeClicked(object sender, EventArgs e)
	{
		_hangingAttemptCount++;
		var attempt = _hangingAttemptCount;
		HangingStatusLabel.Text = $"Attempt {attempt}: running invoke...";

		try
		{
			await WaitForDocumentReadyAsync(HangingHybridWebView);

			var invokeTask = HangingHybridWebView.InvokeJavaScriptAsync<string>(
				"ReturnDemoValue",
				ReproJsonContext.Default.String,
				["broken"],
				[ReproJsonContext.Default.String]);

			_pendingHangingInvokes.Add(invokeTask);
			PendingCountLabel.Text = $"Pending repro tasks: {_pendingHangingInvokes.Count}";

			var completedTask = await Task.WhenAny(invokeTask, Task.Delay(InvokeTimeout));
			if (completedTask == invokeTask)
			{
				var result = await invokeTask;
				HangingStatusLabel.Text = $"Attempt {attempt}: unexpectedly completed: {result}";
				_pendingHangingInvokes.Remove(invokeTask);
			}
			else
			{
				HangingStatusLabel.Text = $"Attempt {attempt}: still pending after {InvokeTimeout.TotalSeconds:0}s.";
			}
		}
		catch (Exception ex)
		{
			HangingStatusLabel.Text = $"Attempt {attempt}: failed before timeout: {ex.GetType().Name}: {ex.Message}";
		}

		PendingCountLabel.Text = $"Pending repro tasks: {_pendingHangingInvokes.Count}";
		UpdateRetainedCount();
	}

	void OnRefreshRetainedCountClicked(object sender, EventArgs e)
	{
		UpdateRetainedCount();
	}

	async Task WaitForDocumentReadyAsync(HybridWebView webView)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);

		while (DateTimeOffset.UtcNow < deadline)
		{
			try
			{
				var readyState = await webView.EvaluateJavaScriptAsync("document.readyState");
				if (readyState is not null &&
					(readyState.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
					readyState.Contains("interactive", StringComparison.OrdinalIgnoreCase)))
				{
					return;
				}
			}
			catch
			{
				// The demo invoke is the relevant signal. Keep polling while the web view initializes.
			}

			await Task.Delay(100);
		}
	}

	void UpdateRetainedCount()
	{
		var retainedCount = TryGetRetainedCallbackCount();
		RetainedCountLabel.Text = retainedCount is int count
			? $"Retained callbacks: {count}"
			: "Retained callbacks: unavailable";
	}

	int? TryGetRetainedCallbackCount()
	{
		var services = HangingHybridWebView.Handler?.MauiContext?.Services;
		if (services is null)
		{
			return null;
		}

		var handlerAssembly = typeof(HybridWebViewHandler).Assembly;
		var managerInterfaceType = handlerAssembly.GetType("Microsoft.Maui.Handlers.IHybridWebViewTaskManager");
		if (managerInterfaceType is null)
		{
			return null;
		}

		var manager = services.GetService(managerInterfaceType);
		var callbackField = manager?.GetType().GetField("_asyncTaskCallbacks", BindingFlags.Instance | BindingFlags.NonPublic);
		var callbacks = callbackField?.GetValue(manager);
		if (callbacks is null)
		{
			return null;
		}

		return callbacks.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public)?.GetValue(callbacks) is int count
			? count
			: null;
	}
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(string))]
internal partial class ReproJsonContext : JsonSerializerContext
{
}
