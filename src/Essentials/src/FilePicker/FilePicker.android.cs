using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.Content;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Environment = Android.OS.Environment;
using AndroidUri = Android.Net.Uri;

namespace Microsoft.Maui.Storage
{
	partial class FilePickerImplementation : IFilePicker
	{
		async Task<IEnumerable<FileResult>> PlatformPickAsync(PickOptions options, bool allowMultiple = false)
		{
			// Essentials supports >= API 19 where this action is available
			var action = Intent.ActionOpenDocument;

			var intent = new Intent(action);
			intent.SetType(FileMimeTypes.All);
			intent.PutExtra(Intent.ExtraAllowMultiple, allowMultiple);
			intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);

			var allowedTypes = options?.FileTypes?.Value?.ToArray();
			if (allowedTypes?.Length > 0)
				intent.PutExtra(Intent.ExtraMimeTypes, allowedTypes);

			var pickerIntent = Intent.CreateChooser(intent, options?.PickerTitle ?? "Select file");
			pickerIntent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
			var requireExtendedAccess = !(OperatingSystem.IsAndroidVersionAtLeast(30) && Environment.IsExternalStorageManager);
			var results = new List<FileResult>();

			try
			{
				async Task OnResultAsync(Intent intent)
				{
					var resultUris = GetResultUris(intent).ToList();
					var persistedUris = TakePersistableReadPermissions(resultUris, intent.Flags);

					try
					{
						results = await Task.Run(() => CreatePhysicalFileResults(resultUris, requireExtendedAccess));
					}
					finally
					{
						ReleasePersistableReadPermissions(persistedUris);
					}
				}

				await IntermediateActivity.StartAsync(pickerIntent, PlatformUtils.requestCodeFilePicker, OnResultAsync);
				return results;
			}
			catch (OperationCanceledException)
			{
				return [];
			}
		}

		internal static IEnumerable<AndroidUri> GetResultUris(Intent intent)
		{
			var uris = new List<AndroidUri>();
			var seenUris = new HashSet<string>(StringComparer.Ordinal);

			AddUri(intent.Data);

			if (intent.ClipData != null)
			{
				for (var i = 0; i < intent.ClipData.ItemCount; i++)
				{
					var uri = intent.ClipData.GetItemAt(i)?.Uri;
					AddUri(uri);
				}
			}

			return uris;

			void AddUri(AndroidUri uri)
			{
				if (uri == null)
					return;

				var uriString = uri.ToString();
				if (!seenUris.Add(uriString))
					return;

				uris.Add(uri);
			}
		}

		internal static List<FileResult> CreatePhysicalFileResults(IEnumerable<AndroidUri> uris, bool requireExtendedAccess)
		{
			var resultList = new List<FileResult>();

			foreach (var uri in uris ?? Enumerable.Empty<AndroidUri>())
			{
				if (uri == null)
					continue;

				resultList.Add(CreatePhysicalFileResult(uri, requireExtendedAccess));
			}

			return resultList;
		}

		static FileResult CreatePhysicalFileResult(AndroidUri uri, bool requireExtendedAccess)
		{
			if (string.Equals(uri.Scheme, FileSystemUtils.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
				return new FileResult(uri.Path);

			if (string.Equals(uri.Scheme, FileSystemUtils.UriSchemeContent, StringComparison.OrdinalIgnoreCase))
			{
				var path = FileSystemUtils.ResolvePhysicalPath(uri, requireExtendedAccess);
				if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !FileSystemUtils.IsFileReadable(path))
				{
					var materialized = FileSystemUtils.MaterializeContentFile(uri);
					if (string.IsNullOrWhiteSpace(materialized?.FullPath) || !Path.IsPathRooted(materialized.FullPath) || !FileSystemUtils.IsFileReadable(materialized.FullPath))
						throw new FileNotFoundException($"Unable to resolve absolute path or retrieve contents of URI '{uri}'.");

					return new FileResult(materialized.FullPath)
					{
						FileName = materialized.FileName,
						ContentType = materialized.ContentType,
					};
				}

				var result = new FileResult(path)
				{
					FileName = FileSystemUtils.GetContentFileName(uri, path),
					ContentType = FileSystemUtils.GetContentType(uri, path),
				};

				return result;
			}

			var physicalPath = FileSystemUtils.EnsurePhysicalPath(uri, requireExtendedAccess);
			return new FileResult(physicalPath);
		}

		static List<AndroidUri> TakePersistableReadPermissions(IEnumerable<AndroidUri> uris, ActivityFlags resultFlags)
		{
			var readPermission = resultFlags & ActivityFlags.GrantReadUriPermission;
			if (readPermission == 0)
				return [];

			var persistedUris = new List<AndroidUri>();
			var seenUris = new HashSet<string>(StringComparer.Ordinal);

			foreach (var uri in uris ?? Enumerable.Empty<AndroidUri>())
			{
				if (uri == null || !string.Equals(uri.Scheme, FileSystemUtils.UriSchemeContent, StringComparison.OrdinalIgnoreCase))
					continue;

				var uriString = uri.ToString();
				if (!seenUris.Add(uriString))
					continue;

				try
				{
					Platform.AppContext.ContentResolver.TakePersistableUriPermission(uri, readPermission);
					persistedUris.Add(uri);
				}
				catch
				{
					// Not all providers return persistable grants.
				}
			}

			return persistedUris;
		}

		static void ReleasePersistableReadPermissions(IEnumerable<AndroidUri> uris)
		{
			foreach (var uri in uris ?? Enumerable.Empty<AndroidUri>())
			{
				try
				{
					Platform.AppContext.ContentResolver.ReleasePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
				}
				catch
				{
					// Ignore providers that revoke the grant as soon as the document is closed.
				}
			}
		}
	}

	public partial class FilePickerFileType
	{
		static FilePickerFileType PlatformImageFileType() =>
			new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
			{
				{ DevicePlatform.Android, new[] { FileMimeTypes.ImagePng, FileMimeTypes.ImageJpg } }
			});

		static FilePickerFileType PlatformPngFileType() =>
			new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
			{
				{ DevicePlatform.Android, new[] { FileMimeTypes.ImagePng } }
			});

		static FilePickerFileType PlatformJpegFileType() =>
			new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
			{
				{ DevicePlatform.Android, new[] { FileMimeTypes.ImageJpg } }
			});

		static FilePickerFileType PlatformVideoFileType() =>
			new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
			{
				{ DevicePlatform.Android, new[] { FileMimeTypes.VideoAll } }
			});

		static FilePickerFileType PlatformPdfFileType() =>
			new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
			{
				{ DevicePlatform.Android, new[] { FileMimeTypes.Pdf } }
			});
	}
}
