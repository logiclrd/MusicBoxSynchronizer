using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;

using File = Google.Apis.Drive.v3.Data.File;

namespace MusicBoxSynchronizer
{
	public class Manifest
	{
		string _pageToken = "";
		Dictionary<string, string> _folders = new Dictionary<string, string>();
		Dictionary<string, ManifestFileInfo> _files = new Dictionary<string, ManifestFileInfo>();
		Dictionary<string, string> _idByPath = new Dictionary<string, string>();
		bool _hasChanges;

		public static Manifest Build(string localPath)
		{
			Manifest ret = new Manifest();

			var enumeration = new FileSystemEnumerable<(FileSystemInfo Entry, string SpecifiedFullPath)>(
				localPath,
				(ref FileSystemEntry entry) => (entry.ToFileSystemInfo(), entry.ToSpecifiedFullPath()),
				new EnumerationOptions() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None });

			foreach (var (entry, specifiedFullPath) in enumeration)
			{
				string? relativePath = PathUtility.GetRelativePath(localPath, specifiedFullPath);

				if (relativePath == null)
					continue;

				if (entry is DirectoryInfo)
				{
					ret._folders[relativePath] = relativePath;
					ret._idByPath[relativePath] = relativePath;
				}
				else if (entry is FileInfo fileEntry)
				{
					var fileInfo = new ManifestFileInfo(relativePath, entry.FullName);

					fileInfo.FileSize = fileEntry.Length;
					fileInfo.ModifiedTimeUTC = fileEntry.LastWriteTimeUtc;

					ret._files[relativePath] = fileInfo;
					ret._idByPath[relativePath] = relativePath;
				}
			}

			ret.HasChanges = false;

			return ret;
		}

		public static Manifest Build(DriveService service)
		{
			Manifest ret = new Manifest();

			var getStartPageTokenRequest = service.Changes.GetStartPageToken();

			ret.PageToken = getStartPageTokenRequest.Execute().StartPageTokenValue;

			var getRootRequest = service.Files.Get("root");

			var rootFolderFile = getRootRequest.Execute();

			var listRequest = service.Files.List();

			listRequest.Q =
				$"("
				+ $"mimeType = '{Constants.GoogleDriveFolderMIMEType}'" +
				" or "
				+ $"mimeType = '{Constants.GoogleDriveShortcutMIMEType}'" +
				$") and trashed = false and 'me' in owners";
			listRequest.Fields = "nextPageToken, files(id, name, parents, mimeType, shortcutDetails(targetId, targetMimeType))";

			var folderMap = new Dictionary<string, File>();

			folderMap[rootFolderFile.Id] = rootFolderFile;

			var shortcutTargets = new List<(string ApparentPath, string ShortcutFileID, string ActualFileID)>();

			while (true)
			{
				var list = listRequest.Execute();

				foreach (var folderFile in list.Files)
				{
					// We can't filter shortcuts precisely with Q (apparently Q supports shortcutDetails.targetId but not
					// shortcutDetails.targetMimeType), so we have to do it manually here.
					if (folderFile.MimeType == Constants.GoogleDriveShortcutMIMEType)
					{
						if (folderFile.ShortcutDetails.TargetMimeType != Constants.GoogleDriveFolderMIMEType)
							continue;

						shortcutTargets.Add(("unknown", folderFile.Id, folderFile.ShortcutDetails.TargetId));
					}

					folderMap[folderFile.Id] = folderFile;
				}

				if (!string.IsNullOrEmpty(list.NextPageToken))
					listRequest.PageToken = list.NextPageToken;
				else
					break;
			}

			string BuildPath(string folderFileId)
			{
				if (folderMap.TryGetValue(folderFileId, out var folderFile))
				{
					string container = "";

					if (folderFile.Parents?.Any() ?? false)
						container = BuildPath(folderFile.Parents.Single()) + "/";

					return container + folderFile.Name;
				}

				return "<unknown>";
			}

			foreach (var folderFile in folderMap.Values)
			{
				string path = BuildPath(folderFile.Id);

				ret._folders[folderFile.Id] = path;
				ret._idByPath[path] = folderFile.Id;
			}

			listRequest.Q = $"mimeType != '{Constants.GoogleDriveFolderMIMEType}' and trashed = false and 'me' in owners";
			listRequest.Fields = "nextPageToken, files(id, name, parents, size, md5Checksum, modifiedTime, mimeType, shortcutDetails(targetId, targetMimeType))";
			listRequest.PageToken = null;

			while (true)
			{
				var list = listRequest.Execute();

				foreach (var file in list.Files)
				{
					var actualFile = file;

					// We can't filter shortcuts precisely with Q (apparently Q supports shortcutDetails.targetId but not
					// shortcutDetails.targetMimeType), so we have to do it manually here.
					if (file.MimeType == Constants.GoogleDriveShortcutMIMEType)
					{
						if (file.ShortcutDetails.TargetMimeType == Constants.GoogleDriveFolderMIMEType)
							continue;

						var getRequest = service.Files.Get(file.ShortcutDetails.TargetId);

						actualFile = getRequest.Execute();
					}

					ret.PopulateFile(file, actualFile);
				}

				if (!string.IsNullOrEmpty(list.NextPageToken))
					listRequest.PageToken = list.NextPageToken;
				else
					break;
			}

			for (int i = 0; i < shortcutTargets.Count; i++)
			{
				var shortcutTarget = shortcutTargets[i];

				shortcutTarget.ApparentPath = ret._folders[shortcutTarget.ShortcutFileID];

				listRequest.Q = $"'{shortcutTarget.ActualFileID}' in parents";
				listRequest.PageToken = null;

				while (true)
				{
					var list = listRequest.Execute();

					foreach (var file in list.Files)
					{
						var actualFile = file;

						// We must recurse manually into subfolders.
						if (file.MimeType == Constants.GoogleDriveFolderMIMEType)
						{
							string subPath = PathUtility.Join(shortcutTarget.ApparentPath, file.Name);

							ret._folders[file.Id] = subPath;
							ret._idByPath[subPath] = file.Id;

							shortcutTargets.Add((subPath, file.Id, file.Id));

							continue;
						}

						// We can't filter shortcuts precisely with Q (apparently Q supports shortcutDetails.targetId but not
						// shortcutDetails.targetMimeType), so we have to do it manually here.
						if (file.MimeType == Constants.GoogleDriveShortcutMIMEType)
						{
							if (file.ShortcutDetails.TargetMimeType == Constants.GoogleDriveFolderMIMEType)
							{
								string subPath = PathUtility.Join(shortcutTarget.ApparentPath, file.Name);

								ret._folders[file.Id] = subPath;
								ret._idByPath[subPath] = file.Id;

								shortcutTargets.Add((subPath, file.Id, file.ShortcutDetails.TargetId));

								continue;
							}

							var getRequest = service.Files.Get(file.ShortcutDetails.TargetId);

							actualFile = getRequest.Execute();
						}

						ret.PopulateFile(file, shortcutTarget.ShortcutFileID, actualFile);
					}

					if (!string.IsNullOrEmpty(list.NextPageToken))
						listRequest.PageToken = list.NextPageToken;
					else
						break;
				}
			}

			ret.HasChanges = false;

			return ret;
		}

		public static Manifest LoadFrom(string filePath)
		{
			using (var reader = new StreamReader(filePath))
				return LoadFrom(reader);
		}

		public static Manifest LoadFrom(TextReader reader)
		{
			var ret = new Manifest();

			ret.PageToken = reader.ReadLine() ?? throw new Exception("Unexpected EOF");

			string folderCountString = reader.ReadLine() ?? throw new Exception("Unexpected EOF");

			if (int.TryParse(folderCountString, out var folderCount))
			{
				for (int i = 0; i < folderCount; i++)
				{
					string id = reader.ReadLine() ?? throw new Exception("Unexpected EOF");
					string path = reader.ReadLine() ?? throw new Exception("Unexpected EOF");

					ret._folders[id] = path;
					ret._idByPath[path] = id;
				}
			}

			string fileCountString = reader.ReadLine() ?? throw new Exception("Unexpected EOF");

			if (int.TryParse(fileCountString, out var fileCount))
			{
				for (int i = 0; i < fileCount; i++)
				{
					string id = reader.ReadLine() ?? throw new Exception("Unexpected EOF");
					var file = ManifestFileInfo.LoadFrom(reader);

					ret._files[id] = file;
					ret._idByPath[file.FilePath] = id;
				}
			}

			ret._hasChanges = false;

			return ret;
		}

		public void SaveTo(string filePath)
		{
			using (var writer = new StreamWriter(filePath))
				SaveTo(writer);
		}

		public void SaveTo(TextWriter writer)
		{
			writer.WriteLine(_pageToken);

			writer.WriteLine(_folders.Count);

			foreach (var folder in _folders)
			{
				writer.WriteLine(folder.Key);
				writer.WriteLine(folder.Value);
			}

			writer.WriteLine(_files.Count);

			foreach (var file in _files)
			{
				writer.WriteLine(file.Key);

				file.Value.SaveTo(writer);
			}

			_hasChanges = false;
		}

		public IEnumerable<string> EnumerateFolders() => _folders.Values;
		public IEnumerable<ManifestFileInfo> EnumerateFiles() => _files.Values;

		public IEnumerable<ManifestFileInfo> EnumerateContents(string folderPath, bool recursive = true)
		{
			if (!folderPath.EndsWith("/"))
				folderPath += "/";

			foreach (var fileInfo in _files.Values)
			{
				if (fileInfo.FilePath.StartsWith(folderPath))
				{
					if (recursive || (fileInfo.FilePath.IndexOf('/', folderPath.Length) < 0))
						yield return fileInfo;
				}
			}
		}

		public string PageToken
		{
			get => _pageToken;
			set
			{
				if (_pageToken != value)
				{
					_pageToken = value;
					_hasChanges = true;
				}
			}
		}

		public bool HasChanges
		{
			get => _hasChanges;
			set => _hasChanges = value;
		}

		public void PopulateFolder(string path, string fileID)
		{
			_folders[fileID] = path;
			_idByPath[path] = fileID;

			_hasChanges = true;
		}

		public void PopulateFile(File file, File actualFile)
			=> PopulateFile(file, file.Parents?.SingleOrDefault(), actualFile);

		public void PopulateFile(File file, string? parentFileID, File actualFile)
		{
			var fileInfo = ManifestFileInfo.Build(file, parentFileID, actualFile, this);

			PopulateFile(fileInfo, file.Id);
		}

		public void PopulateFile(ManifestFileInfo fileInfo, string fileID)
		{
			_files[fileID] = fileInfo;
			_idByPath[fileInfo.FilePath] = fileID;

			_hasChanges = true;
		}

		public string? GetFolderPath(string id)
		{
			if (_folders.TryGetValue(id, out var path))
				return path;
			else
				return null;
		}

		public ManifestFileInfo? GetFileInfo(string id)
		{
			if (_files.TryGetValue(id, out var file))
				return file;
			else
				return null;
		}

		public string? GetFileID(string filePath)
		{
			if (_idByPath.TryGetValue(filePath, out var id))
				return id;
			else
				return null;
		}

		public ChangeInfo? RegisterChange(Change change, MonitorableRepository sourceRepository)
			=> ((change.Removed ?? false) || (change.File?.Trashed ?? false))
				? RegisterRemoval(change.FileId, sourceRepository)
				: RegisterChange(change.File!, sourceRepository);

		public ChangeInfo? RegisterChange(File file, MonitorableRepository sourceRepository)
			=> RegisterChange(ManifestFileInfo.Build(file, this), file.Id, file.Name, file.MimeType, file.Parents?.SingleOrDefault(), sourceRepository);

		ChangeInfo? RegisterRemoval(string fileID, MonitorableRepository sourceRepository)
		{
			if (_files.TryGetValue(fileID, out var removedFile))
			{
				_files.Remove(fileID);
				_idByPath.Remove(removedFile.FilePath);
				_hasChanges = true;

				return new ChangeInfo(
					sourceRepository: sourceRepository,
					changeType: ChangeType.Removed,
					filePath: removedFile?.FilePath ?? "<unknown>",
					md5Checksum: removedFile?.MD5Checksum ?? "<unknown>");
			}

			if (_folders.TryGetValue(fileID, out var removedFolderPath))
			{
				_folders.Remove(fileID);
				_idByPath.Remove(removedFolderPath);
				_hasChanges = true;

				return new ChangeInfo(
					sourceRepository: sourceRepository,
					changeType: ChangeType.Removed,
					filePath: removedFolderPath,
					isFolder: true);
			}

			return null;
		}

		public ChangeInfo? RegisterChange(ManifestFileInfo newFileInfo, string fileID, string fileName, string fileMIMEType, string? fileParentFileID, MonitorableRepository sourceRepository)
		{
			Console.WriteLine("RegisterChange:");
			Console.WriteLine("- newFileInfo: ManifestFileInfo {{ {0} }}", newFileInfo.FilePath);
			Console.WriteLine("- fileID: {0}", fileID);
			Console.WriteLine("- fileName: {0}", fileName);
			Console.WriteLine("- fileParentID: {0}", fileParentFileID ?? "<null>");

			string container = "";

			if (fileParentFileID != null)
			{
				_folders.TryGetValue(fileParentFileID, out var containerPath);

				Console.WriteLine("=> container path: {0}", containerPath ?? "<null>");

				if (containerPath != null)
					container = containerPath + "/";

				Console.WriteLine("=> container: {0}", container);
			}

			string? oldItemPath = GetFileInfo(fileID)?.FilePath;

			string newItemPath = container + fileName;

			Console.WriteLine("=> old item path: {0}", oldItemPath ?? "<null>");
			Console.WriteLine("=> new item path: {0}", newItemPath);

			if (fileMIMEType == Constants.GoogleDriveFolderMIMEType)
			{
				bool fileIDExists = _folders.TryGetValue(fileID, out var oldFolderPath);

				if (oldFolderPath == newItemPath)
					return null;

				try
				{
					if (fileIDExists)
					{
						return new ChangeInfo(
							sourceRepository: sourceRepository,
							changeType: ChangeType.Moved,
							filePath: newItemPath,
							oldFilePath: oldFolderPath!,
							isFolder: true);
					}
					else
					{
						return new ChangeInfo(
							sourceRepository: sourceRepository,
							changeType: fileIDExists ? ChangeType.Modified : ChangeType.Created,
							filePath: newItemPath,
							isFolder: true);
					}
				}
				finally
				{
					_folders[fileID] = newItemPath;
					if (oldItemPath != null)
						_idByPath.Remove(oldItemPath);
					_idByPath[newItemPath] = fileID;
					_hasChanges = true;
				}
			}
			else
			{
				string? oldFilePath = null;

				try
				{
					if (_files.TryGetValue(fileID, out var oldFileInfo))
					{
						oldFilePath = oldFileInfo.FilePath;
						Console.WriteLine("=> got old file info, found path: {0}", oldFilePath);
						Console.WriteLine("=> comparing FileInfo objects");
						return newFileInfo.CompareTo(oldFileInfo, sourceRepository);
					}
					else
						return newFileInfo.GenerateCreationChangeInfo(sourceRepository);
				}
				finally
				{
					Console.WriteLine("=> stashing new file info");
					_files[fileID] = newFileInfo;
					Console.WriteLine("=> unlinking old path");
					if (oldFilePath != null)
						_idByPath.Remove(oldFilePath);
					Console.WriteLine("=> linking new path {0} to file ID {1}", newFileInfo.FilePath, fileID);
					_idByPath[newFileInfo.FilePath] = fileID;
					Console.WriteLine("=> setting dirty flag");
					_hasChanges = true;
				}
			}
		}

		public ChangeInfo RegisterChange(ChangeInfo changeInfo, long fileSize, DateTime modifiedTimeUTC)
		{
			Console.WriteLine("RegisterChange: " + changeInfo);

			if (_idByPath.TryGetValue(changeInfo.FilePath, out var fileID))
			{
				Console.WriteLine("=> got file ID: " + fileID);

				if (changeInfo.ChangeType == ChangeType.Removed)
					RegisterRemoval(fileID, changeInfo.SourceRepository);
				else
				{
					Console.WriteLine("=> building newFileInfo");

					var newFileInfo = new ManifestFileInfo(changeInfo.FilePath, changeInfo.MD5Checksum);

					newFileInfo.FileSize = fileSize;
					newFileInfo.ModifiedTimeUTC = modifiedTimeUTC;

					string fileName = PathUtility.GetFileName(changeInfo.FilePath);
					string containerPath = PathUtility.GetParentPath(changeInfo.FilePath) ?? "";

					RegisterChange(newFileInfo, fileID, fileName, "application/octet-stream", GetFileID(containerPath), changeInfo.SourceRepository);
				}
			}
			else if (((changeInfo.ChangeType == ChangeType.Moved) || (changeInfo.ChangeType == ChangeType.MovedAndModified))
			      && (changeInfo.OldFilePath != null)
			      && _idByPath.TryGetValue(changeInfo.OldFilePath, out fileID))
			{
				var fileInfo = GetFileInfo(fileID);

				if (fileInfo == null)
					throw new Exception("Internal error: Couldn't find FileInfo for " + fileID + " (from path " + changeInfo.OldFilePath + ")");

				fileInfo.FilePath = changeInfo.FilePath;

				_idByPath.Remove(changeInfo.OldFilePath);
				_idByPath[changeInfo.FilePath] = fileID;
			}

			return changeInfo;
		}

		public void RegisterMove(string fromPath, string toPath, MonitorableRepository sourceRepository)
		{
			Console.WriteLine("REGISTERING THAT {0} SHOULD BE AT {1}", fromPath, toPath);

			if (_idByPath.TryGetValue(toPath, out var existingToPathFileID))
				throw new Exception("Can't register a move from " + fromPath + " to " + toPath + " because the 'to' path is already mapped to file ID " + existingToPathFileID);

			string? fileID = GetFileID(fromPath);

			if (fileID != null)
			{
				Console.WriteLine("=> have file ID");

				if (GetFileInfo(fileID) is ManifestFileInfo fileInfo)
				{
					Console.WriteLine("=> have file info");

					RegisterChange(
						new ChangeInfo(sourceRepository, ChangeType.Moved, toPath, fromPath, isFolder: false, fileInfo.MD5Checksum),
						fileSize: fileInfo.FileSize,
						modifiedTimeUTC: fileInfo.ModifiedTimeUTC);
				}
			}
		}
	}
}