#if ANDROID
using NUnit.Framework;
using UITest.Appium;
using UITest.Core;

namespace Microsoft.Maui.TestCases.Tests.Issues;

public class Issue32987 : _IssuesUITest
{
	public Issue32987(TestDevice testDevice) : base(testDevice)
	{
	}

	public override string Issue => "[Android] Shell status and flyout colors use colorPrimary under edge-to-edge";

	[Test]
	[Category(UITestCategories.Shell)]
	[Order(1)]
	public void ShellStatusBarUsesPrimaryDarkWhenFlyoutClosed()
	{
		App.WaitForElement("Issue32987OpenFlyoutButton");
		VerifyScreenshot("Issue32987_FlyoutClosed", retryTimeout: TimeSpan.FromSeconds(2));
	}

	[Test]
	[Category(UITestCategories.Shell)]
	[Order(2)]
	public void ShellFlyoutUsesFlyoutBackgroundColor()
	{
		App.WaitForElement("Issue32987OpenFlyoutButton");
		App.Tap("Issue32987OpenFlyoutButton");
		App.WaitForElement("Issue32987FlyoutHeaderLabel");
		VerifyScreenshot("Issue32987_FlyoutOpen", retryTimeout: TimeSpan.FromSeconds(2));
	}
}
#endif
