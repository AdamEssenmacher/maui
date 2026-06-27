using Foundation;
using Microsoft.Maui;

namespace ShellSectionRendererCompletionTasksRetentionRepro;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
