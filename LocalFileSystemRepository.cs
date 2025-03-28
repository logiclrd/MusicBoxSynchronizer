using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MusicBoxSynchronizer
{
	public class LocalFileSystemRepository : MonitorableRepository
	{
		public override string? ToString() => "Local Filesystem Interface";

		public const string DefaultRepositoryPath = @"C:\MusicBoxDrive";

		const string ManifestStateFileName = "local_drive_manifest";

		string _rootPath;
		Manifest? _manifest;
		FileSystemWatcher? _watcher;
		Dictionary<string, DateTime> _lastSelfChangeToPath = new Dictionary<string, DateTime>();

		public LocalFileSystemRepository()
			: this(DefaultRepositoryPath)
		{
		}

		public LocalFileSystemRepository(string rootPath)
		{
			_rootPath = rootPath;
		}

		void EnsureInitialized()
		{
			if (_manifest == null)
				throw new InvalidOperationException("Repository is not initialized");
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
					catch (Exception e)
					{
						OnDiagnosticOutput("Failed to load existing manifest: " + e);
					}
				}

				return null;
			}

			Manifest BuildManifest()
			{
				OnDiagnosticOutput("Building manifest...");

				var manifest = Manifest.Build(_rootPath!);

				manifest.HasChanges = true;

				return manifest;
			}

			_manifest = TryLoadManifest() ?? BuildManifest();

			SaveManifest();
		}

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
			EnsureInitialized();

			string fullPath = GetFullPath(fileInfo.FilePath);

			if (!File.Exists(fullPath))
				return false;

			if (new FileInfo(fullPath).Length != fileInfo.FileSize)
				return false;

			var localManifestFileInfo = _manifest!.GetFileInfo(fileInfo.FilePath);

			if (localManifestFileInfo == null)
				return false;

			if (localManifestFileInfo.FileSize != fileInfo.FileSize)
				return false;
			if (localManifestFileInfo.MD5Checksum != fileInfo.MD5Checksum)
				return false;

			return true;
		}

		public override IEnumerable<string> EnumerateFolders() { EnsureInitialized(); return _manifest!.EnumerateFolders(); }
		public override IEnumerable<ManifestFileInfo> EnumerateFiles() { EnsureInitialized(); return _manifest!.EnumerateFiles(); }

		public override string CreateFolder(string path)
		{
			OnDiagnosticOutput("Ensuring folder exists: " + path);

			Directory.CreateDirectory(GetFullPath(path));

			return path;
		}

		public override void MoveFolder(string oldPath, string newPath)
		{
			OnDiagnosticOutput($"Moving folder: {newPath} (<- {oldPath})");

			var contentItems = _manifest!.EnumerateContents(oldPath).ToList();

			foreach (var fileInfo in contentItems)
			{
				string relativePath = fileInfo.FilePath.Substring(oldPath.Length);
				string newSubpath = newPath + relativePath;

				_lastSelfChangeToPath[fileInfo.FilePath] = DateTime.UtcNow;
				_lastSelfChangeToPath[newSubpath] = DateTime.UtcNow;

				_manifest.RegisterChange(
					new ChangeInfo(this, ChangeType.Moved, newSubpath, fileInfo.FilePath, isFolder: false, fileInfo.MD5Checksum),
					fileSize: fileInfo.FileSize,
					modifiedTimeUTC: fileInfo.ModifiedTimeUTC);
			}

			Directory.Move(
				GetFullPath(oldPath),
				GetFullPath(newPath));
		}

		public override void RemoveFolder(string path)
		{
			OnDiagnosticOutput("Removing folder: " + path);

			var contentItems = _manifest!.EnumerateContents(path).ToList();

			foreach (var fileInfo in contentItems)
			{
				_lastSelfChangeToPath[fileInfo.FilePath] = DateTime.UtcNow;
				_manifest.RegisterChange(
					new ChangeInfo(this, ChangeType.Removed, fileInfo.FilePath, isFolder: false),
					fileSize: fileInfo.FileSize,
					modifiedTimeUTC: fileInfo.ModifiedTimeUTC);
			}

			Directory.Delete(GetFullPath(path), recursive: true);
		}

		public override void CreateOrUpdateFile(string path, Stream content)
		{
			OnDiagnosticOutput("Creating or updating file: " + path);

			_lastSelfChangeToPath[path] = DateTime.UtcNow;

			using (var fileStream = File.Open(GetFullPath(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
			{
				content.CopyTo(fileStream);
				fileStream.SetLength(fileStream.Position);

				fileStream.Position = 0;

				_manifest!.RegisterChange(
					new ChangeInfo(this, ChangeType.Modified, path, isFolder: false, MD5Utility.ComputeChecksum(fileStream)),
					fileSize: fileStream.Length,
					modifiedTimeUTC: DateTime.UtcNow);
			}
		}

		public override void MoveFile(string oldPath, string newPath)
		{
			OnDiagnosticOutput("Moving/renaming file: " + newPath + " (<- " + oldPath + ")");

			string oldFullPath = GetFullPath(oldPath);
			string newFullPath = GetFullPath(newPath);

			bool oldExists = File.Exists(oldFullPath);
			bool newExists = File.Exists(oldFullPath);

			if (!oldExists && !newExists)
				throw new Exception("Received file move/rename event but the file does not seem to exist: " + newPath + " (<- " + oldPath + ")");

			_lastSelfChangeToPath[oldPath] = DateTime.UtcNow;
			_lastSelfChangeToPath[newPath] = DateTime.UtcNow;

			File.Move(
				GetFullPath(oldPath),
				GetFullPath(newPath),
				overwrite: true);

			if (_manifest!.GetFileInfo(oldPath) is ManifestFileInfo fileInfo)
			{
				_manifest.RegisterChange(
					new ChangeInfo(this, ChangeType.Moved, newPath, oldPath, isFolder: false, fileInfo.MD5Checksum),
					fileSize: fileInfo.FileSize,
					modifiedTimeUTC: fileInfo.ModifiedTimeUTC);
			}
		}

		public override void RemoveFile(string path)
		{
			OnDiagnosticOutput("Removing file: " + path);

			_lastSelfChangeToPath[path] = DateTime.UtcNow;

			File.Delete(GetFullPath(path));

			_manifest!.RegisterChange(
				new ChangeInfo(this, ChangeType.Removed, path, isFolder: false),
				fileSize: -1,
				modifiedTimeUTC: default);
		}

		public override Stream GetFileContentStream(string path)
		{
			string temporaryLocation = Path.GetTempFileName();

			OnDiagnosticOutput("Creating snapshot of file content: " + path);

			File.Copy(GetFullPath(path), temporaryLocation, overwrite: true);

			return new TemporaryFileStream(temporaryLocation, FileMode.Open, FileAccess.Read);
		}

		public override void RegisterFolder(string path)
		{
			EnsureInitialized();

			_manifest!.PopulateFolder(path, path);
		}

		public override void RegisterFile(ManifestFileInfo fileInfo)
		{
			EnsureInitialized();

			_manifest!.PopulateFile(fileInfo, fileInfo.FilePath);
		}

		public override void SaveManifest()
		{
			if (_manifest?.HasChanges ?? false)
				_manifest.SaveTo(ManifestStateFileName);
		}

		public override void StartMonitor()
		{
			EnsureInitialized();

			StartQueuePumpThreadProc();

			_watcher!.EnableRaisingEvents = true;
		}

		public override void StopMonitor()
		{
			EnsureInitialized();

			_watcher!.EnableRaisingEvents = false;

			StopQueuePumpThreadProc();
		}

		void watcher_Created(object sender, FileSystemEventArgs e)
		{
			OnDiagnosticOutput("Received filesystem event: Created - " + e.FullPath);
			QueueChangedEvent(ChangeType.Created, e.FullPath);
		}

		void watcher_Changed(object sender, FileSystemEventArgs e)
		{
			OnDiagnosticOutput("Received filesystem event: Changed - " + e.FullPath);
			QueueChangedEvent(ChangeType.Modified, e.FullPath);
		}

		void watcher_Deleted(object sender, FileSystemEventArgs e)
		{
			OnDiagnosticOutput("Received filesystem event: Removed - " + e.FullPath);
			QueueChangedEvent(ChangeType.Removed, e.FullPath);
		}

		void watcher_Renamed(object sender, RenamedEventArgs e)
		{
			OnDiagnosticOutput("Received filesystem event: Renamed - " + e.FullPath);
			QueueChangedEvent(ChangeType.Renamed, e.FullPath, e.OldFullPath);
		}

		bool IsDirectorySeparatorChar(char ch)
		{
			return (ch == Path.DirectorySeparatorChar) || (ch == Path.AltDirectorySeparatorChar);
		}

		protected override void OnChangeDetected(ChangeInfo change)
		{
			base.OnChangeDetected(change);

OnDiagnosticOutput("Processing change to local file: " + change.ChangeType + " " + change.FilePath);

			string fullPath = GetFullPath(change.FilePath);

			long fileSize = -1;
			DateTime modifiedTimeUTC = DateTime.MinValue;

			if (File.Exists(fullPath))
			{
				var fileInfo = new FileInfo(fullPath);

				fileSize = fileInfo.Length;
				modifiedTimeUTC = fileInfo.LastWriteTimeUtc;
			}

OnDiagnosticOutput("Registering change with the local files manifest");

			_manifest!.RegisterChange(
				change,
				fileSize,
				modifiedTimeUTC);

			SaveManifest();
		}

		void QueueChangedEvent(ChangeType changeType, string fullPath, string? oldFullPath = null)
		{
			var queueEntry = new QueueEntry(changeType, fullPath, oldFullPath);

			queueEntry.DueTimeUTC = DateTime.UtcNow + EventCoalesceWindowLength;

			lock (_sync)
			{
				_changedEventQueue.Add(queueEntry);
				Monitor.PulseAll(_sync);
			}
		}

		class QueueEntry
		{
			public ChangeType ChangeType;
			public string FullPath;
			public string? OldFullPath;
			public DateTime DueTimeUTC;

			public QueueEntry(ChangeType changeType, string fullPath, string? oldFullPath = null)
			{
				this.ChangeType = changeType;
				this.FullPath = fullPath;
				this.OldFullPath = oldFullPath;
			}
		}

		static readonly TimeSpan EventCoalesceWindowLength = TimeSpan.FromSeconds(2);

		object _sync = new object();
		bool _stopping = false;
		List<QueueEntry> _changedEventQueue = new List<QueueEntry>();

		void StartQueuePumpThreadProc()
		{
			_stopping = false;

			var thread = new Thread(QueuePumpThreadProc);

			thread.IsBackground = true;
			thread.Start();
		}

		void StopQueuePumpThreadProc()
		{
			lock (_sync)
			{
				_stopping = true;
				Monitor.PulseAll(_sync);
			}
		}

		void QueuePumpThreadProc()
		{
			while (!_stopping)
			{
				lock (_sync)
				{
					var now = DateTime.UtcNow;

					if (_changedEventQueue.Count > 0)
					{
						var nextEvent = _changedEventQueue.First();

						if (nextEvent.DueTimeUTC < now)
							PumpNextEvent();
						else
						{
							var timeout = nextEvent.DueTimeUTC - now;

							Monitor.Wait(_sync, timeout);
						}
					}
					else
						Monitor.Wait(_sync);
				}
			}
		}

		void PumpNextEvent()
		{
			var nextEvent = _changedEventQueue[0];

			_changedEventQueue.RemoveAt(0);

			if ((nextEvent.ChangeType == ChangeType.Created) || (nextEvent.ChangeType == ChangeType.Modified))
			{
				for (int i = 0; i < _changedEventQueue.Count; i++)
				{
					var laterEvent = _changedEventQueue[i];

					if (laterEvent.FullPath == nextEvent.FullPath)
					{
						if (laterEvent.ChangeType == ChangeType.Modified)
						{
							_changedEventQueue.RemoveAt(i);
							i--;
						}
						else if (laterEvent.ChangeType == ChangeType.Removed)
							return;
					}
				}
			}

			RaiseChangedEvent(nextEvent);
		}

		void RaiseChangedEvent(QueueEntry queueEntry)
		{
			ChangeType changeType = queueEntry.ChangeType;
			string fullPath = queueEntry.FullPath;
			string? oldFullPath = queueEntry.OldFullPath;

			if (!fullPath.StartsWith(_rootPath) || !IsDirectorySeparatorChar(fullPath[_rootPath.Length]))
				return;

			if (oldFullPath != null)
			{
				if (!oldFullPath.StartsWith(_rootPath) || !IsDirectorySeparatorChar(oldFullPath[_rootPath.Length]))
					return;
			}

			string relativePath = PathUtility.GetRelativePath(_rootPath, fullPath) ?? throw new Exception("Unable to determine relative path for full path: " + fullPath);
			string? oldRelativePath = PathUtility.GetRelativePath(_rootPath, oldFullPath);

			string md5Checksum = File.Exists(fullPath) ? MD5Utility.ComputeChecksum(fullPath) : "-";

			if (changeType == ChangeType.Removed)
			{
				OnChangeDetected(new ChangeInfo(
					sourceRepository: this,
					changeType: ChangeType.Removed,
					filePath: relativePath));
			}
			else if (oldRelativePath == null)
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
					oldFilePath: oldRelativePath,
					isFolder: Directory.Exists(fullPath)));
			}
		}
	}
}
