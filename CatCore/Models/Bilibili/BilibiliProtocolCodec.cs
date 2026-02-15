using System;

namespace CatCore.Models.Bilibili
{
	internal readonly struct BilibiliPacketHeader
	{
		public BilibiliPacketHeader(int packetLength, int headerLength, int version, BilibiliPacket.DanmakuOperation operation, int sequence)
		{
			PacketLength = packetLength;
			HeaderLength = headerLength;
			Version = version;
			Operation = operation;
			Sequence = sequence;
		}

		public int PacketLength { get; }
		public int HeaderLength { get; }
		public int Version { get; }
		public BilibiliPacket.DanmakuOperation Operation { get; }
		public int Sequence { get; }
	}

	internal static class BilibiliProtocolCodec
	{
		public const int HeaderLength = BilibiliPacket.HeaderLength;

		public static byte[] BuildPacket(int version, BilibiliPacket.DanmakuOperation operation, int sequence, byte[] body)
		{
			if (body == null)
			{
				throw new ArgumentNullException(nameof(body));
			}

			var packetLength = HeaderLength + body.Length;
			var buffer = new byte[packetLength];

			BilibiliDataView.SetInt32(buffer, BilibiliPacket.PacketOffset, packetLength);
			BilibiliDataView.SetInt16(buffer, BilibiliPacket.HeaderOffset, HeaderLength);
			BilibiliDataView.SetInt16(buffer, BilibiliPacket.VersionOffset, version);
			BilibiliDataView.SetInt32(buffer, BilibiliPacket.OperationOffset, (int)operation);
			BilibiliDataView.SetInt32(buffer, BilibiliPacket.SequenceOffset, sequence);

			Array.Copy(body, 0, buffer, HeaderLength, body.Length);
			return buffer;
		}

		public static bool TryReadHeader(byte[] buffer, out BilibiliPacketHeader header)
		{
			return TryReadHeader(buffer, 0, out header);
		}

		public static bool TryReadHeader(byte[] buffer, int offset, out BilibiliPacketHeader header)
		{
			header = default;
			if (buffer == null || offset < 0 || buffer.Length < offset + HeaderLength)
			{
				return false;
			}

			var packetLength = BilibiliDataView.GetInt32(buffer, offset + BilibiliPacket.PacketOffset);
			var headerLength = BilibiliDataView.GetInt16(buffer, offset + BilibiliPacket.HeaderOffset);
			var version = BilibiliDataView.GetInt16(buffer, offset + BilibiliPacket.VersionOffset);
			var operation = BilibiliDataView.GetInt32(buffer, offset + BilibiliPacket.OperationOffset);
			var sequence = BilibiliDataView.GetInt32(buffer, offset + BilibiliPacket.SequenceOffset);

			if (packetLength <= 0 || headerLength <= 0 || headerLength > packetLength)
			{
				return false;
			}

			if (offset + packetLength > buffer.Length)
			{
				return false;
			}

			header = new BilibiliPacketHeader(packetLength, headerLength, version, (BilibiliPacket.DanmakuOperation)operation, sequence);
			return true;
		}
	}
}
