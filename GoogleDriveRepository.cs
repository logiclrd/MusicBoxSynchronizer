using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

using ChangeList = Google.Apis.Drive.v3.Data.ChangeList;

namespace MusicBoxSynchronizer
{
	public class GoogleDriveRepository : MonitorableRepository
	{
		public override string? ToString() => "Google Drive Interface";

		DriveService? _driveService;
		Manifest? _manifest;
		object _threadSync = new object();
		object _selfChangeSync = new object();
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
					catch { }
				}

				return null;
			}

			Manifest BuildManifest()
			{
				OnDiagnosticOutput("Building manifest...");

				var manifest = Manifest.Build(_driveService!);

				manifest.HasChanges = true;

				return manifest;
			}

			_manifest = TryLoadManifest();

			if (_manifest != null)
			{
				if (string.IsNullOrEmpty(_manifest.PageToken))
				{
					OnDiagnosticOutput("WARNING: Page token missing, grabbing a fresh start token");

					var getStartPageTokenRequest = _driveService!.Changes.GetStartPageToken();

					_manifest.PageToken = getStartPageTokenRequest.Execute().StartPageTokenValue;
				}
			}
			else
				_manifest = BuildManifest();

			SaveManifest();
		}

		void EnsureInitialized()
		{
			if ((_driveService == null) || (_manifest == null))
				throw new InvalidOperationException("Repository is not initialized");
		}

		public void SaveManifest()
		{
			if (_manifest?.HasChanges ?? false)
				_manifest.SaveTo(ManifestStateFileName);
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
					new FileDataStore("google_drive_credentials", true));

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
			EnsureInitialized();

			return
				(_manifest!.GetFileID(path) is string fileID) &&
				(_manifest!.GetFolderPath(fileID) != null);
		}

		public override bool DoesFileExist(ManifestFileInfo fileInfo)
		{
			EnsureInitialized();

			if (!(_manifest!.GetFileID(fileInfo.FilePath) is string fileID))
				return false;

			if (!(_manifest.GetFileInfo(fileID) is ManifestFileInfo existingFileInfo))
				return false;

			return
				(existingFileInfo.FilePath == fileInfo.FilePath) &&
				(existingFileInfo.FileSize == fileInfo.FileSize) &&
				(existingFileInfo.MD5Checksum == fileInfo.MD5Checksum);
		}

		public override bool DoesFolderExistInManifest(string path)
		{
			return DoesFolderExist(path);
		}

		public override bool DoesFileExistInManifest(ManifestFileInfo fileInfo)
		{
			return DoesFileExist(fileInfo);
		}

		public override IEnumerable<string> EnumerateFolders()
		{
			EnsureInitialized();

			return _manifest!.EnumerateFolders();
		}

		public override IEnumerable<ManifestFileInfo> EnumerateFiles()
		{
			EnsureInitialized();

			return _manifest!.EnumerateFiles();
		}

		public override string CreateFolder(string path)
		{
			EnsureInitialized();

			if (_manifest!.GetFileID(path) is string fileID)
			{
				if (_manifest!.GetFolderPath(fileID) is not string)
					throw new ArgumentException("Cannot create a folder because there is already a file with the same path: " + path);

				OnDiagnosticOutput("Folder already exists: " + path);

				return fileID;
			}

			OnDiagnosticOutput("Creating folder: " + path);

			var newFolder = new Google.Apis.Drive.v3.Data.File();

			newFolder.Name = PathUtility.GetFileName(path);

			if (PathUtility.GetParentPath(path) is string containerPath)
			{
				string containerFileID = CreateFolder(containerPath);

				newFolder.Parents = [containerFileID];
			}

			var creationRequest = _driveService!.Files.Create(newFolder);

			creationRequest.Fields = "id";

			lock (_selfChangeSync)
			{
				newFolder = creationRequest.Execute();

				RegisterSelfChange(path);

				_manifest.RegisterChange(
					new ChangeInfo(this, ChangeType.Created, path, isFolder: true),
					fileSize: -1,
					DateTime.MinValue);

				return newFolder.Id;
			}
		}

		public override void CreateOrUpdateFile(string filePath, Stream fileContent)
		{
			EnsureInitialized();

			if (_manifest!.GetFileInfo(filePath) is not ManifestFileInfo)
			{
				OnDiagnosticOutput("Creating file: " + filePath);

				var file = new Google.Apis.Drive.v3.Data.File();

				file.Name = PathUtility.GetFileName(filePath);
				file.Parents = new[] { _manifest.GetFileID(PathUtility.GetParentPath(filePath) ?? "") };

				var createRequest = _driveService!.Files.Create(file, fileContent, "application/octet-stream");

				lock (_selfChangeSync)
				{
					createRequest.Upload();

					RegisterSelfChange(filePath);

					fileContent.Position = 0;

					string md5Checksum = MD5Utility.ComputeChecksum(fileContent);

					_manifest.RegisterChange(
						new ChangeInfo(this, ChangeType.Created, filePath, md5Checksum),
						fileContent.Length,
						DateTime.UtcNow);
				}
			}
			else
			{
				OnDiagnosticOutput("Updating file: " + filePath);

				var file = new Google.Apis.Drive.v3.Data.File();

				file.Name = PathUtility.GetFileName(filePath);
				file.Parents = new[] { _manifest.GetFileID(PathUtility.GetParentPath(filePath) ?? "") };

				var updateRequest = _driveService!.Files.Update(file, _manifest.GetFileID(filePath), fileContent, "application/octet-stream");

				lock (_selfChangeSync)
				{
					updateRequest.Upload();

					RegisterSelfChange(filePath);

					fileContent.Position = 0;

					string md5Checksum = MD5Utility.ComputeChecksum(fileContent);

					_manifest.RegisterChange(
						new ChangeInfo(this, ChangeType.Modified, filePath, md5Checksum),
						fileContent.Length,
						DateTime.UtcNow);
				}
			}
		}

		public override void RemoveFile(string filePath)
		{
			EnsureInitialized();

			if (_manifest!.GetFileID(filePath) is string fileID)
			{
				OnDiagnosticOutput("Moving file to trash: " + filePath);

				var file = new Google.Apis.Drive.v3.Data.File();

				file.Trashed = true;

				lock (_selfChangeSync)
				{
					_driveService!.Files.Update(file, fileID).Execute();

					_manifest.RegisterChange(
						new ChangeInfo(this, ChangeType.Removed, filePath),
						fileSize: -1,
						modifiedTimeUTC: default);
				}
			}
		}

		public override void MoveFile(string oldPath, string newPath)
		{
			if (oldPath == newPath)
				return;

			EnsureInitialized();

			if (!(_manifest!.GetFileID(oldPath) is string fileID))
				throw new Exception("No file was found with the specified path: " + oldPath);

			OnDiagnosticOutput("Moving/renaming file: " + newPath + " (<- " + oldPath + ")");

			string oldParent = PathUtility.GetParentPath(oldPath) ?? "";
			string newParent = PathUtility.GetParentPath(newPath) ?? "";

			string? newParentFolderID = _manifest.GetFileID(newParent);

			if (newParentFolderID == null)
				OnDiagnosticOutput("Target folder does not seem to exist");

			string oldName = PathUtility.GetFileName(oldPath);
			string newName = PathUtility.GetFileName(newPath);

			var file = new Google.Apis.Drive.v3.Data.File();

			if (oldName != newName)
				file.Name = newName;

			var updateRequest = _driveService!.Files.Update(file, fileID);

			if (oldParent != newParent)
			{
				updateRequest.RemoveParents = _manifest.GetFileID(oldParent);
				updateRequest.AddParents = _manifest.GetFileID(newParent);
			}

			lock (_selfChangeSync)
			{
				Console.WriteLine("=> Executing request against Google API");

				updateRequest.Execute();

				Console.WriteLine("=> Registering self-change on paths");

				RegisterSelfChange(oldName);
				RegisterSelfChange(newName);

				Console.WriteLine("=> Notifying the manifest");

				_manifest.RegisterMove(oldPath, newPath, this);

				Console.WriteLine("=> Done");
			}
		}

		public override void MoveFolder(string oldPath, string newPath)
			=> MoveFile(oldPath, newPath);

		public override void RemoveFolder(string path)
			=> RemoveFile(path);

		public override Stream GetFileContentStream(string path)
		{
			EnsureInitialized();

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
			lock (_threadSync)
			{
				_stopping = true;
				Monitor.PulseAll(_threadSync);
			}
		}

		AutoResetEvent _pollThreadIdle = new AutoResetEvent(initialState: false);

		public void WaitForPollThreadIdle()
		{
			_pollThreadIdle.WaitOne();
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

					request.Fields = "newStartPageToken, changes(removed, fileId, file(id, name, size, modifiedTime, md5Checksum, parents, mimeType, trashed))";

					request.IncludeRemoved = true;

					ChangeList? changeList;

					try
					{
						changeList = request.Execute();
					}
					catch (HttpRequestException e)
					{
						OnDiagnosticOutput("HTTP request exception: " + e);

						Thread.Sleep(TimeSpan.FromSeconds(10));

						continue;
					}

					OnDiagnosticOutput($"  Results: {changeList.Changes.Count} changes");

					if (!changeList.Changes.Any())
						_pollThreadIdle.Set();
					else
						_pollThreadIdle.Reset();

					foreach (var change in changeList.Changes)
					{
						ChangeInfo? changeInfo;

						lock (_selfChangeSync)
							changeInfo = _manifest.RegisterChange(change, this);

						// If a file is marked Trashed and it's already not in the manifest, no change is returned.
						if (changeInfo != null)
						{
							if (changeInfo.OldFilePath == null)
								OnDiagnosticOutput($"  * {changeInfo.ChangeType}: {changeInfo.FilePath}");
							else
								OnDiagnosticOutput($"  * {changeInfo.ChangeType}: {changeInfo.FilePath} (<- {changeInfo.OldFilePath})");

							OnChangeDetected(changeInfo);
						}
					}

					if (!string.IsNullOrEmpty(changeList.NextPageToken))
					{
						_manifest.PageToken = changeList.NextPageToken;
						OnDiagnosticOutput("Next page token: " + _manifest.PageToken);
					}
					else if (!string.IsNullOrEmpty(changeList.NewStartPageToken))
					{
						_manifest.PageToken = changeList.NewStartPageToken;
						OnDiagnosticOutput("New start page token: " + _manifest.PageToken);
						break;
					}
					else
						OnDiagnosticOutput("ERROR: Google API did not return either of NextPageToken and NewStartPageToken");
				}

				OnDiagnosticOutput("End of batch");

				SaveManifest();

				lock (_threadSync)
					Monitor.Wait(_threadSync, TimeSpan.FromSeconds(5));
			}
		}
	}
}
