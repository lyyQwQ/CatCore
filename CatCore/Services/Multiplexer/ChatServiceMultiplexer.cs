using System;
using System.Collections.Generic;
using System.Linq;
using CatCore.Services.Bilibili.Interfaces;
using CatCore.Services.Interfaces;
using CatCore.Services.Twitch.Interfaces;

namespace CatCore.Services.Multiplexer
{
	public sealed class ChatServiceMultiplexer : IChatService<MultiplexedPlatformService, MultiplexedChannel, MultiplexedMessage>
	{
		private readonly ITwitchService _twitchPlatformService;
		private readonly IBilibiliService? _bilibiliPlatformService;

		/// <inheritdoc />
		public event Action<MultiplexedPlatformService>? OnAuthenticatedStateChanged;

		/// <inheritdoc />
		public event Action<MultiplexedPlatformService>? OnChatConnected;

		/// <inheritdoc />
		public event Action<MultiplexedPlatformService, MultiplexedMessage>? OnTextMessageReceived;

		/// <inheritdoc />
		public event Action<MultiplexedPlatformService, MultiplexedChannel>? OnJoinChannel;

		/// <inheritdoc />
		public event Action<MultiplexedPlatformService, MultiplexedChannel>? OnLeaveChannel;

		/// <inheritdoc />
		public event Action<MultiplexedPlatformService, MultiplexedChannel>? OnRoomStateUpdated;

		/// <inheritdoc />
		public event Action<MultiplexedPlatformService, MultiplexedChannel, string>? OnMessageDeleted;

		/// <inheritdoc />
		public event Action<MultiplexedPlatformService, MultiplexedChannel, string?>? OnChatCleared;

		public ChatServiceMultiplexer(IList<MultiplexedPlatformService> platformServices)
		{
			foreach (var platformService in platformServices)
			{
				// TODO: Register to all event handlers of IChatService
				platformService.OnAuthenticatedStateChanged += ChatServiceOnAuthenticatedStateChanged;
				platformService.OnChatConnected += ChatServiceOnChatConnected;
				platformService.OnJoinChannel += ChatServiceOnJoinChannel;
				platformService.OnLeaveChannel += ChatServiceOnLeaveChannel;
				platformService.OnRoomStateUpdated += ChatServiceOnRoomStateUpdated;
				platformService.OnTextMessageReceived += ChatServiceOnTextMessageReceived;
				platformService.OnMessageDeleted += ChatServiceOnMessageDeleted;
				platformService.OnChatCleared += ChatServiceOnChatCleared;
			}

			_twitchPlatformService = platformServices.Select(s => s.Underlying).OfType<ITwitchService>().First();
			_bilibiliPlatformService = platformServices.Select(s => s.Underlying).OfType<IBilibiliService>().FirstOrDefault();
		}

		/// <summary>
		/// Returns the Twitch service. Gives access to Twitch-specific features.
		/// </summary>
		/// <returns>Returns the Twitch service</returns>
		public ITwitchService GetTwitchPlatformService() => _twitchPlatformService;

		/// <summary>
		/// Returns the Bilibili service. Gives access to Bilibili-specific features.
		/// </summary>
		/// <returns>Returns the Bilibili service, or null if unavailable</returns>
		public IBilibiliService? GetBilibiliPlatformService() => _bilibiliPlatformService;

		private void ChatServiceOnAuthenticatedStateChanged(MultiplexedPlatformService scv)
		{
			OnAuthenticatedStateChanged?.Invoke(scv);
		}

		private void ChatServiceOnChatConnected(MultiplexedPlatformService scv)
		{
			OnChatConnected?.Invoke(scv);
		}

		private void ChatServiceOnJoinChannel(MultiplexedPlatformService scv, MultiplexedChannel channel)
		{
			OnJoinChannel?.Invoke(scv, channel);
		}

		private void ChatServiceOnLeaveChannel(MultiplexedPlatformService scv, MultiplexedChannel channel)
		{
			OnLeaveChannel?.Invoke(scv, channel);
		}

		private void ChatServiceOnRoomStateUpdated(MultiplexedPlatformService scv, MultiplexedChannel channel)
		{
			OnRoomStateUpdated?.Invoke(scv, channel);
		}

		private void ChatServiceOnTextMessageReceived(MultiplexedPlatformService scv, MultiplexedMessage message)
		{
			OnTextMessageReceived?.Invoke(scv, message);
		}

		private void ChatServiceOnMessageDeleted(MultiplexedPlatformService scv, MultiplexedChannel channel, string deletedMessageId)
		{
			OnMessageDeleted?.Invoke(scv, channel, deletedMessageId);
		}

		private void ChatServiceOnChatCleared(MultiplexedPlatformService scv, MultiplexedChannel channel, string? username)
		{
			OnChatCleared?.Invoke(scv, channel, username);
		}
	}
}
