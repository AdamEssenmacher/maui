namespace GradientBrushGradientStopsLeakRepro;

public sealed class AppShell : Shell
{
	public const string LeakRoute = "gradient-brush-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(LeakRoute, typeof(LeakPage));

		Items.Add(new ShellContent
		{
			Title = "GradientBrush Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
