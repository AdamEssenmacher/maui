namespace Maui35154BackRepro;

public sealed class App : Microsoft.Maui.Controls.Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		BackResult.Write("STARTED");
		return new Window(new RootBackInterceptPage());
	}
}
