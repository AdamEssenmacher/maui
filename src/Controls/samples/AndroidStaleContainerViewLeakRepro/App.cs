namespace Maui.Controls.Sample.AndroidStaleContainerViewLeakRepro;

public class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MonitorPage());
	}
}
