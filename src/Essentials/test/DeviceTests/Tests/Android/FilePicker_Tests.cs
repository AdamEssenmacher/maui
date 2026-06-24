using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.Content;
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
			var uri = AndroidUri.Parse("content://maui.test/data");
			using var intent = new Intent();
			intent.SetData(uri);

			Assert.Equal(new[] { uri.ToString() }, GetResultUriStrings(intent));
		}

		[Fact]
		public void GetResultUris_ReturnsClipDataUrisInOrder()
		{
			var first = AndroidUri.Parse("content://maui.test/first");
			var second = AndroidUri.Parse("content://maui.test/second");
			using var intent = new Intent();
			intent.ClipData = CreateClipData(first, second);

			Assert.Equal(new[] { first.ToString(), second.ToString() }, GetResultUriStrings(intent));
		}

		[Fact]
		public void GetResultUris_ReturnsDataBeforeUniqueClipDataUris()
		{
			var data = AndroidUri.Parse("content://maui.test/data");
			var first = AndroidUri.Parse("content://maui.test/first");
			var second = AndroidUri.Parse("content://maui.test/second");
			using var intent = new Intent();
			intent.SetData(data);
			intent.ClipData = CreateClipData(first, data, second, first);

			Assert.Equal(new[] { data.ToString(), first.ToString(), second.ToString() }, GetResultUriStrings(intent));
		}

		[Fact]
		[Trait(Traits.FileProvider, Traits.FeatureSupport.Supported)]
		public async Task CreatePhysicalFileResults_ContentUriWithoutPhysicalPath_CopiesToEssentialsCacheAndPreservesMetadata()
		{
			DeleteEssentialsCacheDirectories();

			var fileName = "content-uri-file-result.txt";
			var sourcePath = Path.Combine(FileSystem.CacheDirectory, fileName);
			var expected = Encoding.UTF8.GetBytes("The file picker contents.");
			var uri = CreateContentUri(fileName, expected);
			var result = FilePickerImplementation.CreatePhysicalFileResults(new[] { uri }, requireExtendedAccess: true).Single();

			Assert.True(Path.IsPathRooted(result.FullPath));
			Assert.True(File.Exists(result.FullPath));
			Assert.True(new Java.IO.File(result.FullPath).IsFile);
			Assert.Contains(FileSystemUtils.EssentialsFolderHash, result.FullPath, StringComparison.Ordinal);
			Assert.NotEqual(sourcePath, result.FullPath);
			Assert.Equal(fileName, result.FileName);
			Assert.Equal(FileMimeTypes.TextPlain, result.ContentType);
			Assert.Equal(expected, await ReadAllBytesAsync(await result.OpenReadAsync()));
		}

		[Fact]
		[Trait(Traits.FileProvider, Traits.FeatureSupport.Supported)]
		public void CreatePhysicalFileResults_DedupedContentUris_PreserveOrderAndReadablePaths()
		{
			DeleteEssentialsCacheDirectories();

			var data = CreateContentUri("content-data.txt", Encoding.UTF8.GetBytes("data"));
			var first = CreateContentUri("content-first.txt", Encoding.UTF8.GetBytes("first"));
			var second = CreateContentUri("content-second.txt", Encoding.UTF8.GetBytes("second"));

			using var intent = new Intent();
			intent.SetData(data);
			intent.ClipData = CreateClipData(first, data, second, first);

			var results = FilePickerImplementation.CreatePhysicalFileResults(
				FilePickerImplementation.GetResultUris(intent),
				requireExtendedAccess: true);

			Assert.Equal(new[] { "content-data.txt", "content-first.txt", "content-second.txt" }, results.Select(result => result.FileName));
			Assert.All(results, result =>
			{
				Assert.True(Path.IsPathRooted(result.FullPath));
				Assert.True(File.Exists(result.FullPath));
				Assert.True(new Java.IO.File(result.FullPath).IsFile);
			});
		}

		[Theory]
		[InlineData("content://maui.test/.")]
		[InlineData("content://maui.test/..")]
		public void GetContentFileName_InvalidLastPathSegment_IsSanitized(string uriString)
		{
			var fileName = FileSystemUtils.GetContentFileName(AndroidUri.Parse(uriString), materializedExtension: "pdf");

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
			var uri = CreateContentUri("provider-type.txt", Encoding.UTF8.GetBytes("provider type"));
			var contentType = FileSystemUtils.GetContentType(
				uri,
				physicalPath: Path.Combine(FileSystem.CacheDirectory, "materialized.pdf"),
				materializedContentType: FileMimeTypes.Pdf);

			Assert.Equal(FileMimeTypes.Pdf, contentType);
		}

		[Fact]
		[Trait(Traits.FileProvider, Traits.FeatureSupport.Supported)]
		public void GetContentFileName_MaterializedExtension_PrefersMaterializedExtensionOverProviderMetadata()
		{
			var uri = CreateContentUri("provider-name.txt", Encoding.UTF8.GetBytes("provider name"));
			var fileName = FileSystemUtils.GetContentFileName(uri, materializedExtension: "pdf");

			Assert.Equal("provider-name.pdf", fileName);
		}

		static AndroidUri CreateContentUri(string fileName, byte[] contents)
		{
			var filePath = Path.Combine(FileSystem.CacheDirectory, fileName);
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
			FilePickerImplementation.GetResultUris(intent).Select(uri => uri.ToString());

		static async Task<byte[]> ReadAllBytesAsync(Stream stream)
		{
			using (stream)
			using (var memoryStream = new MemoryStream())
			{
				await stream.CopyToAsync(memoryStream);
				return memoryStream.ToArray();
			}
		}

		static void DeleteEssentialsCacheDirectories()
		{
			foreach (var cacheDirectory in GetEssentialsCacheDirectories())
			{
				if (Directory.Exists(cacheDirectory))
					Directory.Delete(cacheDirectory, recursive: true);
			}
		}

		static IEnumerable<string> GetEssentialsCacheDirectories()
		{
			var roots = new[]
			{
				Platform.AppContext.CacheDir?.AbsolutePath,
				Platform.AppContext.ExternalCacheDir?.AbsolutePath,
			};

			return roots
				.Where(root => !string.IsNullOrWhiteSpace(root))
				.Select(root => Path.Combine(root, FileSystemUtils.EssentialsFolderHash));
		}
	}
}
