namespace MusicBoxSynchronizer
{
	public class ChangeInfo
	{
		public ChangeType ChangeType;
		public string FilePath;
		public string? OldFilePath;
		public bool IsFolder;

		public ChangeInfo(ChangeType changeType, string filePath)
		{
			ChangeType = changeType;
			FilePath = filePath;
		}

		public ChangeInfo(ChangeType changeType, string filePath, bool isFolder)
		{
			ChangeType = changeType;
			FilePath = filePath;
			IsFolder = isFolder;
		}

		public ChangeInfo(ChangeType changeType, string filePath, string oldFilePath)
		{
			ChangeType = changeType;
			FilePath = filePath;
			OldFilePath = oldFilePath;
		}

		public ChangeInfo(ChangeType changeType, string filePath, string oldFilePath, bool isFolder)
		{
			ChangeType = changeType;
			FilePath = filePath;
			OldFilePath = oldFilePath;
			IsFolder = isFolder;
		}
	}
}
