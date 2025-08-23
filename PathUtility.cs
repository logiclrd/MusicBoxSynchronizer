using System.IO;

namespace MusicBoxSynchronizer
{
	public class PathUtility
	{
		public static string? GetParentPath(string filePath)
		{
			int separator = filePath.LastIndexOf('/');

			if (separator > 0)
				return filePath.Substring(0, separator);
			else
				return null;
		}

		public static string? GetRelativePath(string rootPath, string? fullPath)
		{
			if (fullPath == null)
				return null;

			rootPath = rootPath.Replace('\\', '/');
			fullPath = fullPath.Replace('\\', '/');

			if (fullPath.StartsWith(rootPath) && (fullPath[rootPath.Length] == '/'))
				return fullPath.Substring(rootPath.Length + 1).TrimStart('/');
			else
				return null;
		}

		public static string GetFileName(string filePath)
			=> Path.GetFileName(filePath);

		public static string Join(string containerPath, string subPath)
			=> containerPath.TrimEnd('/') + '/' + subPath.TrimStart('/');
    }
}
