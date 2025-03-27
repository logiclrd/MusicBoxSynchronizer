using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MusicBoxSynchronizer
{
	public class Synchronizer
	{
		GoogleDriveRepository _googleDriveRepository = new GoogleDriveRepository();
		LocalFileSystemRepository _localFileSystemRepository = new LocalFileSystemRepository();

		MonitorableRepository[] _repositories;
		Dictionary<string, MonitorableRepository> _repositoryByType;

		public Synchronizer()
		{
			_repositories =
				[
					_googleDriveRepository,
					_localFileSystemRepository,
				];

			_repositoryByType = _repositories.ToDictionary(r => r.GetType().Name);

			WireUpDiagnosticOutput();
		}

		void WireUpDiagnosticOutput()
		{
			foreach (var repository in _repositories)
			{
				string prefix = "[" + repository.GetType().Name + "] ";

				repository.DiagnosticOutput += (sender, text) => DiagnosticOutput?.Invoke(sender, prefix + text);
			}
		}

		public event EventHandler<string>? DiagnosticOutput;

		void EnsureInitialStateOfLocalFiles()
		{
			foreach (var folderPath in _googleDriveRepository.EnumerateFolders())
				if (!_localFileSystemRepository.DoesFolderExist(folderPath))
				{
					_localFileSystemRepository.CreateFolder(folderPath);
					_localFileSystemRepository.RegisterFolder(folderPath);
				}

			foreach (var fileInfo in _googleDriveRepository.EnumerateFiles())
				if (!_localFileSystemRepository.DoesFileExist(fileInfo))
				{
					using (var contentStream = _googleDriveRepository.GetFileContentStream(fileInfo.FilePath))
						_localFileSystemRepository.CreateOrUpdateFile(fileInfo.FilePath, contentStream);

					_localFileSystemRepository.RegisterFile(fileInfo);
				}

			_localFileSystemRepository.SaveManifest();
		}

		ManualResetEvent _stopEvent = new ManualResetEvent(initialState: false);
		ManualResetEvent _stoppedEvent = new ManualResetEvent(initialState: false);

		public void Start()
		{
			_stopEvent.Reset();

			new Thread(Run).Start();
		}

		public void Stop()
		{
			_stoppedEvent.Reset();
			_stopEvent.Set();
			_stoppedEvent.WaitOne();
		}

		void Run()
		{
			_googleDriveRepository.Initialize();
			_localFileSystemRepository.Initialize();

			LoadChanges();

			StartChangeProcessor();

			WaitForChangeProcessorIdle();

			EnsureInitialStateOfLocalFiles();

			foreach (var repository in _repositories)
			{
				repository.ChangeDetected +=
					(sender, change) => QueueChangeForProcessing(change);

				repository.StartMonitor();
			}

			DiagnosticOutput?.Invoke(this, "Waiting for stop event");

			_stopEvent.WaitOne();

			DiagnosticOutput?.Invoke(this, "Stopping...");

			foreach (var repository in _repositories)
			{
				DiagnosticOutput?.Invoke(this, "- " + repository);
				repository.StopMonitor();
			}

			DiagnosticOutput?.Invoke(this, "- Change processor");

			RequestChangeProcessorStop();

			WaitForChangeProcessorToExit();

			DiagnosticOutput?.Invoke(this, "All done!");

			_stoppedEvent.Set();
		}

		MonitorableRepository ResolveRepository(string repositoryType)
		{
			if (_repositoryByType.TryGetValue(repositoryType, out var repository))
				return repository;

			throw new Exception("Can't resolve repository of type: " + repositoryType);
		}

		void SaveChanges()
		{
			using (var writer = new StreamWriter("changes"))
			{
				writer.WriteLine(_changeProcessorQueue.Count);

				foreach (var change in _changeProcessorQueue)
					writer.WriteLine(change.ToString());
			}
		}

		void LoadChanges()
		{
			if (!File.Exists("changes"))
				return;

			using (var reader = new StreamReader("changes"))
			{
				string countString = reader.ReadLine() ?? throw new Exception("Unexpected EOF in changes file");

				if (int.TryParse(countString, out var count))
				{
					_changeProcessorQueue.Clear();

					for (int i=0; i < count; i++)
						_changeProcessorQueue.Enqueue(ChangeInfo.FromString(reader.ReadLine() ?? throw new Exception("Unexpected EOF in changes file"), ResolveRepository));
				}
			}
		}

		bool IsRecentChange(ChangeInfo change)
		{
			var cutoff = DateTime.UtcNow.AddMinutes(-1);

			while (_recentChanges.Count > 0)
			{
				var oldestChange = _recentChanges.First();

				if (oldestChange.TimestampUTC < cutoff)
					_recentChanges.RemoveAt(0);
				else
					break;
			}

			return _recentChanges.Any(evt => evt.ChangeInfo.Equals(change));
		}

		void QueueChangeForProcessing(ChangeInfo change)
		{
			lock (_sync)
			{
				if (change.ChangeType == ChangeType.MovedAndModified)
				{
					QueueChangeForProcessing(new ChangeInfo(
						change.SourceRepository,
						ChangeType.Created,
						change.FilePath,
						change.IsFolder,
						change.MD5Checksum));

					QueueChangeForProcessing(new ChangeInfo(
						change.SourceRepository,
						ChangeType.Removed,
						change.OldFilePath ?? throw new Exception("MovedAndModified change event was created without OldFilePath"),
						change.IsFolder,
						change.OldMD5Checksum ?? throw new Exception("MovedAndModified change event was created without OldMD5Checksum")));

					return;
				}

				if (!IsRecentChange(change))
				{
					_changeProcessorQueue.Enqueue(change);
					Monitor.PulseAll(_sync);

					SaveChanges();
				}
			}
		}

		void StartChangeProcessor()
		{
			var thread = new Thread(ChangeProcessorThreadProc);

			_changeProcessorStopping = false;

			thread.Start();
		}

		void RequestChangeProcessorStop()
		{
			lock (_sync)
			{
				_changeProcessorStopping = true;
				Monitor.PulseAll(_sync);
			}
		}

		void WaitForChangeProcessorIdle()
		{
			lock (_sync)
				while (_changeProcessorBusy || _changeProcessorQueue.Any())
					Monitor.Wait(_sync);
		}

		void WaitForChangeProcessorToExit()
		{
			lock (_sync)
				while (_changeProcessorRunning)
					Monitor.Wait(_sync);
		}

		class ChangeEvent
		{
			Synchronizer _synchronizer;

			public ChangeInfo ChangeInfo;
			public DateTime TimestampUTC;

			public ChangeEvent(Synchronizer synchronizer, ChangeInfo changeInfo)
			{
				_synchronizer = synchronizer;

				ChangeInfo = changeInfo;
				TimestampUTC = DateTime.UtcNow;
			}

			public override string ToString()
			{
				return TimestampUTC.Ticks + " " + ChangeInfo.ToString();
			}

			public static ChangeEvent FromString(Synchronizer synchronizer, string str)
			{
				string[] parts = str.Split(' ', count: 2);

				var ret = new ChangeEvent(synchronizer, ChangeInfo.FromString(parts[1], synchronizer.ResolveRepository));

				if (long.TryParse(parts[0], out var timestampTicks))
					ret.TimestampUTC = new DateTime(timestampTicks, DateTimeKind.Utc);

				return ret;
			}
		}

		object _sync = new object();
		Queue<ChangeInfo> _changeProcessorQueue = new Queue<ChangeInfo>();
		List<ChangeEvent> _recentChanges = new List<ChangeEvent>();
		bool _changeProcessorStopping;
		bool _changeProcessorRunning;
		bool _changeProcessorBusy;

		void ChangeProcessorThreadProc()
		{
			try
			{
				_changeProcessorRunning = true;

				while (_changeProcessorQueue.Any() || !_changeProcessorStopping)
				{
					ChangeInfo? change;

					lock (_sync)
					{
						_changeProcessorBusy = false;

						Monitor.PulseAll(_sync);

						SaveChanges();

						while (!_changeProcessorQueue.TryDequeue(out change) && !_changeProcessorStopping)
							Monitor.Wait(_sync);

						if (change == null)
							continue;

						_changeProcessorBusy = true;

						if ((change.ChangeType == ChangeType.Created)
						 || (change.ChangeType == ChangeType.Removed))
						{
							var forgetChangeType = (change.ChangeType == ChangeType.Created)
								? ChangeType.Removed
								: ChangeType.Created;

							for (int i = _recentChanges.Count - 1; i >= 0; i--)
							{
								var previousChange = _recentChanges[i];

								if ((previousChange.ChangeInfo.FilePath == change.FilePath)
								 && (previousChange.ChangeInfo.ChangeType == forgetChangeType))
									_recentChanges.RemoveAt(i);
							}
						}

						_recentChanges.Add(new ChangeEvent(this, change));
					}

					if (change == null)
						continue;

					ProcessChange(change);
				}
			}
			catch (Exception e)
			{
				File.WriteAllText("change_processor_thread_crash", e.ToString());
			}
			finally
			{
				lock (_sync)
				{
					_changeProcessorBusy = false;
					_changeProcessorRunning = false;
					Monitor.PulseAll(_sync);
				}
			}
		}

		void ProcessChange(ChangeInfo change)
		{
			foreach (var repository in _repositories)
			{
				if (repository != change.SourceRepository)
				{
					if (change.IsFile)
					{
						switch (change.ChangeType)
						{
							case ChangeType.Created:
							case ChangeType.Modified:
								using (var contentStream = change.SourceRepository.GetFileContentStream(change.FilePath))
									repository.CreateOrUpdateFile(change.FilePath, contentStream);
								break;
							case ChangeType.Moved:
							case ChangeType.Renamed:
								repository.MoveFile(
									change.OldFilePath ?? throw new Exception(change.ChangeType + " change event was created without OldFilePath"),
									change.FilePath);
								break;
							case ChangeType.Removed:
								repository.RemoveFile(change.FilePath);
								break;
						}
					}
					else
					{
						switch (change.ChangeType)
						{
							case ChangeType.Created:
								repository.CreateFolder(change.FilePath);
								break;
							case ChangeType.Moved:
							case ChangeType.Renamed:
								repository.MoveFolder(
									change.OldFilePath ?? throw new Exception(change.ChangeType + " change event was created without OldFilePath"),
									change.FilePath);
								break;
							case ChangeType.Removed:
								repository.RemoveFolder(change.FilePath);
								break;
						}
					}
				}
			}
		}
	}
}
