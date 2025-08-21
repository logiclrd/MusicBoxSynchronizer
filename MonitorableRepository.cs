using System;
using System.Collections.Generic;
using System.IO;

namespace MusicBoxSynchronizer
{
	public abstract class MonitorableRepository
	{
		public abstract bool DoesFolderExist(string path);
		public abstract bool DoesFileExist(ManifestFileInfo fileInfo, bool requireExactFile);

		public abstract bool DoesFolderExistInManifest(string path);
		public abstract bool DoesFileExistInManifest(ManifestFileInfo fileInfo, bool requireExactFile);

		public abstract IEnumerable<string> EnumerateFolders();
		public abstract IEnumerable<ManifestFileInfo> EnumerateFiles();

		public abstract string CreateFolder(string path);
		public abstract void MoveFolder(string oldPath, string newPath);
		public abstract void RemoveFolder(string path);

		public abstract void CreateOrUpdateFile(string path, Stream content);
		public abstract void RemoveFile(string path);
		public abstract void MoveFile(string oldPath, string newPath);
		public abstract Stream GetFileContentStream(string path);

		Dictionary<string, DateTime> _lastSelfChangeToPath = new Dictionary<string, DateTime>();

		protected void RegisterSelfChange(string path)
		{
			_lastSelfChangeToPath[path] = DateTime.UtcNow;
		}

		protected DateTime GetLastSelfChangeToPath(params string[] paths)
		{
			DateTime newest = DateTime.MinValue;

			foreach (string path in paths)
			{
				if (_lastSelfChangeToPath.TryGetValue(path, out var timestamp))
				{
					if (newest < timestamp)
						newest = timestamp;
				}
			}

			return newest;
		}

		public virtual void RegisterFolder(string path)
		{
		}

		public virtual void RegisterFile(ManifestFileInfo fileInfo)
		{
		}

		public abstract void StartMonitor();
		public abstract void StopMonitor();

		public event EventHandler<string>? DiagnosticOutput;
		public event EventHandler<ChangeInfo>? ChangeDetected;

		protected void OnDiagnosticOutput(string text)
		{
			DiagnosticOutput?.Invoke(this, text);
		}

		protected virtual void OnChangeDetected(ChangeInfo change)
		{
			ChangeDetected?.Invoke(this, change);
		}
	}
}
