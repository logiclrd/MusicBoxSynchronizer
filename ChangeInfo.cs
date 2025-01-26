using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;

namespace MusicBoxSynchronizer
{
	public class ChangeInfo
	{
		public readonly MonitorableRepository SourceRepository;
		public readonly ChangeType ChangeType;
		public readonly string FilePath;
		public readonly string? OldFilePath;
		public readonly string MD5Checksum;
		public readonly string? OldMD5Checksum;
		public readonly bool IsFolder;

		public override bool Equals(object? obj)
		{
			if (obj is ChangeInfo changeInfo)
				return Equals(changeInfo);
			else
				return false;
		}

		public bool Equals(ChangeInfo other)
		{
			return
				(this.ChangeType == other.ChangeType) &&
				(this.FilePath == other.FilePath) &&
				(this.MD5Checksum == other.MD5Checksum) &&
				(this.IsFolder == other.IsFolder);
		}

		public override int GetHashCode()
		{
			return
				this.ChangeType.GetHashCode() ^
				this.FilePath.GetHashCode() ^
				this.MD5Checksum.GetHashCode() ^
				this.IsFolder.GetHashCode();
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			MD5Checksum = "-";
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, string md5Checksum)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			MD5Checksum = md5Checksum;
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, bool isFolder)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			IsFolder = isFolder;
			MD5Checksum = "-";
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, bool isFolder, string md5Checksum)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			IsFolder = isFolder;
			MD5Checksum = md5Checksum;
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, string oldFilePath, string md5Checksum, string oldMD5Checksum)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			OldFilePath = oldFilePath;
			MD5Checksum = md5Checksum;
			OldMD5Checksum = oldMD5Checksum;
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, string oldFilePath, bool isFolder, string md5Checksum)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			OldFilePath = oldFilePath;
			IsFolder = isFolder;
			MD5Checksum = md5Checksum;
		}

		public ChangeInfo(MonitorableRepository sourceRepository, ChangeType changeType, string filePath, string oldFilePath, bool isFolder)
		{
			SourceRepository = sourceRepository;
			ChangeType = changeType;
			FilePath = filePath;
			OldFilePath = oldFilePath;
			IsFolder = isFolder;
			MD5Checksum = "-";
		}

		public override string ToString()
		{
			if (OldFilePath == null)
				return $"{SourceRepository.GetType().Name} {ChangeType} {MD5Checksum} {IsFolder} \"{FilePath}\"";
			else
				return $"{SourceRepository.GetType().Name} {ChangeType} {MD5Checksum} {IsFolder} \"{FilePath}\" \"{OldFilePath}\"";
		}

		public static ChangeInfo FromString(string str, Func<string, MonitorableRepository> resolveRepository)
		{
			string[] parts = str.Split(' ', count: 5);

			string repositoryType = parts[0];
			string changeTypeString = parts[1];
			string md5Checksum = parts[2];
			string isFolderString = parts[3];

			Enum.TryParse<ChangeType>(changeTypeString, out var changeType);
			bool.TryParse(isFolderString, out var isFolder);

			string PullString(ref string src)
			{
				if (!src.StartsWith('"'))
					throw new Exception("Syntax error in serialized ChangeInfo");

				int endString = src.IndexOf('"', startIndex: 1);

				if (endString < 0)
					throw new Exception("Syntax error in serialized ChangeInfo");

				string extracted = src.Substring(1, endString - 1);

				src = src.Substring(endString + 1).TrimStart();

				return extracted;
			}

			string filePath = PullString(ref parts[4]);

			if (!parts[4].StartsWith('"'))
			{
				return new ChangeInfo(
					resolveRepository(repositoryType),
					changeType,
					filePath,
					isFolder,
					md5Checksum);
			}
			else
			{
				string oldFilePath = PullString(ref parts[4]);

				return new ChangeInfo(
					resolveRepository(repositoryType),
					changeType,
					filePath,
					oldFilePath,
					isFolder,
					md5Checksum);
			}
		}
	}
}
