namespace ShellCanceledPushLeakRepro;

public sealed class CleanupPage : ContentPage
{
	public CleanupPage()
	{
		Title = "Cleanup target";
		Content = new Label
		{
			Text = "Successful Shell navigation target",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center,
			TextColor = Color.FromArgb("#172026")
		};
	}
}
