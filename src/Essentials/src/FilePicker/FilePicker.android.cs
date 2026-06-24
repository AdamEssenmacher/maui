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

			var resultUris = new List<AndroidUri>();
			var persistedUris = new List<AndroidUri>();

			try
			{
				var resultFlags = (ActivityFlags)0;
				void OnResult(Intent intent)
				{
					// The uri returned is only temporary and only lives as long as the Activity that requested it,
					// so this means that it will always be cleaned up by the time we need it because we are using
					// an intermediate activity.
					resultFlags = intent.Flags;
					resultUris.AddRange(GetResultUris(intent));
					persistedUris.AddRange(TakePersistableReadPermissions(resultUris, resultFlags));
				}

				await IntermediateActivity.StartAsync(pickerIntent, PlatformUtils.requestCodeFilePicker, onResult: OnResult);

				bool requireExtendedAccess = !(OperatingSystem.IsAndroidVersionAtLeast(30) && Environment.IsExternalStorageManager);
				return await Task.Run(() => (IEnumerable<FileResult>)CreatePhysicalFileResults(resultUris, requireExtendedAccess));
			}
			catch (OperationCanceledException)
			{
				return [];
			}
			finally
			{
				foreach (var uri in persistedUris)
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
					path = FileSystemUtils.EnsurePhysicalPath(uri, requireExtendedAccess);

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
