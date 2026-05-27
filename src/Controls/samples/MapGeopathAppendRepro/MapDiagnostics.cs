#if ANDROID || MONOANDROID
using System.Collections;
using System.Reflection;
using AndroidPolylineOptions = Android.Gms.Maps.Model.PolylineOptions;
using NativePolyline = Android.Gms.Maps.Model.Polyline;
#endif
using MauiPolyline = Microsoft.Maui.Controls.Maps.Polyline;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;

namespace MapGeopathAppendRepro;

internal static class MapDiagnostics
{
	public static string PlatformName
	{
		get
		{
#if ANDROID || MONOANDROID
			return "Android";
#elif IOS || __IOS__
			return "iOS";
#else
			return "unsupported";
#endif
		}
	}

	public static bool SupportsRetainedOptionsInspection
	{
		get
		{
#if ANDROID || MONOANDROID
			return true;
#else
			return false;
#endif
		}
	}

	public static bool IsPlatformMapReady(ControlsMap map)
	{
#if ANDROID || MONOANDROID
		var handler = map.Handler;
		if (handler is null)
			return false;

		var mapProperty = handler.GetType().GetProperty("Map", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		return mapProperty?.GetValue(handler) is not null;
#else
		return map.Handler is not null;
#endif
	}

	public static int? GetRetainedOptionsPointCount(MauiPolyline polyline)
	{
#if ANDROID || MONOANDROID
		if (polyline.Handler?.PlatformView is AndroidPolylineOptions options)
			return options.Points?.Count;
#endif
		return null;
	}

	public static int? GetCurrentNativePolylinePointCount(ControlsMap map, MauiPolyline polyline)
	{
#if ANDROID || MONOANDROID
		var handler = map.Handler;
		if (handler is null)
			return null;

		var polylinesField = FindInstanceField(handler.GetType(), "_polylines");
		if (polylinesField?.GetValue(handler) is not IEnumerable nativePolylines)
			return null;

		var targetId = polyline.MapElementId as string;
		int? fallbackCount = null;

		foreach (var item in nativePolylines)
		{
			if (item is not NativePolyline nativePolyline)
				continue;

			fallbackCount = nativePolyline.Points?.Count;

			if (!string.IsNullOrEmpty(targetId) && string.Equals(nativePolyline.Id, targetId, StringComparison.Ordinal))
				return fallbackCount;
		}

		return fallbackCount;
#else
		return null;
#endif
	}

#if ANDROID || MONOANDROID
	static FieldInfo? FindInstanceField(Type type, string name)
	{
		while (type is not null)
		{
			var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
			if (field is not null)
				return field;

			type = type.BaseType!;
		}

		return null;
	}
#endif
}
