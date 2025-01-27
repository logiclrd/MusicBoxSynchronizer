using System.CodeDom;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace MusicBoxSynchronizer
{
	public class MD5Utility
	{
		public static string ComputeChecksum(string filePath)
		{
			using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				return ComputeChecksum(stream);
		}

		public static string ComputeChecksum(Stream stream)
		{
			var md5 = MD5.Create();

			byte[] hash = md5.ComputeHash(stream);

			char[] hashBytes = new char[hash.Length * 2];

			char ToHex(int nybble) => "0123456789abcdef"[nybble];

			for (int i=0; i < hash.Length; i++)
			{
				int x = i + i;
				int y = x + 1;

				hashBytes[x] = ToHex(hash[i] >> 4);
				hashBytes[y] = ToHex(hash[i] & 0xF);
			}

			return new string(hashBytes);
		}
	}
}
