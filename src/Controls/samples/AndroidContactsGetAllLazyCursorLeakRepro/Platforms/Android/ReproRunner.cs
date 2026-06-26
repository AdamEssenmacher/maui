using System.Collections;
using System.Reflection;
using Android.Content;
using Android.Database;
using Android.Provider;
using Microsoft.Maui.ApplicationModel.Communication;
using CommonDataKinds = Android.Provider.ContactsContract.CommonDataKinds;
using MauiContacts = Microsoft.Maui.ApplicationModel.Communication.Contacts;

namespace AndroidContactsGetAllLazyCursorLeakRepro;

static class ReproRunner
{
	const string ResultFileName = "autorun-results.txt";
	const int Iterations = 80;
	const int SeedContactCount = 120;
	const string SeedPrefix = "MAUI C082 cursor repro";

	public static async Task RunAsync(MainActivity activity)
	{
		try
		{
			await Task.Delay(500);

			EnsurePermission(activity, global::Android.Manifest.Permission.ReadContacts);
			EnsurePermission(activity, global::Android.Manifest.Permission.WriteContacts);
			SeedContacts(activity, SeedContactCount);

			var full = await RunFullEnumerationControlAsync();
			var abandoned = await RunAbandonedEnumerableAsync();
			var partial = await RunPartialDisposedEnumeratorAsync();

			ForceFullGc();

			var proven =
				full.OpenAfter == 0 &&
				abandoned.OpenAfter == Iterations &&
				partial.OpenAfter == Iterations;

			var lines = new[]
			{
				$"RESULT: {(proven ? "PROVEN" : "INCONCLUSIVE")}",
				full.ToString(),
				abandoned.ToString(),
				partial.ToString(),
				$"seedContactCount={SeedContactCount}",
				$"iterations={Iterations}",
				$"dotnet-version={Environment.Version}"
			};

			WriteResults(activity, lines);
		}
		catch (Exception ex)
		{
			WriteResults(activity, ["RESULT: ERROR", ex.ToString()]);
		}
		finally
		{
			await Task.Delay(250);
			global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
		}
	}

	static void EnsurePermission(Context context, string permission)
	{
		if (AndroidX.Core.Content.ContextCompat.CheckSelfPermission(context, permission) != Android.Content.PM.Permission.Granted)
			throw new InvalidOperationException($"{permission} is not granted. Install and run with adb pm grant before launch.");
	}

	static void SeedContacts(Context context, int desiredCount)
	{
		var resolver = context.ContentResolver ?? throw new InvalidOperationException("No ContentResolver.");
		var existing = CountSeedContacts(resolver);
		for (var i = existing; i < desiredCount; i++)
			InsertContact(resolver, i);
	}

	static int CountSeedContacts(ContentResolver resolver)
	{
		using var cursor = resolver.Query(
			ContactsContract.Contacts.ContentUri!,
			new[] { ContactsContract.Contacts.InterfaceConsts.DisplayName },
			$"{ContactsContract.Contacts.InterfaceConsts.DisplayName} LIKE ?",
			new[] { $"{SeedPrefix}%" },
			null);

		return cursor?.Count ?? 0;
	}

	static void InsertContact(ContentResolver resolver, int index)
	{
		var displayName = $"{SeedPrefix} {index:D4} {new string('x', 512)}";
		var operations = new List<ContentProviderOperation>
		{
			ContentProviderOperation
				.NewInsert(ContactsContract.RawContacts.ContentUri!)
				!.WithValue(ContactsContract.RawContacts.InterfaceConsts.AccountType, null)
				!.WithValue(ContactsContract.RawContacts.InterfaceConsts.AccountName, null)
				!.Build()!,
			ContentProviderOperation
				.NewInsert(ContactsContract.Data.ContentUri!)
				!.WithValueBackReference(ContactsContract.Data.InterfaceConsts.RawContactId, 0)
				!.WithValue(ContactsContract.Data.InterfaceConsts.Mimetype, CommonDataKinds.StructuredName.ContentItemType)
				!.WithValue(CommonDataKinds.StructuredName.DisplayName, displayName)
				!.Build()!
		};

		resolver.ApplyBatch(ContactsContract.Authority, operations);
	}

	static async Task<ScenarioResult> RunFullEnumerationControlAsync()
	{
		var retained = new List<IEnumerable<Contact>>();
		var openBefore = 0;
		var openAfter = 0;
		var rowsRead = 0;

		for (var i = 0; i < Iterations; i++)
		{
			var contacts = await MauiContacts.Default.GetAllAsync();
			retained.Add(contacts);
			openBefore += CountOpenCursors(contacts);

			foreach (var _ in contacts)
				rowsRead++;

			openAfter += CountOpenCursors(contacts);
		}

		return new ScenarioResult("control-full-enumeration", retained.Count, openBefore, openAfter, rowsRead);
	}

	static async Task<ScenarioResult> RunAbandonedEnumerableAsync()
	{
		var retained = new List<IEnumerable<Contact>>();
		var openBefore = 0;

		for (var i = 0; i < Iterations; i++)
		{
			var contacts = await MauiContacts.Default.GetAllAsync();
			retained.Add(contacts);
			openBefore += CountOpenCursors(contacts);
		}

		ForceFullGc();

		var openAfter = retained.Sum(CountOpenCursors);
		return new ScenarioResult("current-abandoned-enumerable", retained.Count, openBefore, openAfter, 0);
	}

	static async Task<ScenarioResult> RunPartialDisposedEnumeratorAsync()
	{
		var retained = new List<IEnumerable<Contact>>();
		var openBefore = 0;
		var rowsRead = 0;

		for (var i = 0; i < Iterations; i++)
		{
			var contacts = await MauiContacts.Default.GetAllAsync();
			retained.Add(contacts);
			openBefore += CountOpenCursors(contacts);

			using var enumerator = contacts.GetEnumerator();
			if (enumerator.MoveNext())
				rowsRead++;
		}

		ForceFullGc();

		var openAfter = retained.Sum(CountOpenCursors);
		return new ScenarioResult("current-first-row-then-dispose-enumerator", retained.Count, openBefore, openAfter, rowsRead);
	}

	static int CountOpenCursors(object? root) =>
		FindCursors(root).Count(cursor => !cursor.IsClosed);

	static List<ICursor> FindCursors(object? root)
	{
		var cursors = new List<ICursor>();
		var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
		Walk(root, depth: 0);
		return cursors;

		void Walk(object? value, int depth)
		{
			if (value is null || depth > 6)
				return;

			if (value is ICursor cursor)
			{
				cursors.Add(cursor);
				return;
			}

			var type = value.GetType();
			if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
				return;

			if (!visited.Add(value))
				return;

			if (value is IEnumerable enumerable && value is not Contact)
			{
				if (value is ICollection collection && collection.Count > 200)
					return;
			}

			foreach (var field in GetFields(type))
			{
				if (field.FieldType.IsPrimitive || field.FieldType == typeof(string) || field.FieldType.IsEnum)
					continue;

				object? fieldValue;
				try
				{
					fieldValue = field.GetValue(value);
				}
				catch
				{
					continue;
				}

				Walk(fieldValue, depth + 1);
			}
		}
	}

	static IEnumerable<FieldInfo> GetFields(Type type)
	{
		for (var current = type; current is not null; current = current.BaseType)
		{
			foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				yield return field;
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 5; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	static void WriteResults(Context context, IEnumerable<string> lines)
	{
		var path = Path.Combine(context.FilesDir!.AbsolutePath, ResultFileName);
		File.WriteAllLines(path, lines);
	}

	readonly record struct ScenarioResult(
		string Name,
		int RetainedEnumerables,
		int OpenBefore,
		int OpenAfter,
		int RowsRead)
	{
		public override string ToString() =>
			$"{Name}: retainedEnumerables={RetainedEnumerables}, openCursorsBefore={OpenBefore}, openCursorsAfter={OpenAfter}, rowsRead={RowsRead}";
	}
}
