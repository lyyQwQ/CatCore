using System.Collections.Generic;
using System.Linq;

namespace CatCore.Models.Bilibili
{
	internal static class BilibiliDataView
	{
		public static int GetInt16(byte[] bytes, int offset = 0)
		{
			return ((bytes[offset] & 0xff) << 8) | (bytes[offset + 1] & 0xff);
		}

		public static int GetInt32(byte[] bytes, int offset = 0)
		{
			return ((bytes[offset] & 0xff) << 24) | ((bytes[offset + 1] & 0xff) << 16) | ((bytes[offset + 2] & 0xff) << 8) | (bytes[offset + 3] & 0xff);
		}

		public static void SetInt16(byte[] bytes, int offset, int value)
		{
			bytes[offset] = (byte)((value & 0xff00) >> 8);
			bytes[offset + 1] = (byte)(value & 0x00ff);
		}

		public static void SetInt32(byte[] bytes, int offset, int value)
		{
			bytes[offset] = (byte)((value & 0xff000000) >> 24);
			bytes[offset + 1] = (byte)((value & 0x00ff0000) >> 16);
			bytes[offset + 2] = (byte)((value & 0x0000ff00) >> 8);
			bytes[offset + 3] = (byte)(value & 0x000000ff);
		}

		public static byte[] MergeBytes(IEnumerable<byte[]> bytes)
		{
			var totalLength = bytes.Sum(buffer => buffer.Length);
			var result = new byte[totalLength];
			var offset = 0;

			foreach (var buffer in bytes)
			{
				for (var i = 0; i < buffer.Length; i++)
				{
					result[offset + i] = buffer[i];
				}
				offset += buffer.Length;
			}

			return result;
		}
	}
}
