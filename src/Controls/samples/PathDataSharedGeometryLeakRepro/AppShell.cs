namespace PathDataSharedGeometryLeakRepro;

public sealed class AppShell : Shell
{
	public const string PathDataLeakRoute = "path-data-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(PathDataLeakRoute, typeof(PathDataLeakPage));

		Items.Add(new ShellContent
		{
			Title = "Path.Data Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
