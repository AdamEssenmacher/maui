namespace Maui.Controls.Sample.Issues;

[Issue(IssueTracker.Github, 35257, "Switch custom colors render on iOS 26", PlatformAffected.iOS)]
public class Issue35257 : ContentPage
{
	public Issue35257()
	{
		if (Application.Current is not null)
		{
			Application.Current.UserAppTheme = AppTheme.Light;
		}

		BackgroundColor = Colors.White;
		Content = new VerticalStackLayout
		{
			Padding = new Thickness(30),
			Spacing = 25,
			Children =
			{
				new Switch
				{
					AutomationId = "CustomOffSwitch",
					IsToggled = false,
					OffColor = Colors.Red,
					OnColor = Colors.Green,
					ThumbColor = Colors.Orange
				},
				new Switch
				{
					AutomationId = "DefaultOffSwitch",
					IsToggled = false
				},
				new Switch
				{
					AutomationId = "CustomOnSwitch",
					IsToggled = true,
					OffColor = Colors.Red,
					OnColor = Colors.Green,
					ThumbColor = Colors.Orange
				},
				new Switch
				{
					AutomationId = "DefaultOnSwitch",
					IsToggled = true
				}
			}
		};
	}
}
