namespace AdaptiveTriggerLeakRepro;

public sealed class AppShell : Shell
{
	public const string AdaptiveTriggerLeakRoute = "adaptive-trigger-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(AdaptiveTriggerLeakRoute, typeof(AdaptiveTriggerLeakPage));

		Items.Add(new ShellContent
		{
			Title = "AdaptiveTrigger Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
