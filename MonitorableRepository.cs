using System;
using System.IO;

namespace MusicBoxSynchronizer
{
	public abstract class MonitorableRepository
	{
		public abstract void CreateOrUpdateFile(string path, Stream content);
		public abstract void RemoveFile(string path);

		public abstract void StartMonitor();
		public abstract void StopMonitor();

		public event EventHandler<string>? DiagnosticOutput;
		public event EventHandler<ChangeInfo>? ChangeDetected;

		protected void OnDiagnosticOutput(string text)
		{
			DiagnosticOutput?.Invoke(this, text);
		}

		protected void OnChangeDetected(ChangeInfo change)
		{
			ChangeDetected?.Invoke(this, change);
		}
	}
}
