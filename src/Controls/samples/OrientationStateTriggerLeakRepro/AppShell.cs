namespace OrientationStateTriggerLeakRepro;

public sealed class AppShell : Shell
{
	public const string OrientationStateTriggerLeakRoute = "orientation-state-trigger-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(OrientationStateTriggerLeakRoute, typeof(OrientationStateTriggerLeakPage));

		Items.Add(new ShellContent
		{
			Title = "OrientationStateTrigger Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
