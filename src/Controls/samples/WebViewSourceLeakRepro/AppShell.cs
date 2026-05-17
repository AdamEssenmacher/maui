namespace WebViewSourceLeakRepro;

public sealed class AppShell : Shell
{
	public const string WebViewLeakRoute = "webview-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(WebViewLeakRoute, typeof(WebViewLeakPage));

		Items.Add(new ShellContent
		{
			Title = "WebViewSource Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
