namespace GeometryGroupLeakRepro;

public sealed class AppShell : Shell
{
	public const string GeometryLeakRoute = "geometry-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(GeometryLeakRoute, typeof(GeometryLeakPage));

		Items.Add(new ShellContent
		{
			Title = "GeometryGroup Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
