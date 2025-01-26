using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicBoxSynchronizer
{
	public class LocalFileSystemRepository : MonitorableRepository
	{
		public const string DefaultRepositoryPath = @"C:\MusicBoxDrive";

		const string ManifestStateFileName = "local_drive_manifest";

		Manifest _manifest;

		public LocalFileSystemRepository()
			: this(DefaultRepositoryPath)
		{
		}

		public LocalFileSystemRepository(string rootPath)
		{
			_rootPath = rootPath;
		}

		public void Initialize()
		{
			if (!Directory.Exists(_rootPath))
				Directory.CreateDirectory(_rootPath);

			_watcher = new FileSystemWatcher(_rootPath);
			_watcher.NotifyFilter =
				NotifyFilters.FileName |
				NotifyFilters.DirectoryName |
				NotifyFilters.LastWrite |
				NotifyFilters.Size;

			_watcher.Created += watcher_Created;
			_watcher.Changed += watcher_Changed;
			_watcher.Deleted += watcher_Deleted;
			_watcher.Renamed += watcher_Renamed;

			_watcher.IncludeSubdirectories = true;

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

				var manifest = Manifest.Build(_rootPath!);

				manifest.SaveTo(ManifestStateFileName);

				return manifest;
			}

			_manifest = TryLoadManifest() ?? BuildManifest();
		}

		string _rootPath;
		FileSystemWatcher _watcher;

		public string RootPath => _rootPath;

		static readonly char[] DirectorySeparatorChars =
			[
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar,
			];

		string GetFullPath(string path)
		{
			if (path.Split(DirectorySeparatorChars).Contains(".."))
				throw new Exception("Path may not contain '..' components");

			path = path.TrimStart(DirectorySeparatorChars);

			if (Path.IsPathRooted(path))
				throw new Exception("Path may not be rooted");

			return Path.Combine(_rootPath, path);
		}

		public override bool DoesFolderExist(string path)
		{
			return Directory.Exists(GetFullPath(path));
		}

		public override bool DoesFileExist(ManifestFileInfo fileInfo)
		{
			string fullPath = GetFullPath(fileInfo.FilePath);

			if (!File.Exists(fullPath))
				return false;

			if (new FileInfo(fullPath).Length != fileInfo.FileSize)
				return false;

			var localManifestFileInfo = _manifest.GetFileInfo(fileInfo.FilePath);

			if (localManifestFileInfo == null)
				return false;

			if (localManifestFileInfo.FileSize != fileInfo.FileSize)
				return false;
			if (localManifestFileInfo.MD5Checksum != fileInfo.MD5Checksum)
				return false;

			return true;
		}

		public override IEnumerable<string> EnumerateFolders() => _manifest.EnumerateFolders();
		public override IEnumerable<ManifestFileInfo> EnumerateFiles() => _manifest.EnumerateFiles();

		public override string CreateFolder(string path)
		{
			OnDiagnosticOutput("Ensuring folder exists: " + path);

			Directory.CreateDirectory(GetFullPath(path));

			return path;
		}

		public override void CreateOrUpdateFile(string path, Stream content)
		{
			OnDiagnosticOutput("Creating or updating file: " + path);

			using (var fileStream = File.OpenWrite(GetFullPath(path)))
			{
				content.CopyTo(fileStream);
				fileStream.SetLength(fileStream.Position);
			}
		}

		public override void MoveFile(string oldPath, string newPath)
		{
			OnDiagnosticOutput("Moving/renaming file: " + newPath + " (<- " + oldPath + ")");

			File.Move(
				GetFullPath(oldPath),
				GetFullPath(newPath));
		}

		public override void RemoveFile(string path)
		{
			OnDiagnosticOutput("Removing file: " + path);

			File.Delete(GetFullPath(path));
		}

		public override Stream GetFileContentStream(string path)
		{
			string temporaryLocation = Path.GetTempFileName();

			OnDiagnosticOutput("Creating snapshot of file content: " + path);

			File.Copy(path, temporaryLocation, overwrite: true);

			return new TemporaryFileStream(temporaryLocation, FileMode.Open, FileAccess.Read);
		}

		public override void StartMonitor()
		{
			_watcher.EnableRaisingEvents = true;
		}

		public override void StopMonitor()
		{
			_watcher.EnableRaisingEvents = false;
		}

		void watcher_Created(object sender, FileSystemEventArgs e)
		{
			OnDiagnosticOutput("Received filesystem event: Created - " + e.FullPath);
			RaiseChangedEvent(ChangeType.Created, e.FullPath);
		}

		void watcher_Changed(object sender, FileSystemEventArgs e)
		{
			OnDiagnosticOutput("Received filesystem event: Changed - " + e.FullPath);
			RaiseChangedEvent(ChangeType.Modified, e.FullPath);
		}

		void watcher_Deleted(object sender, FileSystemEventArgs e)
		{
			OnDiagnosticOutput("Received filesystem event: Removed - " + e.FullPath);
			RaiseChangedEvent(ChangeType.Removed, e.FullPath);
		}

		void watcher_Renamed(object sender, RenamedEventArgs e)
		{
			OnDiagnosticOutput("Received filesystem event: Renamed - " + e.FullPath);
			RaiseChangedEvent(ChangeType.Renamed, e.FullPath, e.OldFullPath);
		}

		bool IsDirectorySeparatorChar(char ch)
		{
			return (ch == Path.DirectorySeparatorChar) || (ch == Path.AltDirectorySeparatorChar);
		}

		protected override void OnChangeDetected(ChangeInfo change)
		{
			base.OnChangeDetected(change);

			string fullPath = GetFullPath(change.FilePath);

			long fileSize = -1;
			DateTime modifiedTimeUTC = DateTime.MinValue;

			if (File.Exists(fullPath))
			{
				var fileInfo = new FileInfo(fullPath);

				fileSize = fileInfo.Length;
				modifiedTimeUTC = fileInfo.LastWriteTimeUtc;
			}

			_manifest.RegisterChange(
				change,
				fileSize,
				modifiedTimeUTC);

			_manifest.SaveTo(ManifestStateFileName);
		}

		void RaiseChangedEvent(ChangeType changeType, string fullPath, string? oldFullPath = null)
		{
			if (!fullPath.StartsWith(_rootPath) || !IsDirectorySeparatorChar(fullPath[_rootPath.Length]))
				return;

			if (oldFullPath != null)
			{
				if (!oldFullPath.StartsWith(_rootPath) || !IsDirectorySeparatorChar(oldFullPath[_rootPath.Length]))
					return;
			}

			string relativePath = fullPath.Substring(_rootPath.Length);
			string? oldRelativePath = (oldFullPath == null) ? null : oldFullPath.Substring(_rootPath.Length);

			string md5Checksum = File.Exists(fullPath) ? MD5Utility.ComputeChecksum(fullPath) : "-";

			if (changeType == ChangeType.Removed)
			{
				OnChangeDetected(new ChangeInfo(
					sourceRepository: this,
					changeType: ChangeType.Removed,
					filePath: relativePath));
			}
			else if (oldFullPath == null)
			{
				OnChangeDetected(new ChangeInfo(
					sourceRepository: this,
					changeType: changeType,
					filePath: relativePath,
					isFolder: Directory.Exists(fullPath),
					md5Checksum: md5Checksum));
			}
			else
			{
				OnChangeDetected(new ChangeInfo(
					sourceRepository: this,
					changeType: changeType,
					filePath: relativePath,
					oldFilePath: oldFullPath,
					isFolder: Directory.Exists(fullPath)));
			}
		}
	}
}
