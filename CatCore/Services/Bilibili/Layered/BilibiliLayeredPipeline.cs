using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CatCore.Models.Bilibili;

namespace CatCore.Services.Bilibili.Layered
{
	internal sealed class BilibiliAuthRequest
	{
		private BilibiliAuthRequest(long roomId, long userId, string token, string buvid3)
		{
			RoomId = roomId;
			UserId = userId;
			Token = token ?? string.Empty;
			Buvid3 = buvid3 ?? string.Empty;
		}

		public long RoomId { get; }
		public long UserId { get; }
		public string Token { get; }
		public string Buvid3 { get; }

		public static BilibiliAuthRequest ForDefault(long roomId, long userId, string token, string buvid3)
		{
			return new BilibiliAuthRequest(roomId, userId, token, buvid3);
		}
	}

	internal interface IBilibiliAuthFacade
	{
		bool TryBuildGreetingPacket(BilibiliAuthRequest request, out byte[] packet, out string failureReason);
		byte[] CreateHeartbeatPacket();
	}

	internal sealed class BilibiliAuthFacade : IBilibiliAuthFacade
	{
		private static readonly byte[] HEARTBEAT_PACKET = BilibiliPacket.CreateHeartBeatPacket().PacketBuffer;

		public bool TryBuildGreetingPacket(BilibiliAuthRequest request, out byte[] packet, out string failureReason)
		{
			packet = Array.Empty<byte>();
			failureReason = string.Empty;

			if (request == null)
			{
				failureReason = "AUTH_PRECHECK_INVALID_REQUEST: request is null";
				return false;
			}

			if (request.RoomId <= 0)
			{
				failureReason = "AUTH_PRECHECK_ROOM_INVALID: room_id<=0";
				return false;
			}

			if (string.IsNullOrWhiteSpace(request.Token))
			{
				failureReason = "AUTH_PRECHECK_TOKEN_MISSING: token is empty";
				return false;
			}

			if (string.IsNullOrWhiteSpace(request.Buvid3))
			{
				failureReason = "AUTH_PRECHECK_BUVID_MISSING: buvid3 is empty";
				return false;
			}

			packet = BilibiliPacket.CreateGreetingPacket(request.UserId, request.RoomId, request.Token, request.Buvid3).PacketBuffer;
			return true;
		}

		public byte[] CreateHeartbeatPacket()
		{
			return HEARTBEAT_PACKET;
		}
	}

	internal sealed class BilibiliDecodedPacket
	{
		private string? _body;

		public BilibiliDecodedPacket(BilibiliPacket.DanmakuOperation operation, ReadOnlyMemory<byte> bodyBytes, int version, int sequence)
		{
			Operation = operation;
			BodyBytes = bodyBytes;
			Version = version;
			Sequence = sequence;
		}

		public BilibiliPacket.DanmakuOperation Operation { get; }
		public ReadOnlyMemory<byte> BodyBytes { get; }

		public string Body
		{
			get
			{
				if (_body != null)
				{
					return _body;
				}

				if (BodyBytes.IsEmpty)
				{
					_body = string.Empty;
					return _body;
				}

				if (MemoryMarshal.TryGetArray(BodyBytes, out ArraySegment<byte> segment) && segment.Array != null)
				{
					_body = Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count);
					return _body;
				}

				_body = Encoding.UTF8.GetString(BodyBytes.ToArray());
				return _body;
			}
		}
		public int Version { get; }
		public int Sequence { get; }
	}

	internal sealed class BilibiliCodecDecodeResult
	{
		public BilibiliCodecDecodeResult(List<BilibiliDecodedPacket> messages, string diagnostic)
		{
			Messages = messages ?? new List<BilibiliDecodedPacket>();
			Diagnostic = diagnostic ?? string.Empty;
		}

		public List<BilibiliDecodedPacket> Messages { get; }
		public string Diagnostic { get; }
	}

	internal interface IBilibiliProtocolCodec
	{
		bool TryReadHeader(byte[] frame, out BilibiliPacketHeader header);
		BilibiliCodecDecodeResult Decode(byte[] frame);
	}

	internal sealed class BilibiliProtocolCodecAdapter : IBilibiliProtocolCodec
	{
		public bool TryReadHeader(byte[] frame, out BilibiliPacketHeader header)
		{
			return BilibiliProtocolCodec.TryReadHeader(frame, out header);
		}

		public BilibiliCodecDecodeResult Decode(byte[] frame)
		{
			if (frame == null || frame.Length < BilibiliProtocolCodec.HeaderLength)
			{
				return new BilibiliCodecDecodeResult(new List<BilibiliDecodedPacket>(), "CODEC_FRAME_TOO_SHORT");
			}

			if (!TryReadHeader(frame, out var header))
			{
				return new BilibiliCodecDecodeResult(new List<BilibiliDecodedPacket>(), "CODEC_HEADER_INVALID");
			}

			var messages = new List<BilibiliDecodedPacket>();
			try
			{
				foreach (var message in DanmakuMessage.ParsePackets(frame))
				{
					messages.Add(new BilibiliDecodedPacket(message.Operation, message.BodyBytes, message.Version, message.Sequence));
				}
			}
			catch (Exception ex)
			{
				return new BilibiliCodecDecodeResult(messages, $"CODEC_EXCEPTION:{ex.GetType().Name}:{ex.Message}");
			}

			if (messages.Count == 0)
			{
				return new BilibiliCodecDecodeResult(messages, $"CODEC_DECODE_EMPTY:op={(int)header.Operation},ver={header.Version},len={frame.Length}");
			}

			return new BilibiliCodecDecodeResult(messages, string.Empty);
		}
	}

	internal enum BilibiliNormalizedEventKind
	{
		AuthAck,
		HeartbeatAck,
		Chat,
		DeleteMessage,
		ClearChat,
		StopRoom,
		Unknown
	}

	internal sealed class BilibiliNormalizedEvent
	{
		public BilibiliNormalizedEvent(BilibiliNormalizedEventKind kind, BilibiliDecodedPacket packet, string command, IReadOnlyList<string> messageIds)
		{
			Kind = kind;
			Packet = packet;
			Command = command ?? string.Empty;
			MessageIds = messageIds ?? Array.Empty<string>();
		}

		public BilibiliNormalizedEventKind Kind { get; }
		public BilibiliDecodedPacket Packet { get; }
		public string Command { get; }
		public IReadOnlyList<string> MessageIds { get; }
	}

	internal interface IBilibiliMessageNormalizer
	{
		BilibiliNormalizedEvent Normalize(BilibiliDecodedPacket packet);
	}

	internal sealed class BilibiliMessageNormalizer : IBilibiliMessageNormalizer
	{
		public BilibiliNormalizedEvent Normalize(BilibiliDecodedPacket packet)
		{
			if (packet == null)
			{
				return new BilibiliNormalizedEvent(BilibiliNormalizedEventKind.Unknown, new BilibiliDecodedPacket(0, ReadOnlyMemory<byte>.Empty, 0, 0), string.Empty, Array.Empty<string>());
			}

			switch (packet.Operation)
			{
				case BilibiliPacket.DanmakuOperation.GreetingAck:
					return new BilibiliNormalizedEvent(BilibiliNormalizedEventKind.AuthAck, packet, string.Empty, Array.Empty<string>());
				case BilibiliPacket.DanmakuOperation.HeartBeatAck:
					return new BilibiliNormalizedEvent(BilibiliNormalizedEventKind.HeartbeatAck, packet, string.Empty, Array.Empty<string>());
				case BilibiliPacket.DanmakuOperation.StopRoom:
				case BilibiliPacket.DanmakuOperation.StopLiveRoomList:
					return new BilibiliNormalizedEvent(BilibiliNormalizedEventKind.StopRoom, packet, string.Empty, Array.Empty<string>());
				case BilibiliPacket.DanmakuOperation.ChatMessage:
					return NormalizeChatPacket(packet);
				default:
					return new BilibiliNormalizedEvent(BilibiliNormalizedEventKind.Unknown, packet, string.Empty, Array.Empty<string>());
			}
		}

		private static BilibiliNormalizedEvent NormalizeChatPacket(BilibiliDecodedPacket packet)
		{
			if (packet.BodyBytes.IsEmpty)
			{
				return new BilibiliNormalizedEvent(BilibiliNormalizedEventKind.Unknown, packet, string.Empty, Array.Empty<string>());
			}

			return new BilibiliNormalizedEvent(BilibiliNormalizedEventKind.Chat, packet, string.Empty, Array.Empty<string>());
		}
	}
}
