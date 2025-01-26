using System;
using System.IO;

namespace MusicBoxSynchronizer
{
	public class TemporaryFileStream : FileStream
	{
		string _path;
		bool _disposed;

		public TemporaryFileStream(string path, FileMode mode, FileAccess access)
			: base(path, mode, access)
		{
			_path = path;
		}

		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);

			if (!_disposed)
			{
				File.Delete(_path);
				_disposed = true;
			}
		}
	}
}
