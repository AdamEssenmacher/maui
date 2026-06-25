namespace DisplayRotationStateTriggerLeakRepro;

public sealed class AppShell : Shell
{
	public const string DisplayRotationStateTriggerLeakRoute = "display-rotation-state-trigger-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(DisplayRotationStateTriggerLeakRoute, typeof(DisplayRotationStateTriggerLeakPage));

		Items.Add(new ShellContent
		{
			Title = "DisplayRotationStateTrigger Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
