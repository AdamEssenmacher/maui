namespace Maui35154BackRepro;

static class BackResult
{
	public const string FileName = "back-result.txt";

	public static void Write(string value)
	{
		var text = $"{value} {DateTimeOffset.UtcNow:O}";
		File.WriteAllText(Path.Combine(FileSystem.AppDataDirectory, FileName), text);
#if ANDROID
		Android.Util.Log.Info("Maui35154BackRepro", text);
#endif
	}
}
