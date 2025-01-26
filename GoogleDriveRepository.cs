using System;
using System.IO;
using System.Threading;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace MusicBoxSynchronizer
{
	public class GoogleDriveRepository : MonitorableRepository
	{
		DriveService? _driveService;
		Manifest? _manifest;
		object _sync = new object();
		bool _stopping = false;

		public void ConnectToDriveService()
		{
			if (_driveService == null)
			{
				var scopes =
					new string[]
					{
						DriveService.Scope.Drive,
						DriveService.Scope.DriveFile,
						DriveService.Scope.DriveMetadata,
					};

				var userCredentialsTask = GoogleWebAuthorizationBroker.AuthorizeAsync(
					GoogleClientSecrets.FromFile("client_secret.json").Secrets,
					scopes,
					"user",
					CancellationToken.None,
					new FileDataStore("MusicBoxSynchronizer/credentials", false));

				userCredentialsTask.ConfigureAwait(false);

				var userCredentials = userCredentialsTask.Result;

				_driveService = new DriveService(
					new BaseClientService.Initializer()
					{
						HttpClientInitializer = userCredentials,
						ApplicationName = "MusicBox",
					});
			}
		}

		public override void CreateOrUpdateFile(string filePath, Stream fileContent)
		{
			if ((_driveService == null) || (_manifest == null))
				throw new InvalidOperationException("Repository is not connected");

			if (_manifest.GetFileInfo(filePath) is not ManifestFileInfo)
			{
				var file = new Google.Apis.Drive.v3.Data.File();

				file.Name = Path.GetFileName(filePath);
				file.Parents = new[] { _manifest.GetFileID(Path.GetDirectoryName(filePath) ?? "") };

				var createRequest = _driveService.Files.Create(file, fileContent, "application/octet-stream");

				createRequest.Upload();
			}
			else
			{
				var file = new Google.Apis.Drive.v3.Data.File();

				file.Name = Path.GetFileName(filePath);
				file.Parents = new[] { _manifest.GetFileID(Path.GetDirectoryName(filePath) ?? "") };

				var updateRequest = _driveService.Files.Update(file, _manifest.GetFileID(filePath), fileContent, "application/octet-stream");

				updateRequest.Upload();
			}
		}

		public override void RemoveFile(string filePath)
		{
			if ((_driveService == null) || (_manifest == null))
				throw new InvalidOperationException("Repository is not connected");

			if (_manifest.GetFileID(filePath) is string fileID)
				_driveService.Files.Delete(fileID);
		}

		public override void StartMonitor()
		{
			ConnectToDriveService();

			Manifest? TryLoadManifest()
			{
				if (File.Exists("manifest"))
				{
					try
					{
						OnDiagnosticOutput("Loading existing manifest...");
						return Manifest.LoadFrom("manifest");
					}
					catch {}
				}

				return null;
			}

			Manifest BuildManifest()
			{
				OnDiagnosticOutput("Building manifest...");

			 	var manifest = Manifest.Build(_driveService!);

				manifest.SaveTo("manifest");

				return manifest;
			}

			_manifest = TryLoadManifest() ?? BuildManifest();

			_stopping = false;

			new Thread(PollThread).Start();
		}

		public override void StopMonitor()
		{
			lock (_sync)
			{
				_stopping = true;
				Monitor.PulseAll(_sync);
			}
		}

		void PollThread()
		{
			if ((_driveService == null) || (_manifest == null))
				return;

			while (!_stopping)
			{
				OnDiagnosticOutput("");
				OnDiagnosticOutput("Retrieving results");

				while (true)
				{
					OnDiagnosticOutput("- Page token: " + _manifest.PageToken);

					var request = _driveService.Changes.List(_manifest.PageToken);

					request.Fields = "newStartPageToken, changes(removed, fileId, file(id, name, size, modifiedTime, md5Checksum, trashed))";

					request.IncludeRemoved = true;

					var changeList = request.Execute();

					OnDiagnosticOutput($"  Results: {changeList.Changes.Count} changes");

					foreach (var change in changeList.Changes)
					{
						var changeInfo = _manifest.RegisterChange(change);

						// If a file is marked Trashed and it's already not in the manifest, no change is returned.
						if (changeInfo != null)
						{
							if (changeInfo.OldFilePath == null)
								OnDiagnosticOutput($"  * {changeInfo.ChangeType}: {changeInfo.FilePath}");
							else
								OnDiagnosticOutput($"  * {changeInfo.ChangeType}: {changeInfo.FilePath} (<- {changeInfo.OldFilePath})");
						}
					}

					if (!string.IsNullOrEmpty(changeList.NextPageToken))
						_manifest.PageToken = changeList.NextPageToken;
					else
					{
						_manifest.PageToken = changeList.NewStartPageToken;
						break;
					}
				}

				OnDiagnosticOutput("End of batch");

				if (_manifest.HasChanges)
					_manifest.SaveTo("manifest");

				lock (_sync)
					Monitor.Wait(_sync, TimeSpan.FromSeconds(5));
			}
		}
	}
}
