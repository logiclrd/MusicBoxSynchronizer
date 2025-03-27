using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace MusicBoxSynchronizer
{
	public class Program
	{
		static int Main(string[] args)
		{
			Environment.CurrentDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".";

			int _exitCode = 0;

			AppDomain.CurrentDomain.UnhandledException +=
				(_, e) =>
				{
					Console.Error.WriteLine("UNHANDLED EXCEPTION: {0}", e);

					if (OperatingSystem.IsWindows())
						EventLog.WriteEntry("MusicBoxSynchronizer", "Unhandled exception:\n\n" + e, EventLogEntryType.Error);

					_exitCode = 1;
				};

			var service = new Service();


			string runModeArgument = (args.Length > 0) ? args[0] : "/?";

			switch (runModeArgument)
			{
				case "/service":
				{
					if (!OperatingSystem.IsWindows())
					{
						Console.Error.WriteLine("error: /service mode is only available on Windows");
						_exitCode = 3;
						break;
					}

					ServiceBase.Run(service);

					break;
				}
				case "/console":
				{
					service.StartDirect();

					Console.WriteLine("Press enter to stop service");
					Console.ReadLine();

					service.StopDirect();

					break;
				}
				default:
				{
					Console.Error.WriteLine("usage: MusicBoxSynchronizer { /service | /console }");
					_exitCode = 2;
					break;
				}
			}

			return _exitCode;
		}
	}
}
