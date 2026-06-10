using Microsoft.Maui.Controls.Shapes;

namespace PathDataSharedGeometryLeakRepro;

public sealed class App : Application
{
	public const string SharedPathGeometryResourceKey = "SharedPathGeometry";
	public const string SharedScaleTransformResourceKey = "SharedScaleTransform";

	public App()
	{
		Resources.Add(SharedPathGeometryResourceKey, PathDataCardFactory.CreateSharedGeometry());
		Resources.Add(SharedScaleTransformResourceKey, new ScaleTransform(1, 1, 12, 12));
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
