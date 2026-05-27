namespace MapGeopathAppendRepro;

public sealed class AppShell : Shell
{
	public const string MapMutationRoute = "map-mutation-page";

	public AppShell()
	{
		Routing.RegisterRoute(MapMutationRoute, typeof(MapMutationPage));

		Items.Add(new ShellContent
		{
			Title = "Geopath Append",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
