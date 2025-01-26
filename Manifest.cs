using System;
using System.Collections.Generic;
using System.IO;
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

		public static Manifest Build(DriveService service)
		{
			Manifest ret = new Manifest();

			var getStartPageTokenRequest = service.Changes.GetStartPageToken();

			ret.PageToken = getStartPageTokenRequest.Execute().StartPageTokenValue;

			var getRootRequest = service.Files.Get("root");

			var rootFolderFile = getRootRequest.Execute();

			var listRequest = service.Files.List();

			listRequest.Q = $"mimeType = '{Constants.GoogleDriveFolderMIMEType}' and trashed = false";
			listRequest.Fields = "files(id, name, parents)";

			var folderMap = new Dictionary<string, File>();

			folderMap[rootFolderFile.Id] = rootFolderFile;

			while (true)
			{
				var list = listRequest.Execute();

				foreach (var folderFile in list.Files)
					folderMap[folderFile.Id] = folderFile;

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

				ret._folders[folderFile.Id] = BuildPath(folderFile.Id);
				ret._idByPath[path] = folderFile.Id;
			}

			listRequest.Q = $"mimeType != '{Constants.GoogleDriveFolderMIMEType}' and trashed = false";
			listRequest.Fields = "files(id, name, parents, size, modifiedTime)";

			while (true)
			{
				var list = listRequest.Execute();

				foreach (var file in list.Files)
					ret.PopulateFile(file);

				if (!string.IsNullOrEmpty(list.NextPageToken))
					listRequest.PageToken = list.NextPageToken;
				else
					break;
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
				for (int i=0; i < folderCount; i++)
				{
					string id = reader.ReadLine() ?? throw new Exception("Unexpected EOF");
					string path = reader.ReadLine() ?? throw new Exception("Unexpected EOF");

					ret._folders[id] = path;
				}
			}

			string fileCountString = reader.ReadLine() ?? throw new Exception("Unexpected EOF");

			if (int.TryParse(fileCountString, out var fileCount))
			{
				for (int i=0; i < fileCount; i++)
				{
					string id = reader.ReadLine() ?? throw new Exception("Unexpected EOF");
					var file = ManifestFileInfo.LoadFrom(reader);

					ret._files[id] = file;
				}
			}

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

		public void PopulateFile(File file)
		{
			var fileInfo = ManifestFileInfo.Build(file, this);

			_files[file.Id] = fileInfo;
			_idByPath[fileInfo.FilePath] = file.Id;
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
		{
			if ((change.Removed ?? false) || (change.File.Trashed ?? false))
			{
				if (_files.TryGetValue(change.FileId, out var removedFile))
				{
					_files.Remove(change.FileId);
					_idByPath.Remove(removedFile.FilePath);
					_hasChanges = true;

					return new ChangeInfo(
						sourceRepository: sourceRepository,
						changeType: ChangeType.Removed,
						filePath: removedFile?.FilePath ?? "<unknown>");
				}

				if (_folders.TryGetValue(change.FileId, out var removedFolderPath))
				{
					_folders.Remove(change.FileId);
					_idByPath.Remove(removedFolderPath);
					_hasChanges = true;

					return new ChangeInfo(
						sourceRepository: sourceRepository,
						changeType: ChangeType.Removed,
						filePath: removedFolderPath,
						isFolder: true);
				}
			}
			else
				return RegisterChange(change.File, sourceRepository);

			return null;
		}

		public ChangeInfo RegisterChange(File file, MonitorableRepository sourceRepository)
		{
			var newFileInfo = ManifestFileInfo.Build(file, this);

			if (file.MimeType != Constants.GoogleDriveFolderMIMEType)
			{
				string container = "";

				if (file.Parents?.Any() ?? false)
				{
					_folders.TryGetValue(file.Parents.Single(), out var containerPath);

					if (containerPath != null)
						container = containerPath + "/";
				}

				string newFolderPath = container + file.Name;

				try
				{
					if (_folders.TryGetValue(file.Id, out var oldFolderPath)
					 && (oldFolderPath != newFolderPath))
					{
						return new ChangeInfo(
							sourceRepository: sourceRepository,
							changeType: ChangeType.Moved,
							filePath: newFolderPath,
							oldFilePath: oldFolderPath,
							isFolder: true);
					}
					else
					{
						return new ChangeInfo(
							sourceRepository: sourceRepository,
							changeType: ChangeType.Created,
							filePath: newFolderPath,
							isFolder: true);
					}
				}
				finally
				{
					_folders[file.Id] = newFolderPath;
					_hasChanges = true;
				}
			}
			else
			{
				try
				{
					if (_files.TryGetValue(file.Id, out var oldFileInfo))
						return newFileInfo.CompareTo(oldFileInfo, sourceRepository);
					else
						return newFileInfo.GenerateCreationChangeInfo(sourceRepository);
				}
				finally
				{
					_files[file.Id] = newFileInfo;
					_hasChanges = true;
				}
			}
		}
	}
}