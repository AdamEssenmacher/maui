using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CoreGraphics;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using UIKit;
using Xunit;
using static Microsoft.Maui.DeviceTests.AssertHelpers;

namespace Microsoft.Maui.DeviceTests
{
	[Category(TestCategory.TabbedPage)]
	public partial class TabbedPageTests
	{
		[Theory]
		[InlineData(true, false)]
		[InlineData(false, true)]
		[InlineData(true, true)]
		public async Task ExplicitSizeRequestsDoNotConstrainTabbedPageRoot(bool setWidthRequest, bool setHeightRequest)
		{
			SetupBuilder();

			const double explicitRequest = 100;
			var tabbedPage = CreateBasicTabbedPage();

			if (setWidthRequest)
				tabbedPage.WidthRequest = explicitRequest;

			if (setHeightRequest)
				tabbedPage.HeightRequest = explicitRequest;

			await CreateHandlerAndAddToWindow(tabbedPage, async () =>
			{
				await AssertEventually(() =>
				{
					if (!TryGetPlatformView(tabbedPage, out var platformView))
						return false;

					var parentView = platformView.Superview;

					return parentView is not null &&
						parentView.Bounds.Width > explicitRequest &&
						parentView.Bounds.Height > explicitRequest &&
						tabbedPage.Frame.Width > explicitRequest &&
						tabbedPage.Frame.Height > explicitRequest &&
						platformView.Bounds.Width > explicitRequest &&
						platformView.Bounds.Height > explicitRequest;
				}, timeout: 5000);

				if (!TryGetPlatformView(tabbedPage, out var platformView))
					throw new InvalidOperationException("TabbedPage platform view was not created.");

				var parentView = platformView.Superview ??
					throw new InvalidOperationException("TabbedPage platform view was not added to a parent.");

				var parentBounds = parentView.Bounds;
				AssertBoundsMatch(parentBounds, tabbedPage.Frame);
				AssertBoundsMatch(parentBounds, platformView.Bounds);
			});
		}

		static bool TryGetPlatformView(TabbedPage tabbedPage, out UIView platformView)
		{
			platformView = (tabbedPage.Handler as IPlatformViewHandler)?.PlatformView;
			return platformView is not null;
		}

		static void AssertBoundsMatch(CGRect expected, Rect actual)
		{
			const double tolerance = 1;

			Assert.InRange(Math.Abs(actual.X - expected.X), 0, tolerance);
			Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, tolerance);
			Assert.InRange(Math.Abs(actual.Width - expected.Width), 0, tolerance);
			Assert.InRange(Math.Abs(actual.Height - expected.Height), 0, tolerance);
		}

		static void AssertBoundsMatch(CGRect expected, CGRect actual)
		{
			const double tolerance = 1;

			Assert.InRange(Math.Abs(actual.X - expected.X), 0, tolerance);
			Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, tolerance);
			Assert.InRange(Math.Abs(actual.Width - expected.Width), 0, tolerance);
			Assert.InRange(Math.Abs(actual.Height - expected.Height), 0, tolerance);
		}

		UITabBar GetTabBar(TabbedPage tabbedPage)
		{
			var pagerParent = (tabbedPage.CurrentPage.Handler as IPlatformViewHandler)
				.PlatformView.FindParent(x => x.NextResponder is UITabBarController);

			return pagerParent.Subviews.FirstOrDefault(v => v.GetType() == typeof(UITabBar)) as UITabBar;
		}

		async Task ValidateTabBarIconColor(
			TabbedPage tabbedPage,
			string tabText,
			Color iconColor,
			bool hasColor)
		{
			if (hasColor)
			{
				await AssertionExtensions.AssertTabItemIconContainsColor(
					GetTabBar(tabbedPage),
					tabText, iconColor, MauiContext);
			}
			else
			{
				await AssertionExtensions.AssertTabItemIconDoesNotContainColor(
					GetTabBar(tabbedPage),
					tabText, iconColor, MauiContext);
			}
		}

		async Task ValidateTabBarTextColor(
			TabbedPage tabbedPage,
			string tabText,
			Color iconColor,
			bool hasColor)
		{
			if (hasColor)
			{
				await AssertionExtensions.AssertTabItemTextContainsColor(
					GetTabBar(tabbedPage),
					tabText, iconColor, MauiContext);
			}
			else
			{
				await AssertionExtensions.AssertTabItemTextDoesNotContainColor(
					GetTabBar(tabbedPage),
					tabText, iconColor, MauiContext);
			}
		}
	}
}
