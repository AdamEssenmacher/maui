#nullable enable

using Android.App;
using Android.Content;
using Microsoft.Maui;

namespace IntermediateActivityPendingTaskLeakRepro;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true)]
public class MainActivity : MauiAppCompatActivity
{
	public const int FailingRequestCode = 48127;

	public static bool ThrowIntermediateLaunchFailures { get; set; }

	public override void StartActivityForResult(Intent? intent, int requestCode)
	{
		if (ThrowIntermediateLaunchFailures &&
			requestCode == FailingRequestCode &&
			intent?.Component?.ClassName?.Contains("IntermediateActivity", System.StringComparison.Ordinal) == true)
		{
			throw new ActivityNotFoundException("Forced repro failure after IntermediateActivity pending task insertion.");
		}

		base.StartActivityForResult(intent, requestCode);
	}
}
