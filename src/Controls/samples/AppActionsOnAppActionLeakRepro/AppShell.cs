namespace AppActionsOnAppActionLeakRepro;

public sealed class AppShell : Shell
{
	public AppShell()
	{
		Items.Add(new ShellContent
		{
			Title = "AppActions Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
