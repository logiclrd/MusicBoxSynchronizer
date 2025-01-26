using System;
using System.IO;
using System.Linq;

namespace MusicBoxSynchronizer
{
	public class LocalFileSystemRepository : MonitorableRepository
	{
		public const string DefaultRepositoryPath = @"C:\MusicBoxDrive";

		public LocalFileSystemRepository()
			: this(DefaultRepositoryPath)
		{
		}

		public LocalFileSystemRepository(string rootPath)
		{
			_rootPath = rootPath;

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

			return Path.Combine(_rootPath, path);
		}

		public override void CreateOrUpdateFile(string path, Stream content)
		{
			using (var fileStream = File.OpenWrite(path))
				content.CopyTo(fileStream);
		}

		public override void RemoveFile(string path)
		{
			File.Delete(GetFullPath(path));
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
			RaiseChangedEvent(ChangeType.Created, e.FullPath);
		}

		void watcher_Changed(object sender, FileSystemEventArgs e)
		{
			RaiseChangedEvent(ChangeType.Modified, e.FullPath);
		}

		void watcher_Deleted(object sender, FileSystemEventArgs e)
		{
			RaiseChangedEvent(ChangeType.Removed, e.FullPath);
		}

		void watcher_Renamed(object sender, RenamedEventArgs e)
		{
			RaiseChangedEvent(ChangeType.Renamed, e.FullPath, e.OldFullPath);
		}

		bool IsDirectorySeparatorChar(char ch)
		{
			return (ch == Path.DirectorySeparatorChar) || (ch == Path.AltDirectorySeparatorChar);
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
					isFolder: Directory.Exists(fullPath)));
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
