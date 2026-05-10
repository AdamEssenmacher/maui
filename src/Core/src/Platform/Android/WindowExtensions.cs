using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Util;
using Android.Views;
using AndroidX.Core.View;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using AColor = Android.Graphics.Color;

namespace Microsoft.Maui
{
	public static partial class WindowExtensions
	{
		internal static void UpdateTitle(this Activity platformWindow, IWindow window)
		{
			if (string.IsNullOrEmpty(window.Title))
				platformWindow.Title = ApplicationModel.AppInfo.Current.Name;
			else
				platformWindow.Title = window.Title;
		}

		internal static DisplayOrientation GetOrientation(this IWindow? window)
		{
			if (window == null)
				return DeviceDisplay.Current.MainDisplayInfo.Orientation;

			return window.Handler?.MauiContext?.GetPlatformWindow()?.Resources?.Configuration?.Orientation switch
			{
				Orientation.Landscape => DisplayOrientation.Landscape,
				Orientation.Portrait => DisplayOrientation.Portrait,
				Orientation.Square => DisplayOrientation.Portrait,
				_ => DisplayOrientation.Unknown
			};
		}

		internal static void UpdateWindowSoftInputModeAdjust(this IWindow platformView, SoftInput inputMode)
		{
			var activity = platformView?.Handler?.PlatformView as Activity ??
							platformView?.Handler?.MauiContext?.GetPlatformWindow();

			activity?
				.Window?
				.SetSoftInputMode(inputMode);
		}

		//TODO : Make it public in NET 11.
		internal static void ConfigureTranslucentSystemBars(
			this Window? window,
			Activity activity,
			Color? statusBarBackgroundColor = null,
			Color? navigationBarBackgroundColor = null)
		{
			if (window is null)
			{
				return;
			}

			// Set appropriate system bar appearance for readability using API 30+ methods
			var windowInsetsController = WindowCompat.GetInsetsController(window, window.DecorView);
			if (windowInsetsController is not null)
			{
				windowInsetsController.AppearanceLightStatusBars =
					activity.ShouldUseLightSystemBarAppearance(statusBarBackgroundColor);
				windowInsetsController.AppearanceLightNavigationBars =
					activity.ShouldUseLightSystemBarAppearance(navigationBarBackgroundColor);
			}
		}

		internal static Color? GetDefaultStatusBarBackgroundColor(this Activity activity)
		{
			return activity.GetThemeColor(Resource.Attribute.colorPrimaryDark);
		}

		internal static bool ShouldUseLightSystemBarAppearance(this Activity activity, Color? backgroundColor = null)
		{
			if (backgroundColor?.Alpha >= 1f)
			{
				return GetPerceivedLuminance(backgroundColor) > 0.5f;
			}

			return activity.IsLightTheme();
		}

		static float GetPerceivedLuminance(Color color)
		{
			return 0.2126f * color.Red + 0.7152f * color.Green + 0.0722f * color.Blue;
		}

		internal static Color? GetThemeColor(this Activity activity, int attribute)
		{
			if (activity.Theme is null)
			{
				return null;
			}

			using var value = new TypedValue();
			if (!activity.Theme.ResolveAttribute(attribute, value, true))
			{
				return null;
			}

			return new AColor(activity.GetThemeAttrColor(attribute)).ToColor();
		}

		static bool IsLightTheme(this Activity activity)
		{
			var configuration = activity.Resources?.Configuration;
			return configuration is null ||
				(configuration.UiMode & UiMode.NightMask) != UiMode.NightYes;
		}
	}
}
