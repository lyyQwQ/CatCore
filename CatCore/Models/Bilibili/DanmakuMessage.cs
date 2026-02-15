using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CatCore.Models.Bilibili
{
	internal sealed class DanmakuMessage
	{
		public int PacketLength { get; private set; }
		public int HeaderLength { get; private set; }
		public int Version { get; private set; }
		public BilibiliPacket.DanmakuOperation Operation { get; private set; }
		public int Sequence { get; private set; }
		public ReadOnlyMemory<byte> BodyBytes { get; private set; } = ReadOnlyMemory<byte>.Empty;

		public static IEnumerable<DanmakuMessage> ParsePackets(byte[] buffer)
		{
			if (buffer == null || buffer.Length < BilibiliPacket.HeaderLength)
			{
				yield break;
			}

			if (!BilibiliProtocolCodec.TryReadHeader(buffer, out var header))
			{
				yield break;
			}

			if (header.Operation == BilibiliPacket.DanmakuOperation.ChatMessage && header.Version is 2 or 3)
			{
				byte[] decompressed;
				if (!TryDecompressChatBody(buffer, header, out decompressed))
				{
					yield break;
				}

				foreach (var nestedMessage in ParseNestedPackets(decompressed))
				{
					yield return nestedMessage;
				}

				yield break;
			}

			var bodyStart = header.HeaderLength;
			var bodyLength = header.PacketLength - header.HeaderLength;
			if (bodyLength < 0 || bodyStart + bodyLength > buffer.Length)
			{
				yield break;
			}

			var bodyBytes = bodyLength <= 0
				? ReadOnlyMemory<byte>.Empty
				: new ReadOnlyMemory<byte>(buffer, bodyStart, bodyLength);

			yield return new DanmakuMessage
			{
				PacketLength = header.PacketLength,
				HeaderLength = header.HeaderLength,
				Version = header.Version,
				Operation = header.Operation,
				Sequence = header.Sequence,
				BodyBytes = bodyBytes
			};
		}

		private static bool TryDecompressChatBody(byte[] buffer, BilibiliPacketHeader header, out byte[] decompressed)
		{
			decompressed = Array.Empty<byte>();
			var compressedLength = header.PacketLength - header.HeaderLength;
			if (compressedLength <= 0 || header.HeaderLength + compressedLength > buffer.Length)
			{
				return false;
			}

			try
			{
				Stream sourceStream;
				if (header.Version == 2)
				{
					if (compressedLength <= 2)
					{
						return false;
					}

					sourceStream = new MemoryStream(buffer, header.HeaderLength + 2, compressedLength - 2, false);
				}
				else
				{
					return false;
				}

				using (sourceStream)
				using (var decompressionStream = new DeflateStream(sourceStream, CompressionMode.Decompress, leaveOpen: false))
				using (var destination = new MemoryStream())
				{
					decompressionStream.CopyTo(destination);
					decompressed = destination.ToArray();
					return decompressed.Length > 0;
				}
			}
			catch
			{
				return false;
			}
		}

		private static IEnumerable<DanmakuMessage> ParseNestedPackets(byte[] buffer)
		{
			if (buffer.Length < BilibiliPacket.HeaderLength)
			{
				yield break;
			}

			var offset = 0;
			while (offset + BilibiliPacket.HeaderLength <= buffer.Length)
			{
				if (!BilibiliProtocolCodec.TryReadHeader(buffer, offset, out var header))
				{
					yield break;
				}

				var bodyLength = header.PacketLength - header.HeaderLength;
				var bodyStart = offset + header.HeaderLength;
				if (bodyLength < 0 || bodyStart + bodyLength > buffer.Length)
				{
					yield break;
				}

				yield return new DanmakuMessage
				{
					PacketLength = header.PacketLength,
					HeaderLength = header.HeaderLength,
					Version = header.Version,
					Operation = header.Operation,
					Sequence = header.Sequence,
					BodyBytes = bodyLength <= 0 ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(buffer, bodyStart, bodyLength)
				};

				offset += header.PacketLength;
				if (header.PacketLength <= 0)
				{
					yield break;
				}
			}
		}
	}
}
