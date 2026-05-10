namespace Maui.Controls.Sample.Issues;

[Issue(IssueTracker.Github, 32987, "[Android] Shell status and flyout colors use colorPrimary under edge-to-edge", PlatformAffected.Android)]
public class Issue32987 : Shell
{
	public Issue32987()
	{
		FlyoutBackgroundColor = Color.FromArgb("#8DECB4");
		FlyoutHeader = new Grid
		{
			HeightRequest = 72,
			AutomationId = "Issue32987FlyoutHeader",
			Children =
			{
				new Label
				{
					Text = "Flyout Header",
					AutomationId = "Issue32987FlyoutHeaderLabel",
					TextColor = Colors.Black,
					HorizontalOptions = LayoutOptions.Center,
					VerticalOptions = LayoutOptions.Center
				}
			}
		};

		var page = new ContentPage
		{
			Title = "Issue 32987",
			Content = new Grid
			{
				Padding = 24,
				Children =
				{
					new Button
					{
						Text = "Open Flyout",
						AutomationId = "Issue32987OpenFlyoutButton",
						HorizontalOptions = LayoutOptions.Center,
						VerticalOptions = LayoutOptions.Center,
						Command = new Command(() => FlyoutIsPresented = true)
					}
				}
			}
		};

		Items.Add(new FlyoutItem
		{
			Title = "Home",
			Items =
			{
				new ShellContent
				{
					Title = "Home",
					Content = page
				}
			}
		});
	}
}
