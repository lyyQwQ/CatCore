using System;
using CatCore.Models.Shared;

namespace CatCore.Models.Bilibili
{
	public sealed class BilibiliChannel : IChatChannel<BilibiliChannel, BilibiliMessage>
	{
		private readonly Action<string>? _sendMessageAction;

		public BilibiliChannel(string id, string name, Action<string>? sendMessageAction = null)
		{
			Id = id;
			Name = name;
			_sendMessageAction = sendMessageAction;
		}

		public string Id { get; }
		public string Name { get; }

		public void SendMessage(string message)
		{
			_sendMessageAction?.Invoke(message);
		}

		public object Clone() => new BilibiliChannel(Id, Name, _sendMessageAction);
	}
}
