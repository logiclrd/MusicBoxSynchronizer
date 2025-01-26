using System;
using System.Collections.Generic;
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

		const string ManifestStateFileName = "google_drive_manifest";

		public void Initialize()
		{
			ConnectToDriveService();

			Manifest? TryLoadManifest()
			{
				if (File.Exists(ManifestStateFileName))
				{
					try
					{
						OnDiagnosticOutput("Loading existing manifest...");
						return Manifest.LoadFrom(ManifestStateFileName);
					}
					catch {}
				}

				return null;
			}

			Manifest BuildManifest()
			{
				OnDiagnosticOutput("Building manifest...");

				var manifest = Manifest.Build(_driveService!);

				manifest.SaveTo(ManifestStateFileName);

				return manifest;
			}

			_manifest = TryLoadManifest() ?? BuildManifest();
		}

		void EnsureConnected()
		{
			if ((_driveService == null) || (_manifest == null))
				throw new InvalidOperationException("Repository is not connected");
		}

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

		public override bool DoesFolderExist(string path)
		{
			EnsureConnected();

			return
				(_manifest!.GetFileID(path) is string fileID) &&
				(_manifest!.GetFolderPath(fileID) != null);
		}


		public override bool DoesFileExist(ManifestFileInfo fileInfo)
		{
			EnsureConnected();

			if (!(_manifest!.GetFileID(fileInfo.FilePath) is string fileID))
				return false;

			if (!(_manifest.GetFileInfo(fileID) is ManifestFileInfo existingFileInfo))
				return false;

			return
				(existingFileInfo.FilePath == fileInfo.FilePath) &&
				(existingFileInfo.FileSize == fileInfo.FileSize) &&
				(existingFileInfo.MD5Checksum == fileInfo.MD5Checksum);
		}

		public override IEnumerable<string> EnumerateFolders()
		{
			EnsureConnected();

			return _manifest!.EnumerateFolders();
		}

		public override IEnumerable<ManifestFileInfo> EnumerateFiles()
		{
			EnsureConnected();

			return _manifest!.EnumerateFiles();
		}

		public override string CreateFolder(string path)
		{
			EnsureConnected();

			if (_manifest!.GetFileID(path) is string fileID)
			{
				if (_manifest!.GetFolderPath(fileID) is not string)
					throw new ArgumentException("Cannot create a folder because there is already a file with the same path: " + path);

				OnDiagnosticOutput("Folder already exists: " + path);

				return fileID;
			}

			OnDiagnosticOutput("Creating folder: " + path);

			var newFolder = new Google.Apis.Drive.v3.Data.File();

			newFolder.Name = Path.GetFileName(path);

			if (Path.GetDirectoryName(path) is string containerPath)
			{
				string containerFileID = CreateFolder(containerPath);

				newFolder.Parents = [containerFileID];
			}

			var creationRequest = _driveService!.Files.Create(newFolder);

			creationRequest.Fields = "id";

			newFolder = creationRequest.Execute();

			return newFolder.Id;
		}

		public override void CreateOrUpdateFile(string filePath, Stream fileContent)
		{
			EnsureConnected();

			if (_manifest!.GetFileInfo(filePath) is not ManifestFileInfo)
			{
				OnDiagnosticOutput("Creating file: " + filePath);

				var file = new Google.Apis.Drive.v3.Data.File();

				file.Name = Path.GetFileName(filePath);
				file.Parents = new[] { _manifest.GetFileID(Path.GetDirectoryName(filePath) ?? "") };

				var createRequest = _driveService!.Files.Create(file, fileContent, "application/octet-stream");

				createRequest.Upload();
			}
			else
			{
				OnDiagnosticOutput("Updating file: " + filePath);

				var file = new Google.Apis.Drive.v3.Data.File();

				file.Name = Path.GetFileName(filePath);
				file.Parents = new[] { _manifest.GetFileID(Path.GetDirectoryName(filePath) ?? "") };

				var updateRequest = _driveService!.Files.Update(file, _manifest.GetFileID(filePath), fileContent, "application/octet-stream");

				updateRequest.Upload();
			}
		}

		public override void RemoveFile(string filePath)
		{
			EnsureConnected();

			if (_manifest!.GetFileID(filePath) is string fileID)
			{
				OnDiagnosticOutput("Deleting file: " + filePath);
				_driveService!.Files.Delete(fileID);
			}
		}

		public override void MoveFile(string oldPath, string newPath)
		{
			if (oldPath == newPath)
				return;

			EnsureConnected();

			if (!(_manifest!.GetFileID(oldPath) is string fileID))
				throw new Exception("No file was found with the specified path: " + oldPath);

			OnDiagnosticOutput("Moving/renaming file: " + newPath + " (<- " + oldPath + ")");

			string oldParent = Path.GetDirectoryName(oldPath) ?? "";
			string newParent = Path.GetDirectoryName(newPath) ?? "";

			string oldName = Path.GetFileName(oldPath);
			string newName = Path.GetFileName(newPath);

			var getRequest = _driveService!.Files.Get(fileID);

			var file = getRequest.Execute();

			if (oldName != newName)
				file.Name = newName;

			var updateRequest = _driveService.Files.Update(file, fileID);

			if (oldParent != newParent)
			{
				updateRequest.RemoveParents = _manifest.GetFileID(oldParent);
				updateRequest.AddParents = _manifest.GetFileID(newParent);
			}

			updateRequest.Execute();
		}

		public override Stream GetFileContentStream(string path)
		{
			EnsureConnected();

			if (!(_manifest!.GetFileID(path) is string fileID))
				throw new FileNotFoundException("No file was found with the specified path: " + path);

			OnDiagnosticOutput("Retrieving file: " + path);

			var get = _driveService!.Files.Get(fileID);

			get.Alt = DriveBaseServiceRequest<Google.Apis.Drive.v3.Data.File>.AltEnum.Media;

			using (var downloadStream = get.ExecuteAsStream())
			{
				var temporaryFileStream = new TemporaryFileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite);

				downloadStream.CopyTo(temporaryFileStream);

				temporaryFileStream.Position = 0;

				return temporaryFileStream;
			}
		}

		public override void StartMonitor()
		{
			Initialize();

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
						var changeInfo = _manifest.RegisterChange(change, this);

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
					_manifest.SaveTo(ManifestStateFileName);

				lock (_sync)
					Monitor.Wait(_sync, TimeSpan.FromSeconds(5));
			}
		}
	}
}
