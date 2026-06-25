#nullable enable
using Android.App;
using Android.Content;
using Android.OS;

namespace Microsoft.Maui.Essentials.DeviceTests.Shared
{
	[Activity(Exported = false, NoHistory = true)]
	public class FilePickerTestResultActivity : Activity
	{
		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);

			SetResult(Result.Ok, new Intent());
			Finish();
		}
	}
}
