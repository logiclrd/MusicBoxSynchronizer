using System;
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
			_synchronizer = new Synchronizer();

			_synchronizer.DiagnosticOutput +=
				(_, message) => Console.WriteLine(message);

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
