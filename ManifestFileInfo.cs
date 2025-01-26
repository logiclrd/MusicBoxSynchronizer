using System;
using System.IO;
using System.Linq;

using File = Google.Apis.Drive.v3.Data.File;

namespace MusicBoxSynchronizer
{
	public class ManifestFileInfo
	{
		public string FilePath;
		public long FileSize;
		public DateTime ModifiedTimeUTC;
		public string MD5Checksum;

		public ManifestFileInfo(string filePath, string md5Checksum)
		{
			FilePath = filePath;
			MD5Checksum = md5Checksum;
		}

		public static ManifestFileInfo LoadFrom(TextReader reader)
		{
			string path = reader.ReadLine() ?? throw new Exception("Unexpected EOF");
			string sizeString = reader.ReadLine() ?? throw new Exception("Unexpected EOF");
			string modifiedTimeUTCTicksString = reader.ReadLine() ?? throw new Exception("Unexpected EOF");
			string md5Checksum = reader.ReadLine() ?? throw new Exception("Unexpected EOF");

			long.TryParse(sizeString, out var size);
			long.TryParse(modifiedTimeUTCTicksString, out var modifiedTimeUTCTicks);

			return
				new ManifestFileInfo(path, md5Checksum)
				{
					FileSize = size,
					ModifiedTimeUTC = new DateTime(modifiedTimeUTCTicks, DateTimeKind.Utc),
				};
		}

		public void SaveTo(TextWriter writer)
		{
			writer.WriteLine(FilePath);
			writer.WriteLine(FileSize);
			writer.WriteLine(ModifiedTimeUTC.Ticks);
			writer.WriteLine(MD5Checksum);
		}

		public static ManifestFileInfo Build(File file, Manifest manifest)
			=> Build(file, file.Parents?.SingleOrDefault(), file, manifest);

		public static ManifestFileInfo Build(File file, string? parentFileID, File actualFile, Manifest manifest)
		{
			string container = "";

			if ((parentFileID != null)
			 && (manifest.GetFolderPath(parentFileID) is string containerPath))
				container = containerPath + "/";

			return
				new ManifestFileInfo(container + file.Name, actualFile.Md5Checksum)
				{
					FileSize = actualFile.Size ?? -1,
					ModifiedTimeUTC = (actualFile.ModifiedTimeDateTimeOffset ?? DateTimeOffset.MinValue).UtcDateTime,
				};
		}

		public ChangeInfo CompareTo(ManifestFileInfo oldFileInfo, MonitorableRepository sourceRepository)
		{
			bool renamed = this.FilePath != oldFileInfo.FilePath;
			bool modified = (this.FileSize != oldFileInfo.FileSize) || (this.MD5Checksum != oldFileInfo.MD5Checksum);

			if (modified && !renamed)
			{
				return new ChangeInfo(
					sourceRepository: sourceRepository,
					changeType: ChangeType.Modified,
					filePath: this.FilePath,
					md5Checksum: this.MD5Checksum);
			}
			else if (renamed && !modified)
			{
				int lastSeparator = this.FilePath.LastIndexOf('/');

				string containerPath = this.FilePath.Substring(0, lastSeparator + 1);

				return new ChangeInfo(
					sourceRepository: sourceRepository,
					changeType:
						oldFileInfo.FilePath.StartsWith(containerPath)
						? ChangeType.Renamed
						: ChangeType.Moved,
					filePath: this.FilePath,
					oldFilePath: oldFileInfo.FilePath,
					md5Checksum: this.MD5Checksum,
					oldMD5Checksum: this.MD5Checksum);
			}
			else
			{
				return new ChangeInfo(
					sourceRepository: sourceRepository,
					changeType: ChangeType.MovedAndModified,
					filePath: this.FilePath,
					oldFilePath: oldFileInfo.FilePath,
					md5Checksum: this.MD5Checksum,
					oldMD5Checksum: oldFileInfo.MD5Checksum);
			}
		}

		public ChangeInfo GenerateCreationChangeInfo(MonitorableRepository sourceRepository)
		{
			return new ChangeInfo(
				sourceRepository: sourceRepository,
				changeType: ChangeType.Created,
				filePath: this.FilePath,
				md5Checksum: this.MD5Checksum);
		}
	}
}
