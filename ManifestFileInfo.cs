using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;

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
		{
			string container = "";

			if ((file.Parents?.Any() ?? false)
			 && (manifest.GetFolderPath(file.Parents.Single()) is string containerPath))
				container = containerPath + "/";

			return
				new ManifestFileInfo(container + file.Name, file.Md5Checksum)
				{
					FileSize = file.Size ?? -1,
					ModifiedTimeUTC = (file.ModifiedTimeDateTimeOffset ?? DateTimeOffset.MinValue).UtcDateTime,
				};
		}

		public ChangeInfo CompareTo(ManifestFileInfo oldFileInfo)
		{
			bool renamed = this.FilePath != oldFileInfo.FilePath;
			bool modified = (this.FileSize != oldFileInfo.FileSize) || (this.MD5Checksum != oldFileInfo.MD5Checksum);

			if (modified && !renamed)
			{
				return new ChangeInfo(
					changeType: ChangeType.Modified,
					filePath: this.FilePath);
			}
			else if (renamed && !modified)
			{
				int lastSeparator = this.FilePath.LastIndexOf('/');

				string containerPath = this.FilePath.Substring(0, lastSeparator + 1);

				return new ChangeInfo(
					changeType:
						oldFileInfo.FilePath.StartsWith(containerPath)
						? ChangeType.Renamed
						: ChangeType.Moved,
					filePath: this.FilePath,
					oldFilePath: oldFileInfo.FilePath);
			}
			else
			{
				return new ChangeInfo(
					changeType: ChangeType.MovedAndModified,
					filePath: this.FilePath,
					oldFilePath: oldFileInfo.FilePath);
			}
		}

		public ChangeInfo GenerateCreationChangeInfo()
		{
			return new ChangeInfo(
				changeType: ChangeType.Created,
				filePath: this.FilePath);
		}
	}
}
