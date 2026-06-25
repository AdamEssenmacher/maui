namespace PickerItemsSourceLeakRepro;

public sealed class AppShell : Shell
{
	public const string PickerLeakRoute = "picker-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(PickerLeakRoute, typeof(PickerLeakPage));

		Items.Add(new ShellContent
		{
			Title = "Picker ItemsSource Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
