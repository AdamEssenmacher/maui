namespace BorderDashArrayLeakRepro;

public sealed class App : Application
{
	public const string SharedDashArrayResourceKey = "SharedCardDashArray";

	public App()
	{
		Resources = new ResourceDictionary
		{
			[SharedDashArrayResourceKey] = SharedStrokeDashArray
		};
	}

	public static DoubleCollection SharedStrokeDashArray { get; } = new(new[] { 6d, 3d, 1d, 3d });

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
