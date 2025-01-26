using System.Threading;

namespace MusicBoxSynchronizer
{
	public class ChangeInfo
	{
		public readonly MonitorableRepository SourceRepository;
		public readonly ChangeType ChangeType;
		public readonly string FilePath;
		public readonly string? OldFilePath;
		public readonly bool IsFolder;

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, bool isFolder)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			IsFolder = isFolder;
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, string oldFilePath)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			OldFilePath = oldFilePath;
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, string oldFilePath, bool isFolder)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			OldFilePath = oldFilePath;
			IsFolder = isFolder;
		}
	}
}
