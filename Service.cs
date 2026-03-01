using System;
using System.IO;
using System.ServiceProcess;

namespace MusicBoxSynchronizer
{
	class Service : ServiceBase
	{
		public Service()
		{
			if (OperatingSystem.IsWindows())
			{
				ServiceName = "MusicBoxSynchronizer";

				CanStop = true;
				CanPauseAndContinue = false;
			}

			_synchronizer = new Synchronizer();
		}

		Synchronizer _synchronizer;

		protected override void OnStart(string[]? args)
		{
			var startTime = DateTime.Now;

			string logFileName = "MusicBoxSynchronizer " + startTime.ToString("yyyy-MM-dd HH.mm.ss") + ".log";

			string logFilePath = Path.Combine(
				Path.GetDirectoryName(typeof(Service).Assembly.Location) ?? ".",
				logFileName);

			StreamWriter? logFile = null;

			try
			{
				logFile = new StreamWriter(logFilePath, append: true);
			}
			catch { }

			logFile?.WriteLine("--------------------------------");
			logFile?.WriteLine("MusicBoxSynchronizer Start {0:yyyy-MM-dd HH:mm:ss}", startTime);

			_synchronizer = new Synchronizer();

			_synchronizer.DiagnosticOutput +=
				(_, message) =>
				{
					Console.WriteLine(message);
					logFile?.WriteLine(message);
				};

			_synchronizer.Start();
		}

		protected override void OnStop()
		{
			_synchronizer.Stop();
		}

		public void StartDirect() => OnStart(default);
		public void StopDirect() => OnStop();
	}
}
