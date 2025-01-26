using System;
using System.IO;

namespace MusicBoxSynchronizer
{
	public class TemporaryFileStream : FileStream
	{
		string _path;
		bool _disposed;

		public TemporaryFileStream(string path, FileMode mode)
			: base(path, mode)
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
