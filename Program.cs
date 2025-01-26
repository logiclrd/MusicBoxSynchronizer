using System;
using System.Collections.Generic;
using System.Threading;

namespace MusicBoxSynchronizer
{
	public class Program
	{
		static void Main()
		{
			MonitorableRepository[] repositories =
				[
					new GoogleDriveRepository(),
					new LocalFileSystemRepository(),
				];

			foreach (var repository in repositories)
			{
				repository.ChangeDetected +=
					(sender, change) =>
					{
						lock (s_sync)
						{
							s_changesToProcess.Enqueue(change);
							Monitor.PulseAll(s_sync);
						}
					};

				repository.StartMonitor();
			}

			// TODO: proper service scaffolding

			Console.WriteLine("Press enter to exit");
			Console.ReadLine();
		}

			// TODO: how to suppress feedback, where the act of propagating a change to a repository
			//       generates a detection itself?

		static object s_sync = new object();
		static Queue<ChangeInfo> s_changesToProcess = new Queue<ChangeInfo>();
	}
}
