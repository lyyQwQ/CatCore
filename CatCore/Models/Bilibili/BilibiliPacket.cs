using System;
using System.Globalization;
using System.Text;

namespace CatCore.Models.Bilibili
{
	internal sealed class BilibiliPacket
	{
		public const int HeaderLength = 16;
		public const int PacketOffset = 0;
		public const int HeaderOffset = 4;
		public const int VersionOffset = 6;
		public const int OperationOffset = 8;
		public const int SequenceOffset = 12;

		public byte[] PacketBuffer { get; }

		private BilibiliPacket(DanmakuOperation operation, string body)
		{
			var bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
			var packetLength = HeaderLength + bodyBytes.Length;
			var headerBytes = new byte[HeaderLength];

			BilibiliDataView.SetInt32(headerBytes, PacketOffset, packetLength);
			BilibiliDataView.SetInt16(headerBytes, HeaderOffset, HeaderLength);
			BilibiliDataView.SetInt16(headerBytes, VersionOffset, 1);
			BilibiliDataView.SetInt32(headerBytes, OperationOffset, (int)operation);
			BilibiliDataView.SetInt32(headerBytes, SequenceOffset, 1);

			PacketBuffer = BilibiliDataView.MergeBytes(new[] { headerBytes, bodyBytes });
		}

		public static BilibiliPacket CreateGreetingPacket(long uid, long roomId)
		{
			var uidText = uid.ToString(CultureInfo.InvariantCulture);
			var roomIdText = roomId.ToString(CultureInfo.InvariantCulture);
			var jsonBody = "{" +
				$"\"uid\":{uidText}," +
				$"\"roomid\":{roomIdText}," +
				"\"protover\":1," +
				"\"platform\":\"web\"," +
				"\"type\":2" +
				"}";

			return new BilibiliPacket(DanmakuOperation.GreetingReq, jsonBody);
		}

		public static BilibiliPacket CreateGreetingPacket(long uid, long roomId, string token, string buvid)
		{
			var uidText = uid.ToString(CultureInfo.InvariantCulture);
			var roomIdText = roomId.ToString(CultureInfo.InvariantCulture);
			var tokenText = EscapeJsonString(token ?? string.Empty);
			var buvidText = EscapeJsonString(buvid ?? string.Empty);
			var jsonBody = "{" +
				$"\"uid\":{uidText}," +
				$"\"roomid\":{roomIdText}," +
				"\"protover\":1," +
				$"\"buvid\":\"{buvidText}\"," +
				"\"platform\":\"web\"," +
				"\"type\":2," +
				$"\"key\":\"{tokenText}\"" +
				"}";

			return new BilibiliPacket(DanmakuOperation.GreetingReq, jsonBody);
		}

		public static BilibiliPacket CreateAuthPacket(string authBody)
		{
			return new BilibiliPacket(DanmakuOperation.GreetingReq, authBody ?? string.Empty);
		}

		public static BilibiliPacket CreateHeartBeatPacket()
		{
			return new BilibiliPacket(DanmakuOperation.HeartBeatReq, "[object Object]");
		}

		private static string EscapeJsonString(string input)
		{
			if (string.IsNullOrEmpty(input))
			{
				return string.Empty;
			}

			return input
				.Replace("\\", "\\\\")
				.Replace("\"", "\\\"");
		}

		public enum DanmakuOperation
		{
			HeartBeatReq = 2,
			HeartBeatAck = 3,
			ChatMessage = 5,
			GreetingReq = 7,
			GreetingAck = 8,
			StopRoom = 1398034256,
			StopLiveRoomList = 0
		}
	}
}
