namespace FormattedTextLeakRepro;

public sealed class App : Application
{
	public App()
	{
		foreach (var resource in RichTextCatalog.CreateApplicationResources())
			Resources.Add(resource.Key, resource.Value);
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
