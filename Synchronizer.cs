using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

		void OnDiagnosticOutput(string line)
		{
			DiagnosticOutput?.Invoke(this, line);
		}

		// To be called after ensuring that the Google Drive repository event queue is drained.
		// Pushes local differences that may have happened when we weren't receiving events up
		// to Google Drive. The opposite direction doesn't need this because Google Drive's
		// event queue model is persistent and disconnected; if we're offline for a while,
		// we won't lose any events. Locally, though, we can and do lose events if we're not
		// actively running/monitoring.

		void CheckForLocalChanges(bool remotePrecedence)
		{
			var deletedFolders = new List<string>();

			OnDiagnosticOutput("=> Checking for remote files that don't exist locally");

			bool removingFiles = false;

			var delayAdd = new List<ChangeInfo>();

			foreach (var fileInfo in _googleDriveRepository.EnumerateFiles())
			{
				if (!_localFileSystemRepository.DoesFileExistInManifest(fileInfo, false))
				{
					if (fileInfo.FilePath.StartsWith("My Drive/MusicBox/"))
					{
						OnDiagnosticOutput("   * MUSICBOX (always sync downstream): " + fileInfo.FilePath);

						delayAdd.Add(
							new ChangeInfo(
								sourceRepository: _googleDriveRepository,
								changeType: ChangeType.Created,
								filePath: fileInfo.FilePath));
					}
					else
					{
						if (remotePrecedence)
						{
							OnDiagnosticOutput("   * ADDED: " + fileInfo.FilePath);

							delayAdd.Add(
								new ChangeInfo(
									sourceRepository: _googleDriveRepository,
									changeType: ChangeType.Created,
									filePath: fileInfo.FilePath));
						}
						else
						{
							OnDiagnosticOutput("   * REMOVE: " + fileInfo.FilePath);

							QueueChangeForProcessing(
								new ChangeInfo(
									sourceRepository: _localFileSystemRepository,
									changeType: ChangeType.Removed,
									filePath: fileInfo.FilePath));

							removingFiles = true;
						}
					}
				}
			}

			if (removingFiles)
			{
				OnDiagnosticOutput("=> Waiting for queued file removals to complete");
				WaitForChangeProcessorIdle();
			}

			OnDiagnosticOutput("=> Checking for remote folders that don't exist locally");

			foreach (var folderPath in _googleDriveRepository.EnumerateFolders())
			{
				if (!_localFileSystemRepository.DoesFolderExistInManifest(folderPath))
				{
					if (folderPath.StartsWith("My Drive/MusicBox/") || (folderPath == "My Drive/MusicBox"))
						continue;

					if (remotePrecedence)
					{
						OnDiagnosticOutput("   * REMOVE: " + folderPath);

						QueueChangeForProcessing(
							new ChangeInfo(
								sourceRepository: _localFileSystemRepository,
								changeType: ChangeType.Removed,
								isFolder: true,
								filePath: folderPath));
					}
					else
					{
						OnDiagnosticOutput("   * ADDED: " + folderPath);

						QueueChangeForProcessing(
							new ChangeInfo(
								sourceRepository: _googleDriveRepository,
								changeType: ChangeType.Created,
								isFolder: true,
								filePath: folderPath));
					}
				}
			}

			if (delayAdd.Any())
			{
				OnDiagnosticOutput("=> Waiting for folder creations to process");

				WaitForChangeProcessorIdle();

				OnDiagnosticOutput("=> Queuing file creations");

				delayAdd.ForEach(QueueChangeForProcessing);
			}

			OnDiagnosticOutput("=> Checking for local folders that don't exist remotely");

			List<string> snapshotOfFolders;

			lock (_localFileSystemRepository.Sync)
				snapshotOfFolders = _localFileSystemRepository.EnumerateFolders().ToList();

			bool creatingFolders = false;

			foreach (var folderPath in snapshotOfFolders)
			{
				if (!_googleDriveRepository.DoesFolderExistInManifest(folderPath))
				{
					_googleDriveRepository.DoesFolderExistInManifest(folderPath);

					if (folderPath.StartsWith("My Drive/MusicBox/") || (folderPath == "My Drive/MusicBox"))
					{
						// local exists, remote doesn't (pick remote)
						OnDiagnosticOutput("   * MUSICBOX (always sync downstream): " + folderPath);

						QueueChangeForProcessing(
							new ChangeInfo(
								sourceRepository: _googleDriveRepository,
								changeType: ChangeType.Removed,
								isFolder: true,
								filePath: folderPath));
					}
					else
					{
						OnDiagnosticOutput("   * CREATE: " + folderPath);

						QueueChangeForProcessing(
							new ChangeInfo(
								sourceRepository: _localFileSystemRepository,
								changeType: ChangeType.Created,
								isFolder: true,
								filePath: folderPath));

						creatingFolders = true;
					}
				}
			}

			if (creatingFolders)
			{
				OnDiagnosticOutput("=> Waiting for queued direction creations to complete");
				WaitForChangeProcessorIdle();
			}

			OnDiagnosticOutput("=> Checking for local files that don't exist remotely");

			List<ManifestFileInfo> snapshotOfFiles;

			lock (_localFileSystemRepository.Sync)
				snapshotOfFiles = _localFileSystemRepository.EnumerateFiles().ToList();

			foreach (var fileInfo in snapshotOfFiles)
			{
				if (!_googleDriveRepository.DoesFileExistInManifest(fileInfo, true))
				{
					if (!_googleDriveRepository.DoesFileExistInManifest(fileInfo, false))
					{
						if (fileInfo.FilePath.StartsWith("My Drive/MusicBox/"))
						{
							// local exist, remote doesn't (pick remote)
							OnDiagnosticOutput("   * MUSICBOX (always sync downstream): " + fileInfo.FilePath);

							QueueChangeForProcessing(
								new ChangeInfo(
									sourceRepository: _googleDriveRepository,
									changeType: ChangeType.Removed,
									filePath: fileInfo.FilePath));
						}
						else
						{
							OnDiagnosticOutput("   * MODIFY: " + fileInfo.FilePath);

							QueueChangeForProcessing(
								new ChangeInfo(
									sourceRepository: _localFileSystemRepository,
									changeType: ChangeType.Modified,
									filePath: fileInfo.FilePath));
						}
					}
					else
					{
						if (fileInfo.FilePath.StartsWith("My Drive/MusicBox/"))
						{
							// local and remote both exist, remote is different (pick remote)
							OnDiagnosticOutput("   * MUSICBOX (always sync downstream): " + fileInfo.FilePath);

							QueueChangeForProcessing(
								new ChangeInfo(
									sourceRepository: _googleDriveRepository,
									changeType: ChangeType.Modified,
									filePath: fileInfo.FilePath));
						}
						else
						{
							OnDiagnosticOutput("   * CREATE: " + fileInfo.FilePath);

							QueueChangeForProcessing(
								new ChangeInfo(
									sourceRepository: _localFileSystemRepository,
									changeType: ChangeType.Created,
									filePath: fileInfo.FilePath));
						}
					}
				}
			}
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
			OnDiagnosticOutput("Startup: Initializing repositories");

			_googleDriveRepository.Initialize();
			_localFileSystemRepository.Initialize();

			OnDiagnosticOutput("Startup: Loading changes from previous session");

			int changeCount = LoadChanges();

			OnDiagnosticOutput("Startup: => " + Plural(changeCount, "change"));

			OnDiagnosticOutput("Startup: Starting change processor");

			StartChangeProcessor();

			foreach (var repository in _repositories)
			{
				OnDiagnosticOutput("Startup: Starting monitor: " + repository.GetType().Name);

				repository.ChangeDetected +=
					(sender, change) => QueueChangeForProcessing(change);

				repository.StartMonitor();
			}

			OnDiagnosticOutput("Startup: Draining remote events");

			_googleDriveRepository.WaitForPollThreadIdle();

			lock (_sync)
				changeCount = _changeProcessorQueue.Count;

			OnDiagnosticOutput("Startup: Waiting for change processor idle (" + changeCount + ")");

			WaitForChangeProcessorIdle();

			OnDiagnosticOutput("Startup: Checking for changes to local state");

			CheckForLocalChanges(remotePrecedence: !_googleDriveRepository.HasContinuity);

			OnDiagnosticOutput("Waiting for stop event");

			_stopEvent.WaitOne();

			OnDiagnosticOutput("Stopping...");

			foreach (var repository in _repositories)
			{
				OnDiagnosticOutput("- " + repository);
				repository.StopMonitor();
			}

			OnDiagnosticOutput("- Change processor");

			RequestChangeProcessorStop();

			WaitForChangeProcessorToExit();

			OnDiagnosticOutput("All done!");

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

		int LoadChanges()
		{
			if (File.Exists("changes"))
			{
				using (var reader = new StreamReader("changes"))
				{
					var countString = reader.ReadLine();

					if (countString == null)
					{
						// ???
						reader.Close();
						File.Delete("changes");
					}

					if (int.TryParse(countString, out var count))
					{
						_changeProcessorQueue.Clear();

						for (int i = 0; i < count; i++)
							_changeProcessorQueue.Enqueue(ChangeInfo.FromString(reader.ReadLine() ?? throw new Exception("Unexpected EOF in changes file"), ResolveRepository));

						return count;
					}
				}
			}

			return 0;
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
			if ((change.FilePath == "My Drive")
			 || (change.FilePath.StartsWith("My Drive/") && (change.FilePath.LastIndexOf('/') == 8)))
			{
				OnDiagnosticOutput("CHANGE TO CORE FOLDER: " + change.FilePath);
				System.Diagnostics.Debugger.Break();
			}

			OnDiagnosticOutput("QUEUE CHANGE: " + change.ChangeType + " " + change.FilePath);

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
			{
				while (_changeProcessorBusy || _changeProcessorQueue.Any())
					Monitor.Wait(_sync);
			}
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
				if (File.Exists("change_processor_thread_crash"))
					File.Delete("change_processor_thread_crash");

				_changeProcessorRunning = true;

				while (_changeProcessorQueue.Any() || !_changeProcessorStopping)
				{
					ChangeInfo? change;

					Console.WriteLine("[CPT] Top of loop, obtaining lock");

					lock (_sync)
					{
						_changeProcessorBusy = false;

						Console.WriteLine("[CPT] Pulse anybody waiting");

						Monitor.PulseAll(_sync);

						Console.WriteLine("[CPT] Serialize changes");

						SaveChanges();

						Console.WriteLine("[CPT] Try to dequeue change");

						while (!_changeProcessorQueue.TryDequeue(out change) && !_changeProcessorStopping)
						{
							Console.WriteLine("[CPT] Sync wait");
							Monitor.Wait(_sync);
						}

						if (change == null)
						{
							if (!_changeProcessorStopping)
								Console.WriteLine("[CPT] DID NOT GET A CHANGE (??)");
							continue;
						}

						OnDiagnosticOutput("DEQUEUED: " + change.ChangeType + " " + change.FilePath);

						_changeProcessorBusy = true;

						Console.WriteLine("[CPT] Checking for recent changes that this supersedes");

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
								{
									Console.WriteLine("[CPT] => forgetting a recent change");
									_recentChanges.RemoveAt(i);
								}
							}
						}

						Console.WriteLine("[CPT] Logging new recent change");

						_recentChanges.Add(new ChangeEvent(this, change));
					}

					Console.WriteLine("[CPT] Process change");
					Console.WriteLine("[CPT] - type: {0}", change.ChangeType);
					if (change.OldFilePath != null)
						Console.WriteLine("[CPT] - path: {0} (<- {1})", change.FilePath, change.OldFilePath);
					else
						Console.WriteLine("[CPT] - path: {0}", change.FilePath);

					ProcessChange(change);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[CPT] CRASH: " + e);

				var timestamped = "change_processor_thread_crash." + DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");

				File.WriteAllText("change_processor_thread_crash", e.ToString());
				File.WriteAllText(timestamped, e.ToString());
			}
			finally
			{
				lock (_sync)
				{
					_changeProcessorBusy = false;
					_changeProcessorRunning = false;
					Monitor.PulseAll(_sync);
				}

				if (!_changeProcessorStopping)
				{
					void RestartChangeProcessor()
					{
						Thread.Sleep(30000);

						if (!_changeProcessorRunning)
							StartChangeProcessor();
					}

					var restartThread = new Thread(RestartChangeProcessor);

					restartThread.IsBackground = true;
					restartThread.Start();
				}
			}
		}

		void ProcessChange(ChangeInfo change)
		{
			foreach (var repository in _repositories)
			{
				while (true)
				{
					try
					{
						if (repository != change.SourceRepository)
						{
							if (change.IsFile)
							{
								switch (change.ChangeType)
								{
									case ChangeType.Created:
									case ChangeType.Modified:
									{
										try
										{
											using (var contentStream = change.SourceRepository.GetFileContentStream(change.FilePath))
											{
												try
												{
													repository.CreateOrUpdateFile(change.FilePath, contentStream);
												}
												catch (Exception e)
												{
													OnDiagnosticOutput("Failed to push file: " + change.FilePath);
													OnDiagnosticOutput("  to repository: " + repository.GetType().Name);
													OnDiagnosticOutput(e.ToString());
													throw;
												}
											}
										}
										catch (Exception e)
										{
											OnDiagnosticOutput("Failed to obtain file content stream for: " + change.FilePath);
											OnDiagnosticOutput(e.ToString());
											throw;
										}

										break;
									}
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
					catch (TaskCanceledException) when (!_changeProcessorStopping)
					{
						// Retry
						continue;
					}

					// Don't retry
					break;
				}
			}
		}

		static string Plural(int count, string singular)
			=> Plural(count, singular, singular + "s");

		static string Plural(int count, string singular, string plural)
		{
			if (count == 1)
				return "1 " + singular;
			else
				return count + " " + plural;
		}
	}
}
