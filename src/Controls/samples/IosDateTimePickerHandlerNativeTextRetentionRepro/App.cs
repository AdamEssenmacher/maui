namespace IosDateTimePickerHandlerNativeTextRetentionRepro;

public sealed class App : Application
{
	public App()
	{
		AutoRunSettings.Initialize(Environment.GetCommandLineArgs());
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new ReproPage());
	}
}
