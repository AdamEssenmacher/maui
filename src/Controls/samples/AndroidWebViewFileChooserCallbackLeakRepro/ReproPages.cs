using Android.Content;
using Android.Webkit;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using MauiWebView = Microsoft.Maui.Controls.WebView;
using Object = Java.Lang.Object;

namespace Maui.Controls.Sample.AndroidWebViewFileChooserCallbackLeakRepro;

public class MonitorPage : ContentPage
{
	readonly Button _trackedChooserButton;
	readonly Label _countsLabel;
	readonly Label _resultLabel;
	readonly MauiWebView _webView;
	TrackingWebChromeClient? _trackingWebChromeClient;
	int _baselineRegistryCallbackCount;

	public MonitorPage()
	{
		Title = "WebView File Callback Leak";

		_webView = new MauiWebView
		{
			HeightRequest = 150,
			AutomationId = "FileInputWebView",
			Source = new HtmlWebViewSource
			{
				Html = """
					<!doctype html>
					<html>
					<head>
						<meta name="viewport" content="width=device-width, initial-scale=1" />
						<style>
							body { font: 16px sans-serif; margin: 16px; }
							input { display: block; margin-top: 12px; font-size: 18px; }
						</style>
					</head>
					<body>
						<div>Native WebView file input</div>
						<input id="file-input" type="file" />
					</body>
					</html>
					"""
			}
		};
		_webView.HandlerChanged += OnWebViewHandlerChanged;

		_trackedChooserButton = new Button
		{
			Text = "Open tracked chooser",
			AutomationId = "OpenTrackedChooser",
			IsEnabled = false
		};
		_trackedChooserButton.Clicked += OnTrackedChooserClicked;

		var refreshButton = new Button
		{
			Text = "Refresh counts",
			AutomationId = "RefreshCounts"
		};
		refreshButton.Clicked += (_, _) => UpdateCounts(LeakTracker.Snapshot());

		var collectButton = new Button
		{
			Text = "Force GC",
			AutomationId = "ForceGC"
		};
		collectButton.Clicked += (_, _) => UpdateCounts(LeakTracker.CollectAndSnapshot());

		_countsLabel = new Label
		{
			AutomationId = "Counts",
			FontFamily = "monospace",
			FontSize = 14
		};

		_resultLabel = new Label
		{
			AutomationId = "Result",
			FontAttributes = FontAttributes.Bold,
			FontSize = 18
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(24),
				Spacing = 14,
				Children =
				{
					new Label
					{
						Text = "Android WebView file chooser callback leak repro",
						FontSize = 24,
						FontAttributes = FontAttributes.Bold
					},
					new Label
					{
						Text = "Tap the WebView file input or the tracked chooser, cancel the Android picker, then force GC. On an unfixed build, completed callbacks remain rooted by ActivityResultCallbackRegistry.",
						FontSize = 16
					},
					_webView,
					_trackedChooserButton,
					new HorizontalStackLayout
					{
						Spacing = 12,
						Children =
						{
							refreshButton,
							collectButton
						}
					},
					_resultLabel,
					_countsLabel
				}
			}
		};

		UpdateCounts(LeakTracker.Snapshot());
	}

	void OnWebViewHandlerChanged(object? sender, EventArgs e)
	{
		if (_webView.Handler is not IWebViewHandler webViewHandler || webViewHandler.PlatformView is null)
			return;

		_trackingWebChromeClient?.Dispose();
		_trackingWebChromeClient = new TrackingWebChromeClient(webViewHandler);
		webViewHandler.PlatformView.SetWebChromeClient(_trackingWebChromeClient);

		_baselineRegistryCallbackCount = LeakTracker.Snapshot().RegistryCallbackCount;
		_trackedChooserButton.IsEnabled = true;
		UpdateCounts(LeakTracker.Snapshot());
	}

	void OnTrackedChooserClicked(object? sender, EventArgs e)
	{
		if (_trackingWebChromeClient?.ChooseTrackedFile() != true)
		{
			_resultLabel.Text = "The WebView handler is not ready yet.";
		}
	}

	void UpdateCounts(LeakSnapshot snapshot)
	{
		var registryDelta = snapshot.RegistryCallbackCount - _baselineRegistryCallbackCount;

		_countsLabel.Text =
			$"Registry callbacks:      {snapshot.RegistryCallbackCount}\n" +
			$"Registry delta:          {registryDelta}\n" +
			$"Tracked created:         {snapshot.CreatedCallbackCount}\n" +
			$"Tracked completed:       {snapshot.CompletedCallbackCount}\n" +
			$"Tracked finalized:       {snapshot.FinalizedCallbackCount}\n" +
			$"Tracked live:            {snapshot.LiveTrackedCallbackCount}\n" +
			$"Weak refs alive:         {snapshot.AliveWeakReferences}/{snapshot.TotalWeakReferences}";

		_resultLabel.Text =
			snapshot.CompletedCallbackCount > snapshot.FinalizedCallbackCount && snapshot.AliveWeakReferences > 0
				? "Completed tracked callbacks are still alive."
				: "No completed tracked callback is currently retained.";
	}
}

public class TrackingWebChromeClient : MauiWebChromeClient
{
	public TrackingWebChromeClient(IWebViewHandler handler) : base(handler)
	{
	}

	public bool ChooseTrackedFile()
	{
		var callback = new TrackedValueCallback();
		var intent = new Intent(Intent.ActionGetContent);
		intent.AddCategory(Intent.CategoryOpenable);
		intent.SetType("*/*");

		return ChooseFile(callback, intent, "Select a file");
	}
}

public class TrackedValueCallback : Object, IValueCallback
{
	public TrackedValueCallback()
	{
		Interlocked.Increment(ref LeakTracker.CreatedCallbackCount);
		LeakTracker.Track(this);
	}

	public void OnReceiveValue(Object? value)
	{
		Interlocked.Increment(ref LeakTracker.CompletedCallbackCount);
	}

	~TrackedValueCallback()
	{
		Interlocked.Increment(ref LeakTracker.FinalizedCallbackCount);
	}
}
