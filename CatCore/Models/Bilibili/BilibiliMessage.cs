using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CatCore.Models.Shared;

namespace CatCore.Models.Bilibili
{
	public sealed class BilibiliMessage : IChatMessage<BilibiliMessage, BilibiliChannel>
	{
		public BilibiliMessage(string id, bool isSystemMessage, bool isActionMessage, bool isMentioned, string message, IChatUser sender, BilibiliChannel channel,
			ReadOnlyCollection<IChatEmote>? emotes = null, IReadOnlyDictionary<string, string>? metadata = null)
		{
			Id = id;
			IsSystemMessage = isSystemMessage;
			IsActionMessage = isActionMessage;
			IsMentioned = isMentioned;
			Message = message;
			Sender = sender;
			Channel = channel;
			Emotes = emotes ?? new ReadOnlyCollection<IChatEmote>(new List<IChatEmote>());
			Metadata = metadata switch
			{
				null => null,
				ReadOnlyDictionary<string, string> readOnly => readOnly,
				IDictionary<string, string> dictionary => new ReadOnlyDictionary<string, string>(dictionary),
				_ => new ReadOnlyDictionary<string, string>(CreateMetadataCopy(metadata))
			};
		}

		private static Dictionary<string, string> CreateMetadataCopy(IReadOnlyDictionary<string, string> metadata)
		{
			var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
			foreach (var pair in metadata)
			{
				copy[pair.Key] = pair.Value;
			}

			return copy;
		}

		public string Id { get; }
		public bool IsSystemMessage { get; }
		public bool IsActionMessage { get; }
		public bool IsMentioned { get; }
		public string Message { get; }
		public IChatUser Sender { get; }
		public BilibiliChannel Channel { get; }
		public ReadOnlyCollection<IChatEmote> Emotes { get; }
		public ReadOnlyDictionary<string, string>? Metadata { get; }
	}
}
