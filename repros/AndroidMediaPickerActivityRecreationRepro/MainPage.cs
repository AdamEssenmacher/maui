using Android.Content;
using Android.OS;
using AndroidMediaPickerActivityRecreationRepro.Platforms.Android;

namespace AndroidMediaPickerActivityRecreationRepro;

public sealed class MainPage : ContentPage
{
	readonly Label _launchStatusLabel;

	public MainPage()
	{
		Title = "MediaPicker recreation repro";
		BackgroundColor = Colors.White;

		var title = new Label
		{
			Text = "Android MediaPicker activity recreation",
			FontAttributes = FontAttributes.Bold,
			FontSize = 24,
			TextColor = Color.FromArgb("#111827")
		};

		var summary = new Label
		{
			Text = "This launcher closes after opening the picker activity. The picker activity intentionally allows rotation to recreate it while PickPhotosAsync is pending.",
			FontSize = 16,
			TextColor = Color.FromArgb("#374151")
		};

		var environment = new Label
		{
			Text = $"Android API {(int)Build.VERSION.SdkInt}",
			FontSize = 14,
			TextColor = Color.FromArgb("#4B5563")
		};

		var openButton = new Button
		{
			AutomationId = "OpenChildActivityButton",
			Text = "Open child activity",
			HorizontalOptions = LayoutOptions.Fill
		};
		openButton.Clicked += OnOpenChildActivityClicked;

		_launchStatusLabel = new Label
		{
			AutomationId = "LaunchStatusLabel",
			Text = "Ready",
			FontSize = 14,
			TextColor = Color.FromArgb("#4B5563")
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 24,
				Spacing = 18,
				Children =
				{
					title,
					summary,
					environment,
					openButton,
					_launchStatusLabel
				}
			}
		};
	}

	void OnOpenChildActivityClicked(object? sender, EventArgs e)
	{
		if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not Android.App.Activity activity)
		{
			_launchStatusLabel.Text = "Unable to resolve the current Android activity.";
			_launchStatusLabel.TextColor = Colors.Red;
			return;
		}

		_launchStatusLabel.Text = "Opening child activity...";
		_launchStatusLabel.TextColor = Color.FromArgb("#4B5563");
		activity.StartActivity(new Intent(activity, typeof(MediaPickerActivity)));
		activity.Finish();
	}
}
