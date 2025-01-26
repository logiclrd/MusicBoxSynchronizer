using System;

namespace MusicBoxSynchronizer
{
	public class Program
	{
		static void Main()
		{
			var repositories =
				[
					new GoogleDriveRepository(),
					new LocalFileSystemRepository(),
				];

			// TODO: how to suppress feedback, where the act of propagating a change to a repository
			//       generates a detection itself?

			foreach (var repository in repositories)
			{
				repository.ChangeDetected +=
					(sender, change) =>
					{
						// TODO
					};

				repository.StartMonitor();
			}

			// TODO: proper service scaffolding

			Console.WriteLine("Press enter to exit");
			Console.ReadLine();
		}
	}
}
