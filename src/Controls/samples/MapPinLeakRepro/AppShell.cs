namespace MapPinLeakRepro;

public sealed class AppShell : Shell
{
	public const string MapPinLeakRoute = "map-pin-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(MapPinLeakRoute, typeof(MapPinLeakPage));

		Items.Add(new ShellContent
		{
			Title = "Map Pin Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
