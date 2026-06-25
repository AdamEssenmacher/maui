#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Xunit;
using AndroidUri = Android.Net.Uri;

namespace Microsoft.Maui.Essentials.DeviceTests.Shared
{
	using Platform = Microsoft.Maui.ApplicationModel.Platform;

	[Category("FilePicker")]
	public class Android_FilePicker_Tests
	{
		[Fact]
		public void GetResultUris_ReturnsDataUri()
		{
			var uri = AndroidUri.Parse("content://maui.test/data")!;
			using var intent = new Intent();
			intent.SetData(uri);

			Assert.Equal(new[] { uri.ToString() }, GetResultUriStrings(intent));
		}

		[Fact]
		public void GetResultUris_ReturnsClipDataUrisInOrder()
		{
			var first = AndroidUri.Parse("content://maui.test/first")!;
			var second = AndroidUri.Parse("content://maui.test/second")!;
			using var intent = new Intent();
			intent.ClipData = CreateClipData(first, second);

			Assert.Equal(new[] { first.ToString(), second.ToString() }, GetResultUriStrings(intent));
		}

		[Fact]
		public void GetResultUris_PrefersClipDataOverData()
		{
			var data = AndroidUri.Parse("content://maui.test/data")!;
			var first = AndroidUri.Parse("content://maui.test/first")!;
			var second = AndroidUri.Parse("content://maui.test/second")!;
			using var intent = new Intent();
			intent.SetData(data);
			intent.ClipData = CreateClipData(first, data, second, first);

			Assert.Equal(new[] { first.ToString(), data.ToString(), second.ToString(), first.ToString() }, GetResultUriStrings(intent));
		}

		[Fact]
		public void GetResultUris_PreservesDuplicateClipDataUris()
		{
			var first = AndroidUri.Parse("content://maui.test/first")!;
			var second = AndroidUri.Parse("content://maui.test/second")!;
			using var intent = new Intent();
			intent.ClipData = CreateClipData(first, second, first);

			Assert.Equal(new[] { first.ToString(), second.ToString(), first.ToString() }, GetResultUriStrings(intent));
		}

		[Fact]
		public void CreatePickerIntent_RequestsReadGrantWithoutPersistableGrant()
		{
			using var documentIntent = FilePickerImplementation.CreateDocumentPickerIntent(null, allowMultiple: true);
			using var pickerIntent = FilePickerImplementation.CreatePickerIntent(null, allowMultiple: true);

			Assert.Equal(Intent.ActionOpenDocument, documentIntent.Action);
			Assert.Equal(FileMimeTypes.All, documentIntent.Type);
			Assert.True(documentIntent.GetBooleanExtra(Intent.ExtraAllowMultiple, false));
			Assert.True(documentIntent.Flags.HasFlag(ActivityFlags.GrantReadUriPermission));
			Assert.False(documentIntent.Flags.HasFlag(ActivityFlags.GrantPersistableUriPermission));

			Assert.True(pickerIntent.Flags.HasFlag(ActivityFlags.GrantReadUriPermission));
			Assert.False(pickerIntent.Flags.HasFlag(ActivityFlags.GrantPersistableUriPermission));
		}

		[Fact]
		[Trait(Traits.FileProvider, Traits.FeatureSupport.Supported)]
		public async Task CreatePhysicalFileResults_ContentUriWithoutPhysicalPath_CopiesToEssentialsCacheAndPreservesMetadata()
		{
			var fileName = "content-uri-file-result.txt";
			var sourcePath = GetSourcePath(fileName);
			var expected = Encoding.UTF8.GetBytes("The file picker contents.");
			FileResult? result = null;

			try
			{
				var uri = CreateContentUri(fileName, expected);
				result = FilePickerImplementation.CreatePhysicalFileResults(new[] { uri }, requireExtendedAccess: true).Single();

				Assert.True(Path.IsPathRooted(result.FullPath));
				Assert.True(File.Exists(result.FullPath));
				Assert.True(new Java.IO.File(result.FullPath).IsFile);
				Assert.Contains(FileSystemUtils.EssentialsFolderHash, result.FullPath, StringComparison.Ordinal);
				Assert.NotEqual(sourcePath, result.FullPath);
				Assert.Equal(fileName, result.FileName);
				Assert.Equal(FileMimeTypes.TextPlain, result.ContentType);

				DeleteCreatedFile(sourcePath);
				Assert.False(File.Exists(sourcePath));
				Assert.Equal(expected, await ReadAllBytesAsync(await result.OpenReadAsync()));
			}
			finally
			{
				DeleteMaterializedFileDirectory(result?.FullPath);
				DeleteCreatedFile(sourcePath);
			}
		}

		[Fact]
		[Trait(Traits.FileProvider, Traits.FeatureSupport.Supported)]
		public void CreatePhysicalFileResults_ClipDataUris_PreserveOrderAndReadablePaths()
		{
			var fileNames = new[] { "content-data.txt", "content-first.txt", "content-second.txt" };
			var sourcePaths = fileNames.Select(GetSourcePath).ToArray();
			var results = new List<FileResult>();

			try
			{
				var data = CreateContentUri(fileNames[0], Encoding.UTF8.GetBytes("data"));
				var first = CreateContentUri(fileNames[1], Encoding.UTF8.GetBytes("first"));
				var second = CreateContentUri(fileNames[2], Encoding.UTF8.GetBytes("second"));

				using var intent = new Intent();
				intent.SetData(data);
				intent.ClipData = CreateClipData(first, data, second, first);

				results = FilePickerImplementation.CreatePhysicalFileResults(
					FilePickerImplementation.GetResultUris(intent),
					requireExtendedAccess: true);

				Assert.Equal(new[] { "content-first.txt", "content-data.txt", "content-second.txt", "content-first.txt" }, results.Select(result => result.FileName));
				Assert.All(results, result =>
				{
					Assert.True(Path.IsPathRooted(result.FullPath));
					Assert.True(File.Exists(result.FullPath));
					Assert.True(new Java.IO.File(result.FullPath).IsFile);
				});
			}
			finally
			{
				DeleteMaterializedFileDirectories(results);

				foreach (var sourcePath in sourcePaths)
					DeleteCreatedFile(sourcePath);
			}
		}

		[Fact]
		public async Task IntermediateActivity_WaitsForOnResultAsyncBeforeDestroy()
		{
			var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var intermediateDestroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Activity? observedIntermediateActivity = null;
			Task<Intent>? startTask = null;

			void OnActivityStateChanged(object? sender, ActivityStateChangedEventArgs e)
			{
				if (e.Activity is not IntermediateActivity)
					return;

				observedIntermediateActivity ??= e.Activity;
				if (!ReferenceEquals(e.Activity, observedIntermediateActivity))
					return;

				if (e.State == ActivityState.Destroyed)
					intermediateDestroyed.TrySetResult();
			}

			Platform.ActivityStateChanged += OnActivityStateChanged;

			try
			{
				await MainThread.InvokeOnMainThreadAsync(() =>
				{
					var intent = new Intent(Platform.CurrentActivity!, typeof(FilePickerTestResultActivity));
					startTask = IntermediateActivity.StartAsync(
						intent,
						PlatformUtils.requestCodeFilePicker,
						async _ =>
						{
							callbackEntered.TrySetResult();
							await releaseCallback.Task;
						});
				});

				await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

				var destroyedWhileBlocked = await Task.WhenAny(intermediateDestroyed.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
				Assert.NotSame(intermediateDestroyed.Task, destroyedWhileBlocked);

				releaseCallback.TrySetResult();

				var localStartTask = startTask ?? throw new InvalidOperationException("Intermediate activity did not start.");
				await localStartTask.WaitAsync(TimeSpan.FromSeconds(10));
				await intermediateDestroyed.Task.WaitAsync(TimeSpan.FromSeconds(10));
			}
			finally
			{
				releaseCallback.TrySetResult();
				Platform.ActivityStateChanged -= OnActivityStateChanged;
			}
		}

		[Theory]
		[InlineData("content://maui.test/.")]
		[InlineData("content://maui.test/..")]
		public void GetContentFileName_InvalidLastPathSegment_IsSanitized(string uriString)
		{
			var fileName = FileSystemUtils.GetContentFileName(AndroidUri.Parse(uriString)!, materializedExtension: "pdf");

			Assert.False(string.IsNullOrWhiteSpace(fileName));
			Assert.NotEqual(".", fileName);
			Assert.NotEqual("..", fileName);
			Assert.Equal(".pdf", Path.GetExtension(fileName));
			Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _));
		}

		[Fact]
		[Trait(Traits.FileProvider, Traits.FeatureSupport.Supported)]
		public void GetContentType_MaterializedContentType_PrefersMaterializedType()
		{
			var fileName = "provider-type.txt";
			var sourcePath = GetSourcePath(fileName);

			try
			{
				var uri = CreateContentUri(fileName, Encoding.UTF8.GetBytes("provider type"));
				var contentType = FileSystemUtils.GetContentType(
					uri,
					physicalPath: Path.Combine(FileSystem.CacheDirectory, "materialized.pdf"),
					materializedContentType: FileMimeTypes.Pdf);

				Assert.Equal(FileMimeTypes.Pdf, contentType);
			}
			finally
			{
				DeleteCreatedFile(sourcePath);
			}
		}

		[Fact]
		[Trait(Traits.FileProvider, Traits.FeatureSupport.Supported)]
		public void GetContentFileName_MaterializedExtension_PrefersMaterializedExtensionOverProviderMetadata()
		{
			var sourceFileName = "provider-name.txt";
			var sourcePath = GetSourcePath(sourceFileName);

			try
			{
				var uri = CreateContentUri(sourceFileName, Encoding.UTF8.GetBytes("provider name"));
				var fileName = FileSystemUtils.GetContentFileName(uri, materializedExtension: "pdf");

				Assert.Equal("provider-name.pdf", fileName);
			}
			finally
			{
				DeleteCreatedFile(sourcePath);
			}
		}

		static AndroidUri CreateContentUri(string fileName, byte[] contents)
		{
			var filePath = GetSourcePath(fileName);
			if (File.Exists(filePath))
				File.Delete(filePath);

			File.WriteAllBytes(filePath, contents);

			return FileProvider.GetUriForFile(new Java.IO.File(filePath));
		}

		static ClipData CreateClipData(params AndroidUri[] uris)
		{
			var clipData = new ClipData("files", new[] { FileMimeTypes.All }, new ClipData.Item(uris[0]));
			foreach (var uri in uris.Skip(1))
				clipData.AddItem(new ClipData.Item(uri));

			return clipData;
		}

		static IEnumerable<string> GetResultUriStrings(Intent intent) =>
			FilePickerImplementation.GetResultUris(intent).Select(uri => uri.ToString()!);

		static async Task<byte[]> ReadAllBytesAsync(Stream stream)
		{
			using (stream)
			using (var memoryStream = new MemoryStream())
			{
				await stream.CopyToAsync(memoryStream);
				return memoryStream.ToArray();
			}
		}

		static string GetSourcePath(string fileName) =>
			Path.Combine(FileSystem.CacheDirectory, fileName);

		static void DeleteMaterializedFileDirectories(IEnumerable<FileResult> results)
		{
			foreach (var result in results ?? Enumerable.Empty<FileResult>())
				DeleteMaterializedFileDirectory(result?.FullPath);
		}

		static void DeleteMaterializedFileDirectory(string? fullPath)
		{
			if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathRooted(fullPath))
				return;

			var materializedDirectory = Path.GetDirectoryName(Path.GetFullPath(fullPath));
			if (string.IsNullOrWhiteSpace(materializedDirectory))
				return;

			var tempDirectory = new DirectoryInfo(materializedDirectory);
			var essentialsDirectory = tempDirectory.Parent;
			if (essentialsDirectory == null ||
				!string.Equals(essentialsDirectory.Name, FileSystemUtils.EssentialsFolderHash, StringComparison.Ordinal) ||
				!Guid.TryParseExact(tempDirectory.Name, "N", out _))
				return;

			if (Directory.Exists(tempDirectory.FullName))
				Directory.Delete(tempDirectory.FullName, recursive: true);
		}

		static void DeleteCreatedFile(string filePath)
		{
			if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
				File.Delete(filePath);
		}
	}
}
