using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using AndroidX.Activity;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.Maui.MauiBlazorWebView.DeviceTests.Elements;

public partial class BlazorWebViewTests
{
#if ANDROID
	[Fact]
	public async Task BackCallbackConsumesFirstBackPressWhenStaleEnabledRepro()
	{
		EnsureHandlerCreated(additionalCreationActions: appBuilder =>
		{
			appBuilder.Services.AddMauiBlazorWebView();
		});

		var bwv = new BlazorWebViewWithCustomFiles
		{
			HostPage = "wwwroot/index.html",
			CustomFiles = new Dictionary<string, string>
			{
				{ "index.html", TestStaticFilesContents.DefaultMauiIndexHtmlContent },
			},
		};
		bwv.RootComponents.Add(new RootComponent { ComponentType = typeof(MauiBlazorWebView.DeviceTests.Components.NoOpComponent), Selector = "#app", });

		await InvokeOnMainThreadAsync(async () =>
		{
			var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as ComponentActivity;
			Assert.NotNull(activity);

			var lowerPriorityCallback = new RecordingBackPressedCallback();
			activity.OnBackPressedDispatcher.AddCallback(activity, lowerPriorityCallback);

			try
			{
				var bwvHandler = CreateHandler<BlazorWebViewHandler>(bwv);
				var platformWebView = bwvHandler.PlatformView;
				await WebViewHelpers.WaitForWebViewReady(platformWebView);

				Assert.False(platformWebView.CanGoBack(), "The repro needs a BlazorWebView with no WebView history.");

				var blazorBackCallback = GetRegisteredBackPressedCallback(bwvHandler);
				blazorBackCallback.Enabled = true;

				activity.OnBackPressedDispatcher.OnBackPressed();

				Assert.Equal(0, lowerPriorityCallback.InvocationCount);
				Assert.False(blazorBackCallback.Enabled);

				activity.OnBackPressedDispatcher.OnBackPressed();

				Assert.Equal(1, lowerPriorityCallback.InvocationCount);
			}
			finally
			{
				lowerPriorityCallback.Remove();
				lowerPriorityCallback.Dispose();
				bwv.Handler = null;
			}
		});
	}

	static OnBackPressedCallback GetRegisteredBackPressedCallback(BlazorWebViewHandler handler)
	{
		var field = typeof(BlazorWebViewHandler).GetField("_backPressedCallback", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);

		return Assert.IsAssignableFrom<OnBackPressedCallback>(field.GetValue(handler));
	}

	sealed class RecordingBackPressedCallback : OnBackPressedCallback
	{
		public RecordingBackPressedCallback() : base(true)
		{
		}

		public int InvocationCount { get; private set; }

		public override void HandleOnBackPressed()
		{
			InvocationCount++;
		}
	}
#endif
}
