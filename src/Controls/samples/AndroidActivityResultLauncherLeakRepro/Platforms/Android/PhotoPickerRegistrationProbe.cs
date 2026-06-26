using System.Reflection;
using AndroidX.Activity;
using AndroidX.Activity.Result;
using JavaObject = Java.Lang.Object;
using JavaSystem = Java.Lang.JavaSystem;

namespace AndroidActivityResultLauncherLeakRepro;

static class PhotoPickerRegistrationProbe
{
	static readonly string[] s_typeNames =
	[
		"Microsoft.Maui.ApplicationModel.PickVisualMediaForResult",
		"Microsoft.Maui.ApplicationModel.PickMultipleVisualMediaForResult"
	];

	public static void RegisterAll(ComponentActivity activity)
	{
		foreach (var instance in GetInstances())
		{
			var register = instance.Type.BaseType?.GetMethod(
				"Register",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?? throw new MissingMethodException(instance.Type.FullName, "Register");

			register.Invoke(instance.Value, [activity]);
		}
	}

	public static void ClearAll(bool unregister)
	{
		foreach (var instance in GetInstances())
		{
			var baseType = instance.Type.BaseType ?? throw new InvalidOperationException("Missing ActivityForResultRequest base type.");
			var launcherField = GetField(baseType, "launcher");
			var registeredActivityField = GetField(baseType, "registeredActivity");
			var tcsField = GetField(baseType, "tcs");

			if (unregister && launcherField.GetValue(instance.Value) is ActivityResultLauncher launcher)
			{
				try
				{
					launcher.Unregister();
				}
				catch
				{
					// The repro only needs to release MAUI's static root. AndroidX may reject unregister after lifecycle teardown.
				}
			}

			launcherField.SetValue(instance.Value, null);
			registeredActivityField.SetValue(instance.Value, null);
			tcsField.SetValue(instance.Value, null);
		}
	}

	public static string DescribeLaunchers()
	{
		var descriptions = new List<string>();

		foreach (var instance in GetInstances())
		{
			var baseType = instance.Type.BaseType ?? throw new InvalidOperationException("Missing ActivityForResultRequest base type.");
			var launcher = GetField(baseType, "launcher").GetValue(instance.Value);
			descriptions.Add($"{instance.Type.Name}={(launcher is null ? "null" : launcher.GetType().FullName)}");
		}

		return string.Join("; ", descriptions);
	}

	public static LauncherActivityRoot InspectLauncherActivityRoot(int expectedActivityIdentityHash)
	{
		var descriptions = new List<string>();
		var referencesExpectedActivity = false;

		foreach (var instance in GetInstances())
		{
			var baseType = instance.Type.BaseType ?? throw new InvalidOperationException("Missing ActivityForResultRequest base type.");
			var launcher = GetField(baseType, "launcher").GetValue(instance.Value);

			if (launcher is not JavaObject launcherObject)
			{
				descriptions.Add($"{instance.Type.Name}=null");
				continue;
			}

			try
			{
				var registry = GetJavaField(launcherObject, "this$0");
				var activity = registry is null ? null : GetJavaField(registry, "this$0");
				var identityHash = activity is null ? 0 : JavaSystem.IdentityHashCode(activity);
				var className = activity?.Class?.Name ?? "null";
				var matches = identityHash == expectedActivityIdentityHash;

				referencesExpectedActivity |= matches;
				descriptions.Add($"{instance.Type.Name}: activityClass={className}, identityHash={identityHash}, matchesProbe={matches}");
			}
			catch (Exception ex)
			{
				descriptions.Add($"{instance.Type.Name}: inspectError={ex.GetType().Name}:{ex.Message}");
			}
		}

		return new LauncherActivityRoot(referencesExpectedActivity, string.Join("; ", descriptions));
	}

	static IEnumerable<(Type Type, object Value)> GetInstances()
	{
		var assembly = typeof(Microsoft.Maui.ApplicationModel.Platform).Assembly;

		foreach (var typeName in s_typeNames)
		{
			var type = assembly.GetType(typeName, throwOnError: true)!;
			var property = type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				?? throw new MissingMemberException(typeName, "Instance");
			yield return (type, property.GetValue(null)!);
		}
	}

	static FieldInfo GetField(Type type, string name) =>
		type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingFieldException(type.FullName, name);

	static JavaObject? GetJavaField(JavaObject owner, string name)
	{
		using var field = owner.Class.GetDeclaredField(name);
		field.Accessible = true;
		return field.Get(owner);
	}

	public readonly record struct LauncherActivityRoot(bool ReferencesExpectedActivity, string Description);
}
