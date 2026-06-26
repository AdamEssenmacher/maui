#nullable enable

using Android.App;
using Android.Content;
using Android.OS;

namespace IntermediateActivityPendingTaskLeakRepro;

[Activity(Exported = false)]
public class NoopResultActivity : Activity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		SetResult(Result.Ok, new Intent());
		Finish();
	}
}
