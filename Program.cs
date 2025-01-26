using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MusicBoxSynchronizer
{
	public class Program
	{
		static GoogleDriveRepository s_googleDriveRepository = new GoogleDriveRepository();
		static LocalFileSystemRepository s_localFileSystemRepository = new LocalFileSystemRepository();

		static MonitorableRepository[] s_repositories =
			[
				s_googleDriveRepository,
				s_localFileSystemRepository,
			];

		static Dictionary<string, MonitorableRepository> s_repositoryByType = s_repositories.ToDictionary(r => r.GetType().Name);

		static void WireUpDiagnosticOutput()
		{
			foreach (var repository in s_repositories)
			{
				string prefix = "[" + repository.GetType().Name + "] ";

				repository.DiagnosticOutput += (_, text) => Console.WriteLine(prefix + text);
			}
		}

		static void EnsureInitialStateOfLocalFiles()
		{
			foreach (var folderPath in s_googleDriveRepository.EnumerateFolders())
				if (!s_localFileSystemRepository.DoesFolderExist(folderPath))
					s_localFileSystemRepository.CreateFolder(folderPath);

			foreach (var fileInfo in s_googleDriveRepository.EnumerateFiles())
				if (!s_localFileSystemRepository.DoesFileExist(fileInfo))
					using (var contentStream = s_googleDriveRepository.GetFileContentStream(fileInfo.FilePath))
						s_localFileSystemRepository.CreateOrUpdateFile(fileInfo.FilePath, contentStream);
		}

		static void Main()
		{
			WireUpDiagnosticOutput();

			s_googleDriveRepository.Initialize();
			s_localFileSystemRepository.Initialize();

			LoadChanges();

			StartChangeProcessor();

			WaitForChangeProcessorIdle();

			EnsureInitialStateOfLocalFiles();

			foreach (var repository in s_repositories)
			{
				repository.ChangeDetected +=
					(sender, change) => QueueChangeForProcessing(change);

				repository.StartMonitor();
			}

			// TODO: proper service scaffolding

			Console.WriteLine("Press enter to exit");
			Console.ReadLine();

			RequestChangeProcessorStop();

			WaitForChangeProcessorToExit();
		}

		static MonitorableRepository ResolveRepository(string repositoryType)
		{
			if (s_repositoryByType.TryGetValue(repositoryType, out var repository))
				return repository;

			throw new Exception("Can't resolve repository of type: " + repositoryType);
		}

		static void SaveChanges()
		{
			using (var writer = new StreamWriter("changes"))
			{
				writer.WriteLine(s_changeProcessorQueue.Count);

				foreach (var change in s_changeProcessorQueue)
					writer.WriteLine(change.ToString());
			}
		}

		static void LoadChanges()
		{
			if (!File.Exists("changes"))
				return;

			using (var reader = new StreamReader("changes"))
			{
				string countString = reader.ReadLine() ?? throw new Exception("Unexpected EOF in changes file");

				if (int.TryParse(countString, out var count))
				{
					s_changeProcessorQueue.Clear();

					for (int i=0; i < count; i++)
						s_changeProcessorQueue.Enqueue(ChangeInfo.FromString(reader.ReadLine() ?? throw new Exception("Unexpected EOF in changes file"), ResolveRepository));
				}
			}
		}

		static bool IsRecentChange(ChangeInfo change)
		{
			var cutoff = DateTime.UtcNow.AddMinutes(-1);

			while (s_recentChanges.Count > 0)
			{
				var oldestChange = s_recentChanges.First();

				if (oldestChange.TimestampUTC < cutoff)
					s_recentChanges.RemoveAt(0);
				else
					break;
			}

			return s_recentChanges.Any(evt => evt.ChangeInfo.Equals(change));
		}

		static void QueueChangeForProcessing(ChangeInfo change)
		{
			lock (s_sync)
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
					s_changeProcessorQueue.Enqueue(change);
					Monitor.PulseAll(s_sync);

					SaveChanges();
				}
			}
		}

		static void StartChangeProcessor()
		{
			var thread = new Thread(ChangeProcessorThreadProc);

			s_changeProcessorStopping = false;

			thread.Start();
		}

		static void RequestChangeProcessorStop()
		{
			s_changeProcessorStopping = true;
		}

		static void WaitForChangeProcessorIdle()
		{
			lock (s_changeProcessorSync)
				while (s_changeProcessorBusy || s_changeProcessorQueue.Any())
					Monitor.Wait(s_changeProcessorSync);
		}

		static void WaitForChangeProcessorToExit()
		{
			lock (s_changeProcessorSync)
				while (s_changeProcessorRunning)
					Monitor.Wait(s_changeProcessorSync);
		}

		class ChangeEvent
		{
			public ChangeInfo ChangeInfo;
			public DateTime TimestampUTC;

			public ChangeEvent(ChangeInfo changeInfo)
			{
				ChangeInfo = changeInfo;
				TimestampUTC = DateTime.UtcNow;
			}

			public override string ToString()
			{
				return TimestampUTC.Ticks + " " + ChangeInfo.ToString();
			}

			public static ChangeEvent FromString(string str)
			{
				string[] parts = str.Split(' ', count: 2);

				var ret = new ChangeEvent(ChangeInfo.FromString(parts[1], ResolveRepository));

				if (long.TryParse(parts[0], out var timestampTicks))
					ret.TimestampUTC = new DateTime(timestampTicks, DateTimeKind.Utc);

				return ret;
			}
		}

		static object s_sync = new object();
		static Queue<ChangeInfo> s_changeProcessorQueue = new Queue<ChangeInfo>();
		static List<ChangeEvent> s_recentChanges = new List<ChangeEvent>();
		static bool s_changeProcessorStopping;
		static object s_changeProcessorSync = new object();
		static bool s_changeProcessorRunning;
		static bool s_changeProcessorBusy;

		static void ChangeProcessorThreadProc()
		{
			try
			{
				s_changeProcessorRunning = true;

				while (s_changeProcessorQueue.Any() || !s_changeProcessorStopping)
				{
					ChangeInfo? change;

					lock (s_sync)
					{
						s_changeProcessorBusy = false;

						Monitor.PulseAll(s_sync);

						SaveChanges();

						while (!s_changeProcessorQueue.TryDequeue(out change))
							Monitor.Wait(s_sync);

						s_changeProcessorBusy = true;
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
				lock (s_changeProcessorSync)
				{
					s_changeProcessorBusy = false;
					s_changeProcessorRunning = false;
					Monitor.PulseAll(s_changeProcessorSync);
				}
			}
		}

		static void ProcessChange(ChangeInfo change)
		{
			foreach (var repository in s_repositories)
			{
				if (repository != change.SourceRepository)
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
			}
		}
	}
}
