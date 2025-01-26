using System;
using System.Collections.Generic;
using System.IO;

namespace MusicBoxSynchronizer
{
	public abstract class MonitorableRepository
	{
		public abstract bool DoesFolderExist(string path);
		public abstract bool DoesFileExist(ManifestFileInfo fileInfo);

		public abstract IEnumerable<string> EnumerateFolders();
		public abstract IEnumerable<ManifestFileInfo> EnumerateFiles();

		public abstract string CreateFolder(string path);
		public abstract void CreateOrUpdateFile(string path, Stream content);
		public abstract void RemoveFile(string path);
		public abstract void MoveFile(string oldPath, string newPath);
		public abstract Stream GetFileContentStream(string path);

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
