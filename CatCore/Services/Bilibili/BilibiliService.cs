using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CatCore.Models.Bilibili;
using CatCore.Models.Config;
using CatCore.Models.Shared;
using CatCore.Services.Bilibili.Interfaces;
using CatCore.Services.Bilibili.Internal;
using CatCore.Services.Bilibili.Layered;
using CatCore.Services.Interfaces;
using CatCore.Services.Overlay;
using Serilog;
using Timer = System.Timers.Timer;

namespace CatCore.Services.Bilibili
{
	internal sealed class BilibiliService : IBilibiliService, IDisposable
	{
		private const string LIVE_SOCKET_URL = "wss://broadcastlv.chat.bilibili.com:443/sub";
		private const string ROOM_INFO_API_URL = "https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id=";
		private const string AUTH_FLOW = "qr_cookie";

		private readonly ILogger _logger;
		private readonly IKittenSettingsService _settingsService;
		private readonly Random _random;

		private readonly SemaphoreSlim _connectLocker = new(1, 1);
		private readonly object _reconnectLock = new();
		private readonly BilibiliWebSocketClient _webSocketClient;
		private readonly IBilibiliAuthFacade _authFacade;
		private readonly IBilibiliProtocolCodec _protocolCodec;
		private readonly IBilibiliMessageNormalizer _messageNormalizer;
		private readonly IOverlayWebSocketService? _overlayWebSocketService;

		private readonly Timer _heartbeatTimer;
		private readonly Timer _heartbeatAckWatchdogTimer;

		private bool _isStarted;
		private bool _disposed;

		private int _reconnectAttempts;
		private DateTime _lastReconnectUtc = DateTime.MinValue;
		private int _reconnectScheduled;

		private long _lastHeartbeatSentTicksUtc;
		private long _lastHeartbeatAckTicksUtc;
		private int _heartbeatAckTimeoutTriggered;
		private int _isAuthenticated;

		private readonly int _heartbeatAckTimeoutMs;
		private readonly int _webSocketOpenTimeoutMs;

		private long _activeRoomId;
		private BilibiliAuthRequest? _pendingAuthRequest;
		private string _pendingConnectUri = string.Empty;
		private long _webSocketConnectAttempt;
		private long _webSocketOpenedAttempt;

		private string _chatToken = string.Empty;
		private string _buvid3 = string.Empty;
		private long _resolvedUserId;
		private long _activeBroadcasterUserId;
		private string _authSuspendedReason = string.Empty;
		private string _defaultSocketUri = LIVE_SOCKET_URL;
		private readonly object _danmuWealthIconCacheLock = new();
		private Dictionary<int, string> _danmuWealthLevelIconUrls = new();

		private BilibiliChannel? _currentChannel;

		public BilibiliService(ILogger logger, IKittenSettingsService settingsService, IOverlayWebSocketService overlayWebSocketService, Random random)
		{
			_logger = logger;
			_settingsService = settingsService;
			_overlayWebSocketService = overlayWebSocketService;
			_random = random;

			_webSocketClient = new BilibiliWebSocketClient();
			_webSocketClient.Opened += WebSocketClientOnOpened;
			_webSocketClient.Closed += WebSocketClientOnClosed;
			_webSocketClient.Error += WebSocketClientOnError;
			_webSocketClient.DataReceived += WebSocketClientOnDataReceived;

			_authFacade = new BilibiliAuthFacade();
			_protocolCodec = new BilibiliProtocolCodecAdapter();
			_messageNormalizer = new BilibiliMessageNormalizer();

			_heartbeatTimer = new Timer(30_000);
			_heartbeatTimer.AutoReset = true;
			_heartbeatTimer.Elapsed += HeartbeatTimerOnElapsed;

			_heartbeatAckTimeoutMs = ReadEnvInt("CATCORE_BILI_HEARTBEAT_ACK_TIMEOUT_MS", 90_000);
			_webSocketOpenTimeoutMs = ReadEnvInt("CATCORE_BILI_WS_OPEN_TIMEOUT_MS", 10_000);
			var heartbeatAckCheckMs = ReadEnvInt("CATCORE_BILI_HEARTBEAT_ACK_CHECK_MS", 1_000);
			if (heartbeatAckCheckMs < 50)
			{
				heartbeatAckCheckMs = 50;
			}

			_heartbeatAckWatchdogTimer = new Timer(heartbeatAckCheckMs);
			_heartbeatAckWatchdogTimer.AutoReset = true;
			_heartbeatAckWatchdogTimer.Elapsed += HeartbeatAckWatchdogTimerOnElapsed;

			_settingsService.OnConfigChanged += SettingsServiceOnConfigChanged;
		}

		public bool LoggedIn => !string.IsNullOrWhiteSpace(_settingsService.Config.BilibiliConfig.Cookies);

		public BilibiliChannel? DefaultChannel
		{
			get
			{
				if (_currentChannel != null)
				{
					return _currentChannel;
				}

				var roomId = _settingsService.Config.BilibiliConfig.RoomId;
				if (roomId <= 0)
				{
					return null;
				}

				return new BilibiliChannel(roomId.ToString(CultureInfo.InvariantCulture), roomId.ToString(CultureInfo.InvariantCulture), SendMessageToChannel);
			}
		}

		public event Action<IBilibiliService>? OnAuthenticatedStateChanged;
		public event Action<IBilibiliService>? OnChatConnected;
		public event Action<IBilibiliService, BilibiliChannel>? OnJoinChannel;
		public event Action<IBilibiliService, BilibiliChannel>? OnRoomStateUpdated;
		public event Action<IBilibiliService, BilibiliChannel>? OnLeaveChannel;
		public event Action<IBilibiliService, BilibiliMessage>? OnTextMessageReceived;
		public event Action<IBilibiliService, BilibiliChannel, string>? OnMessageDeleted;
		public event Action<IBilibiliService, BilibiliChannel, string?>? OnChatCleared;

		async Task IPlatformService<IBilibiliService, BilibiliChannel, BilibiliMessage>.Start()
		{
			if (_isStarted)
			{
				return;
			}

			_isStarted = true;
			_authSuspendedReason = string.Empty;
			TryWarmUpBadgeRenderer();
			await ConnectCoreAsync(false).ConfigureAwait(false);
		}

		Task IPlatformService<IBilibiliService, BilibiliChannel, BilibiliMessage>.Stop()
		{
			if (!_isStarted)
			{
				return Task.CompletedTask;
			}

			_isStarted = false;
			Interlocked.Exchange(ref _reconnectScheduled, 0);
			_heartbeatTimer.Stop();
			_heartbeatAckWatchdogTimer.Stop();
			Interlocked.Exchange(ref _isAuthenticated, 0);
			Interlocked.Exchange(ref _lastHeartbeatSentTicksUtc, 0);
			Interlocked.Exchange(ref _lastHeartbeatAckTicksUtc, 0);
			Interlocked.Exchange(ref _webSocketOpenedAttempt, 0);
			_activeBroadcasterUserId = 0;
			_webSocketClient.Disconnect();

			if (_currentChannel != null)
			{
				OnLeaveChannel?.Invoke(this, _currentChannel);
				_currentChannel = null;
			}

			return Task.CompletedTask;
		}

		private async void SettingsServiceOnConfigChanged(IKittenSettingsService service, ConfigRoot config)
		{
			if (!_isStarted)
			{
				return;
			}

			_authSuspendedReason = string.Empty;
			TryWarmUpBadgeRenderer();
			await ConnectCoreAsync(true).ConfigureAwait(false);
		}

		private void TryWarmUpBadgeRenderer()
		{
			try
			{
				if (!(_settingsService?.Config?.BilibiliConfig?.ShowBadge ?? true))
				{
					return;
				}

				LegacySvgRendererBridge.WarmUpInBackground();
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "[BADGE_SVG_BRIDGE] warm-up trigger failed");
			}
		}

		private async Task ConnectCoreAsync(bool forceReconnect)
		{
			if (!_isStarted || _disposed)
			{
				return;
			}

			await _connectLocker.WaitAsync().ConfigureAwait(false);
			try
			{
				if (!_isStarted || _disposed)
				{
					return;
				}

				var config = _settingsService.Config.BilibiliConfig;
				if (!IsConfigReady(config, out var notReadyReason))
				{
					if (!string.IsNullOrWhiteSpace(notReadyReason))
					{
						_logger.Information("Bilibili start skipped: {Reason}", notReadyReason);
					}
					return;
				}

				if (!string.IsNullOrWhiteSpace(_authSuspendedReason))
				{
					_logger.Warning("Bilibili reconnect suspended: {Reason}", _authSuspendedReason);
					return;
				}

				if (forceReconnect)
				{
					_heartbeatTimer.Stop();
					_heartbeatAckWatchdogTimer.Stop();
					Interlocked.Exchange(ref _isAuthenticated, 0);
					_webSocketClient.Disconnect();
				}

				_pendingAuthRequest = null;
				_pendingConnectUri = LIVE_SOCKET_URL;
				_defaultSocketUri = LIVE_SOCKET_URL;

				if (!await PrepareDefaultAuthAsync(config).ConfigureAwait(false))
				{
					ScheduleReconnect("default_auth_prepare_failed");
					return;
				}

				_activeRoomId = config.RoomId;
				_pendingConnectUri = string.IsNullOrWhiteSpace(_defaultSocketUri) ? LIVE_SOCKET_URL : _defaultSocketUri;
				_pendingAuthRequest = BilibiliAuthRequest.ForDefault(config.RoomId, _resolvedUserId, _chatToken, _buvid3);

				if (_pendingAuthRequest == null || string.IsNullOrWhiteSpace(_pendingConnectUri))
				{
					ScheduleReconnect("auth_request_missing");
					return;
				}

				var connectAttempt = Interlocked.Increment(ref _webSocketConnectAttempt);
				Interlocked.Exchange(ref _webSocketOpenedAttempt, 0);
				_logger.Information("Connecting to Bilibili websocket mode={Mode} room={RoomId} attempt={Attempt} endpoint={Endpoint}", AUTH_FLOW, _activeRoomId, connectAttempt, RedactEndpoint(_pendingConnectUri));
				_logger.Information("BILI_WS_CONNECT_CALL_BEGIN mode={Mode} room={RoomId} attempt={Attempt} endpoint={Endpoint}", AUTH_FLOW, _activeRoomId, connectAttempt, RedactEndpoint(_pendingConnectUri));
				try
				{
					_webSocketClient.Connect(_pendingConnectUri, forceReconnect || _webSocketClient.IsConnected, BilibiliAuthHttpClient.BilibiliUserAgent, "https://live.bilibili.com");
					_logger.Information("BILI_WS_CONNECT_CALL_RETURN mode={Mode} room={RoomId} attempt={Attempt}", AUTH_FLOW, _activeRoomId, connectAttempt);
				}
				catch (Exception ex)
				{
					_logger.Warning(ex, "BILI_WS_CONNECT_CALL_THROW mode={Mode} room={RoomId} attempt={Attempt} endpoint={Endpoint}", AUTH_FLOW, _activeRoomId, connectAttempt, RedactEndpoint(_pendingConnectUri));
					ScheduleReconnect("ws_connect_exception");
					return;
				}

				StartWebSocketOpenWatchdog(connectAttempt, AUTH_FLOW, _activeRoomId);
			}
			finally
			{
				_connectLocker.Release();
			}
		}

		private void WebSocketClientOnOpened()
		{
			var connectAttempt = Interlocked.Read(ref _webSocketConnectAttempt);
			_logger.Information("BILI_WS_OPENED_ENTER mode={Mode} room={RoomId} attempt={Attempt} started={Started} pendingAuth={PendingAuth}", AUTH_FLOW, _activeRoomId, connectAttempt, _isStarted, _pendingAuthRequest != null);
			Interlocked.Exchange(ref _webSocketOpenedAttempt, connectAttempt);

			if (!_isStarted || _pendingAuthRequest == null)
			{
				return;
			}

			if (!_authFacade.TryBuildGreetingPacket(_pendingAuthRequest, out var packet, out var failureReason))
			{
				_logger.Warning("Bilibili auth precheck failed: {FailureReason}", failureReason);
				ScheduleReconnect("auth_precheck_failed");
				return;
			}

			_logger.Information("BILI_WS_OPEN mode={Mode} room={RoomId} attempt={Attempt}", AUTH_FLOW, _activeRoomId, connectAttempt);

			_webSocketClient.Send(packet);
		}

		private void WebSocketClientOnClosed()
		{
			var connectAttempt = Interlocked.Read(ref _webSocketConnectAttempt);
			_logger.Information("BILI_WS_CLOSED_ENTER mode={Mode} room={RoomId} attempt={Attempt} started={Started}", AUTH_FLOW, _activeRoomId, connectAttempt, _isStarted);
			HandleDisconnected("closed", null);
		}

		private void WebSocketClientOnError(Exception? exception)
		{
			var connectAttempt = Interlocked.Read(ref _webSocketConnectAttempt);
			_logger.Information("BILI_WS_ERROR_ENTER mode={Mode} room={RoomId} attempt={Attempt} started={Started} exceptionNull={ExceptionNull} message={Message}", AUTH_FLOW, _activeRoomId, connectAttempt, _isStarted, exception == null, exception?.Message ?? string.Empty);

			if (exception != null)
			{
				_logger.Warning(exception, "Bilibili websocket error");
			}

			HandleDisconnected("error", exception);
		}

		private void HandleDisconnected(string reason, Exception? exception)
		{
			var connectAttempt = Interlocked.Read(ref _webSocketConnectAttempt);

			_heartbeatTimer.Stop();
			_heartbeatAckWatchdogTimer.Stop();
			Interlocked.Increment(ref _webSocketConnectAttempt);
			Interlocked.Exchange(ref _webSocketOpenedAttempt, 0);
			Interlocked.Exchange(ref _isAuthenticated, 0);
			Interlocked.Exchange(ref _lastHeartbeatSentTicksUtc, 0);
			Interlocked.Exchange(ref _lastHeartbeatAckTicksUtc, 0);
			Interlocked.Exchange(ref _heartbeatAckTimeoutTriggered, 0);
			_activeBroadcasterUserId = 0;

			if (_currentChannel != null)
			{
				OnLeaveChannel?.Invoke(this, _currentChannel);
				_currentChannel = null;
			}

			if (!_isStarted)
			{
				_logger.Information("BILI_WS_DISCONNECTED_WHILE_STOPPED mode={Mode} room={RoomId} attempt={Attempt} reason={Reason} message={Message}", AUTH_FLOW, _activeRoomId, connectAttempt, reason, exception?.Message ?? string.Empty);
				return;
			}

			_logger.Information("BILI_WS_DISCONNECTED mode={Mode} room={RoomId} attempt={Attempt} reason={Reason} message={Message}", AUTH_FLOW, _activeRoomId, connectAttempt, reason, exception?.Message ?? string.Empty);

			if (exception != null)
			{
				_logger.Information("Bilibili disconnected ({Reason}): {Message}", reason, exception.Message);
			}
			else
			{
				_logger.Information("Bilibili disconnected ({Reason})", reason);
			}

			ScheduleReconnect(reason);
		}

		private void StartWebSocketOpenWatchdog(long connectAttempt, string mode, long roomId)
		{
			if (_webSocketOpenTimeoutMs <= 0)
			{
				return;
			}

			_logger.Information("BILI_WS_OPEN_WATCHDOG_ARM mode={Mode} room={RoomId} attempt={Attempt} timeoutMs={TimeoutMs}", mode, roomId, connectAttempt, _webSocketOpenTimeoutMs);

			var watchdogTimer = new Timer(_webSocketOpenTimeoutMs)
			{
				AutoReset = false
			};

			watchdogTimer.Elapsed += (_, _) =>
			{
				try
				{
					var currentConnectAttempt = Interlocked.Read(ref _webSocketConnectAttempt);
					var openedAttempt = Interlocked.Read(ref _webSocketOpenedAttempt);
					var started = _isStarted;
					var disposed = _disposed;

					_logger.Information("BILI_WS_OPEN_WATCHDOG_CHECK mode={Mode} room={RoomId} attempt={Attempt} currentAttempt={CurrentAttempt} openedAttempt={OpenedAttempt} started={Started} disposed={Disposed}", mode, roomId, connectAttempt, currentConnectAttempt, openedAttempt, started, disposed);

					if (connectAttempt != currentConnectAttempt)
					{
						return;
					}

					if (!started || disposed)
					{
						return;
					}

					if (connectAttempt == openedAttempt)
					{
						return;
					}

					const string reason = "ws_open_timeout";
					_logger.Warning("BILI_WS_OPEN_TIMEOUT mode={Mode} room={RoomId} attempt={Attempt} reason={Reason}", mode, roomId, connectAttempt, reason);
					_webSocketClient.Disconnect();
					ScheduleReconnect(reason);
				}
				catch (Exception ex)
				{
					_logger.Warning(ex, "BILI_WS_OPEN_WATCHDOG_ERROR mode={Mode} room={RoomId} attempt={Attempt}", mode, roomId, connectAttempt);
				}
				finally
				{
					watchdogTimer.Dispose();
				}
			};

			try
			{
				watchdogTimer.Start();
			}
			catch (Exception ex)
			{
				watchdogTimer.Dispose();
				_logger.Warning(ex, "BILI_WS_OPEN_WATCHDOG_ARM_FAILED mode={Mode} room={RoomId} attempt={Attempt}", mode, roomId, connectAttempt);
			}
		}

		private void ScheduleReconnect(string reason)
		{
			if (!_isStarted)
			{
				return;
			}

			if (Interlocked.CompareExchange(ref _reconnectScheduled, 1, 0) != 0)
			{
				return;
			}

			var delay = TimeSpan.Zero;
			lock (_reconnectLock)
			{
				var nowUtc = DateTime.UtcNow;
				var elapsed = nowUtc - _lastReconnectUtc;
				if (elapsed < TimeSpan.FromSeconds(5))
				{
					var seconds = Math.Min(30.0, Math.Pow(2, _reconnectAttempts));
					delay = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(_random.Next(0, 750));
				}

				if (elapsed > TimeSpan.FromMinutes(1))
				{
					_reconnectAttempts = 0;
				}

				_reconnectAttempts++;
				_lastReconnectUtc = nowUtc;
			}

			_ = Task.Run(async () =>
			{
				try
				{
					if (delay > TimeSpan.Zero)
					{
						_logger.Information("Bilibili reconnect scheduled in {DelayMs}ms ({Reason})", (int)delay.TotalMilliseconds, reason);
						await Task.Delay(delay).ConfigureAwait(false);
					}

					if (_isStarted)
					{
						await ConnectCoreAsync(true).ConfigureAwait(false);
					}
				}
				finally
				{
					Interlocked.Exchange(ref _reconnectScheduled, 0);
				}
			});
		}

		private void WebSocketClientOnDataReceived(byte[] frame)
		{
			if (!_isStarted)
			{
				return;
			}

			var decodeResult = _protocolCodec.Decode(frame);
			if (!string.IsNullOrWhiteSpace(decodeResult.Diagnostic))
			{
				_logger.Warning("Bilibili decode diagnostic: {Diagnostic}", decodeResult.Diagnostic);
			}

			foreach (var packet in decodeResult.Messages)
			{
				var normalizedEvent = _messageNormalizer.Normalize(packet);
				switch (normalizedEvent.Kind)
				{
					case BilibiliNormalizedEventKind.AuthAck:
						HandleAuthAck(packet.Body);
						break;
					case BilibiliNormalizedEventKind.HeartbeatAck:
						Interlocked.Exchange(ref _lastHeartbeatAckTicksUtc, DateTime.UtcNow.Ticks);
						Interlocked.Exchange(ref _heartbeatAckTimeoutTriggered, 0);
						break;
					case BilibiliNormalizedEventKind.Chat:
						HandleChatPacket(packet);
						break;
					case BilibiliNormalizedEventKind.DeleteMessage:
						if (_currentChannel != null)
						{
							foreach (var messageId in normalizedEvent.MessageIds)
							{
								if (!string.IsNullOrWhiteSpace(messageId))
								{
									OnMessageDeleted?.Invoke(this, _currentChannel, messageId);
								}
							}
						}
						break;
					case BilibiliNormalizedEventKind.ClearChat:
						if (_currentChannel != null)
						{
							OnChatCleared?.Invoke(this, _currentChannel, null);
						}
						break;
					case BilibiliNormalizedEventKind.StopRoom:
						_logger.Warning("Bilibili reported stop-room event");
						break;
				}
			}
		}

		private void HandleAuthAck(string body)
		{
			if (TryParseAuthAck(body, out var code, out var message) && !string.Equals(code, "0", StringComparison.Ordinal))
			{
				_authSuspendedReason = $"code={code}, message={message}";
				_logger.Warning("BILI_AUTH_ACK_FAILED mode={Mode} room={RoomId} code={Code} message={Message}", AUTH_FLOW, _activeRoomId, code, message);
				_logger.Warning("Bilibili auth rejected: {Reason}", _authSuspendedReason);
				_ = ((IPlatformService<IBilibiliService, BilibiliChannel, BilibiliMessage>)this).Stop();
				return;
			}

			_logger.Information("BILI_AUTH_ACK_SUCCESS mode={Mode} room={RoomId}", AUTH_FLOW, _activeRoomId);

			_authSuspendedReason = string.Empty;
			Interlocked.Exchange(ref _isAuthenticated, 1);
			Interlocked.Exchange(ref _heartbeatAckTimeoutTriggered, 0);
			Interlocked.Exchange(ref _lastHeartbeatSentTicksUtc, 0);
			Interlocked.Exchange(ref _lastHeartbeatAckTicksUtc, DateTime.UtcNow.Ticks);

			lock (_reconnectLock)
			{
				_reconnectAttempts = 0;
			}

			EnsureCurrentChannelMatchesActiveRoom(raiseLeaveWhenChanged: true, raiseJoinWhenChanged: true, raiseRoomStateUpdated: true);

			OnAuthenticatedStateChanged?.Invoke(this);
			OnChatConnected?.Invoke(this);

			SendHeartbeatPacket();
			if (!_heartbeatTimer.Enabled)
			{
				_heartbeatTimer.Start();
			}

			if (!_heartbeatAckWatchdogTimer.Enabled && _heartbeatAckTimeoutMs > 0)
			{
				_heartbeatAckWatchdogTimer.Start();
			}
		}

		private void HandleChatPacket(BilibiliDecodedPacket packet)
		{
			if (packet.BodyBytes.IsEmpty)
			{
				return;
			}

			try
			{
				using var document = JsonDocument.Parse(packet.BodyBytes);
				var root = document.RootElement;
				if (root.ValueKind != JsonValueKind.Object)
				{
					return;
				}

				var command = ResolveCommand(root);
				EnsureCurrentChannel();
				var message = _currentChannel == null ? null : TryBuildChatMessage(root, string.Empty, command, _currentChannel);
				PublishOverlayPackets(packet, root, command, message);

				var textMessageReceived = OnTextMessageReceived;
				var messageDeleted = OnMessageDeleted;
				var chatCleared = OnChatCleared;
				if (IsDeleteMessageCommand(command))
				{
					if (_currentChannel == null || messageDeleted == null)
					{
						return;
					}

					foreach (var messageId in ExtractDeletedMessageIds(root))
					{
						if (!string.IsNullOrWhiteSpace(messageId))
						{
							messageDeleted(this, _currentChannel, messageId);
						}
					}

					return;
				}

				if (IsClearChatCommand(command))
				{
					if (_currentChannel != null && chatCleared != null)
					{
						chatCleared(this, _currentChannel, null);
					}

					return;
				}

				if (_currentChannel == null || message == null)
				{
					return;
				}

				_logger.Debug("BILI_CHAT_RECEIVED cmd={Command} user={User} uid={UserId} len={TextLength}", command, message.Sender.UserName, message.Sender.Id, message.Message?.Length ?? 0);
				if (textMessageReceived != null)
				{
					textMessageReceived(this, message);
				}
			}
			catch
			{
				// Ignore invalid chat payloads.
			}
		}

		private void EnsureCurrentChannel()
		{
			EnsureCurrentChannelMatchesActiveRoom(raiseLeaveWhenChanged: true, raiseJoinWhenChanged: true, raiseRoomStateUpdated: false);
		}

		private void EnsureCurrentChannelMatchesActiveRoom(bool raiseLeaveWhenChanged, bool raiseJoinWhenChanged, bool raiseRoomStateUpdated)
		{
			var channelId = _activeRoomId > 0 ? _activeRoomId.ToString(CultureInfo.InvariantCulture) : "bilibili";
			var currentChannel = _currentChannel;
			if (currentChannel != null && string.Equals(currentChannel.Id, channelId, StringComparison.Ordinal))
			{
				if (raiseRoomStateUpdated)
				{
					OnRoomStateUpdated?.Invoke(this, currentChannel);
				}

				return;
			}

			if (currentChannel != null && raiseLeaveWhenChanged)
			{
				OnLeaveChannel?.Invoke(this, currentChannel);
			}

			_currentChannel = new BilibiliChannel(channelId, channelId, SendMessageToChannel);
			if (raiseJoinWhenChanged)
			{
				OnJoinChannel?.Invoke(this, _currentChannel);
			}

			if (raiseRoomStateUpdated)
			{
				OnRoomStateUpdated?.Invoke(this, _currentChannel);
			}
		}

		private void PublishOverlayPackets(BilibiliDecodedPacket packet, JsonElement root, string command, BilibiliMessage? message)
		{
			var overlayWebSocketService = _overlayWebSocketService;
			if (overlayWebSocketService == null)
			{
				return;
			}

			var rawBody = packet.Body;
			if (!string.IsNullOrWhiteSpace(rawBody))
			{
				overlayWebSocketService.BroadcastData("bilibili_raw", rawBody);
			}

			var payload = BuildOverlayPayload(root, command, message);
			if (!string.IsNullOrWhiteSpace(payload))
			{
				overlayWebSocketService.BroadcastData("bilibili", payload);
			}
		}

		private static string BuildOverlayPayload(JsonElement root, string command, BilibiliMessage? message)
		{
			var messageType = ResolveOverlayMessageType(command, root);
			var senderUserName = message?.Sender?.UserName;
			if (string.IsNullOrWhiteSpace(senderUserName))
			{
				senderUserName = ResolveOverlayUserName(root);
			}

			var username = message?.Sender?.DisplayName;
			if (string.IsNullOrWhiteSpace(username))
			{
				username = senderUserName;
			}

			var messageText = message?.Message;
			if (string.IsNullOrWhiteSpace(messageText))
			{
				messageText = ResolveOverlayMessageText(root, senderUserName);
			}

			var senderUserId = message?.Sender?.Id;
			if (string.IsNullOrWhiteSpace(senderUserId))
			{
				senderUserId = ResolveOverlayUserId(root);
			}

			var payload = new Dictionary<string, object>(StringComparer.Ordinal)
			{
				["MessageType"] = messageType,
				["Message"] = messageText ?? string.Empty,
				["Content"] = messageText ?? string.Empty,
				["Uid"] = senderUserId ?? "0",
				["Username"] = username ?? senderUserName ?? "Bilibili",
				["Sender"] = new Dictionary<string, object>(StringComparer.Ordinal)
				{
					["UserName"] = senderUserName ?? "Bilibili",
					["Badges"] = Array.Empty<object>()
				},
				["extra"] = BuildOverlayExtra(root, command),
				["Emotes"] = BuildOverlayEmotes(message)
			};

			return JsonSerializer.Serialize(payload);
		}

		private static string ResolveOverlayMessageType(string command, JsonElement root)
		{
			if (command.StartsWith("DANMU_MSG", StringComparison.OrdinalIgnoreCase))
			{
				if (root.TryGetProperty("info", out var infoNode)
					&& infoNode.ValueKind == JsonValueKind.Array
					&& TryGetArrayElement(infoNode, 0, out var info0Node)
					&& info0Node.ValueKind == JsonValueKind.Array
					&& TryGetArrayElement(info0Node, 12, out var emoteTypeNode)
					&& GetElementInt(emoteTypeNode, 0) == 1)
				{
					return "danmuku_motion";
				}

				return "danmuku";
			}

			switch (command)
			{
				case "LIVE_OPEN_PLATFORM_DM":
					if (root.TryGetProperty("data", out var dataNode)
						&& dataNode.ValueKind == JsonValueKind.Object
						&& GetPropertyInt(dataNode, "dm_type", 0) == 1)
					{
						return "danmuku_motion";
					}

					return "danmuku";
				case "SEND_GIFT":
				case "LIVE_OPEN_PLATFORM_SEND_GIFT":
					return "gift";
				case "SUPER_CHAT_MESSAGE":
				case "LIVE_OPEN_PLATFORM_SUPER_CHAT":
					return "super_chat";
				case "SUPER_CHAT_MESSAGE_JPN":
					return "super_chat_japanese";
				case "LIKE_INFO_V3_CLICK":
				case "LIVE_OPEN_PLATFORM_LIKE":
					return "like_info";
				case "GUARD_BUY":
					return "new_guard";
				case "USER_TOAST_MSG":
				case "LIVE_OPEN_PLATFORM_GUARD":
					return "new_guard_msg";
				case "INTERACT_WORD":
					if (root.TryGetProperty("data", out var interactNode) && interactNode.ValueKind == JsonValueKind.Object)
					{
						switch (GetPropertyInt(interactNode, "msg_type", 0))
						{
							case 1:
								return "welcome";
							case 2:
								return "follow";
							case 3:
								return "share";
							case 4:
								return "special_follow";
							case 5:
								return "mutual_follow";
						}
					}

					break;
			}

			return string.IsNullOrWhiteSpace(command) ? "unknown" : command;
		}

		private static string ResolveOverlayUserName(JsonElement root)
		{
			if (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
			{
				var userName = FirstNonEmpty(
					GetPropertyString(dataNode, "uname"),
					GetPropertyString(dataNode, "username"),
					GetPropertyString(dataNode, "sender_username"),
					GetNestedPropertyString(dataNode, "user_info", "uname"));

				if (!string.IsNullOrWhiteSpace(userName))
				{
					return userName;
				}
			}

			if (root.TryGetProperty("info", out var infoNode)
				&& infoNode.ValueKind == JsonValueKind.Array
				&& TryGetArrayElement(infoNode, 2, out var userNode)
				&& userNode.ValueKind == JsonValueKind.Array)
			{
				var infoUserName = GetArrayString(userNode, 1);
				if (!string.IsNullOrWhiteSpace(infoUserName))
				{
					return infoUserName;
				}
			}

			return "Bilibili";
		}

		private static string ResolveOverlayUserId(JsonElement root)
		{
			if (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
			{
				var userId = FirstNonEmpty(
					GetPropertyString(dataNode, "uid"),
					GetPropertyString(dataNode, "sender_uid"),
					GetNestedPropertyString(dataNode, "user_info", "uid"));

				if (!string.IsNullOrWhiteSpace(userId))
				{
					return userId;
				}
			}

			if (root.TryGetProperty("info", out var infoNode)
				&& infoNode.ValueKind == JsonValueKind.Array
				&& TryGetArrayElement(infoNode, 2, out var userNode)
				&& userNode.ValueKind == JsonValueKind.Array)
			{
				var infoUserId = GetArrayString(userNode, 0, "0");
				if (!string.IsNullOrWhiteSpace(infoUserId))
				{
					return infoUserId;
				}
			}

			return "0";
		}

		private static string ResolveOverlayMessageText(JsonElement root, string? fallbackUserName)
		{
			if (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
			{
				var text = FirstNonEmpty(
					GetPropertyString(dataNode, "message"),
					GetPropertyString(dataNode, "msg"),
					GetPropertyString(dataNode, "content"),
					GetPropertyString(dataNode, "like_text"),
					GetPropertyString(dataNode, "notice_msg"));

				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}

				var giftName = FirstNonEmpty(GetPropertyString(dataNode, "giftName"), GetPropertyString(dataNode, "gift_name"));
				if (!string.IsNullOrWhiteSpace(giftName))
				{
					var giftNum = FirstNonEmpty(GetPropertyString(dataNode, "num"), GetPropertyString(dataNode, "gift_num"), "1");
					return $"{fallbackUserName ?? "Bilibili"} 赠送 {giftNum}x{giftName}";
				}
			}

			if (root.TryGetProperty("info", out var infoNode) && infoNode.ValueKind == JsonValueKind.Array)
			{
				var infoText = GetArrayString(infoNode, 1);
				if (!string.IsNullOrWhiteSpace(infoText))
				{
					return infoText;
				}
			}

			return string.Empty;
		}

		private static Dictionary<string, object> BuildOverlayExtra(JsonElement root, string command)
		{
			var extra = new Dictionary<string, object>(StringComparer.Ordinal)
			{
				["cmd"] = command ?? string.Empty
			};

			if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
			{
				return extra;
			}

			var giftPrice = FirstNonEmpty(
				GetPropertyString(dataNode, "gift_price"),
				GetPropertyString(dataNode, "price"),
				GetPropertyString(dataNode, "total_price"));
			if (!string.IsNullOrWhiteSpace(giftPrice))
			{
				extra["gift_price"] = giftPrice;
			}

			var giftImage = FirstNonEmpty(
				GetPropertyString(dataNode, "gift_img"),
				GetPropertyString(dataNode, "gift_icon"),
				GetPropertyString(dataNode, "giftIcon"));
			if (!string.IsNullOrWhiteSpace(giftImage))
			{
				extra["gift_img"] = NormalizeBilibiliImageUrl(giftImage);
			}

			var scPrice = FirstNonEmpty(
				GetPropertyString(dataNode, "sc_price"),
				GetPropertyString(dataNode, "price"),
				GetPropertyString(dataNode, "rmb"));
			if (!string.IsNullOrWhiteSpace(scPrice))
			{
				extra["sc_price"] = scPrice;
			}

			var giftType = GetPropertyString(dataNode, "gift_type");
			if (string.IsNullOrWhiteSpace(giftType) && dataNode.TryGetProperty("paid", out var paidNode) && paidNode.ValueKind == JsonValueKind.True)
			{
				giftType = "gold";
			}
			else if (string.IsNullOrWhiteSpace(giftType) && dataNode.TryGetProperty("paid", out paidNode) && paidNode.ValueKind == JsonValueKind.False)
			{
				giftType = "silver";
			}

			if (!string.IsNullOrWhiteSpace(giftType))
			{
				extra["gift_type"] = giftType;
			}

			return extra;
		}

		private static object[] BuildOverlayEmotes(BilibiliMessage? message)
		{
			if (message?.Emotes == null || message.Emotes.Count == 0)
			{
				return Array.Empty<object>();
			}

			var emotes = new object[message.Emotes.Count];
			for (var i = 0; i < message.Emotes.Count; i++)
			{
				var emote = message.Emotes[i];
				emotes[i] = new Dictionary<string, object>(StringComparer.Ordinal)
				{
					["Id"] = emote.Id,
					["Name"] = emote.Name,
					["Uri"] = emote.Url,
					["StartIndex"] = emote.StartIndex,
					["EndIndex"] = emote.EndIndex
				};
			}

			return emotes;
		}

		private static string ResolveCommand(JsonElement root)
		{
			if (root.TryGetProperty("cmd", out var cmdNode))
			{
				return cmdNode.GetString() ?? string.Empty;
			}

			return string.Empty;
		}

		private static bool IsDeleteMessageCommand(string command)
		{
			return string.Equals(command, "LIVE_OPEN_PLATFORM_SUPER_CHAT_DEL", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsClearChatCommand(string command)
		{
			return string.Equals(command, "CLEAR_DANMU", StringComparison.OrdinalIgnoreCase);
		}

		private static IReadOnlyList<string> ExtractDeletedMessageIds(JsonElement root)
		{
			var ids = new List<string>();
			if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
			{
				return ids;
			}

			if (dataNode.TryGetProperty("message_ids", out var messageIds) && messageIds.ValueKind == JsonValueKind.Array)
			{
				foreach (var messageId in messageIds.EnumerateArray())
				{
					var id = messageId.ValueKind switch
					{
						JsonValueKind.String => messageId.GetString(),
						JsonValueKind.Number => messageId.GetRawText(),
						_ => null
					};

					if (!string.IsNullOrWhiteSpace(id))
					{
						ids.Add(id!);
					}
				}
			}

			if (dataNode.TryGetProperty("msg_id", out var msgIdNode))
			{
				var msgId = msgIdNode.ValueKind switch
				{
					JsonValueKind.String => msgIdNode.GetString(),
					JsonValueKind.Number => msgIdNode.GetRawText(),
					_ => null
				};

				if (!string.IsNullOrWhiteSpace(msgId) && !ids.Contains(msgId!))
				{
					ids.Add(msgId!);
				}
			}

			return ids;
		}

		private BilibiliMessage? TryBuildChatMessage(JsonElement root, string rawBody, string command, BilibiliChannel channel)
		{
			try
			{
				var resolvedCommand = !string.IsNullOrWhiteSpace(command)
					? command
					: root.TryGetProperty("cmd", out var cmdNode)
						? cmdNode.GetString() ?? string.Empty
						: string.Empty;

				var text = string.Empty;
				var userId = "0";
				var userName = "Bilibili";
				var senderDisplayName = string.Empty;
				var danmuMedalPrefix = string.Empty;
				var danmuHonorPrefix = string.Empty;
				var showBadge = _settingsService?.Config?.BilibiliConfig?.ShowBadge ?? true;
				var color = "#FFFFFF";
				var isBroadcaster = false;
				var isModerator = false;
				List<IChatEmote>? emotes = null;
				HashSet<string>? emoteIdentities = null;
				List<BilibiliRichImage>? richImages = null;
				HashSet<string>? richImageIds = null;
				ReadOnlyCollection<IChatBadge>? senderBadges = null;

				if (resolvedCommand.StartsWith("DANMU_MSG", StringComparison.OrdinalIgnoreCase))
				{
					if (root.TryGetProperty("info", out var infoNode) && infoNode.ValueKind == JsonValueKind.Array)
					{
						text = GetArrayString(infoNode, 1);
						if (infoNode.GetArrayLength() > 2)
						{
							var userNode = infoNode[2];
							if (userNode.ValueKind == JsonValueKind.Array)
							{
								userId = GetArrayString(userNode, 0, "0");
								userName = GetArrayString(userNode, 1, "Bilibili");
								senderDisplayName = userName;
								if (userNode.GetArrayLength() > 2)
								{
									isModerator = GetArrayString(userNode, 2) == "1";
								}
								if (userNode.GetArrayLength() > 7)
								{
									var rawColor = GetArrayString(userNode, 7);
									if (!string.IsNullOrWhiteSpace(rawColor))
									{
										color = rawColor.StartsWith("#", StringComparison.Ordinal) ? rawColor : $"#{rawColor}";
									}
								}
							}
						}

						danmuMedalPrefix = showBadge
							? ResolveDanmuMedalDisplayPrefix(infoNode, root)
							: string.Empty;
						danmuHonorPrefix = ResolveDanmuHonorDisplayPrefix(infoNode);
						#if BADGE_DEBUG
						Log.Information("[TASK14DBG][CATCORE] " + $"stage=danmu-prefix uid={userId} uname={Task14ToLogValue(userName)} hasMedalPrefix={!string.IsNullOrWhiteSpace(danmuMedalPrefix)} hasHonorPrefix={!string.IsNullOrWhiteSpace(danmuHonorPrefix)}");
						#endif
						senderBadges = BuildDanmuBadges(infoNode, showBadge, userId, userName);
						if (senderBadges != null && senderBadges.Count > 0)
						{
							danmuMedalPrefix = string.Empty;
						}

						var hasDanmuRichMedia = false;
						if (TryGetArrayElement(infoNode, 0, out var info0Node) && info0Node.ValueKind == JsonValueKind.Array)
						{
							hasDanmuRichMedia = (TryGetArrayElement(info0Node, 13, out var emoteNode) && emoteNode.ValueKind == JsonValueKind.Object)
								|| (TryGetArrayElement(info0Node, 15, out var metaNode) && metaNode.ValueKind == JsonValueKind.Object);
						}

						hasDanmuRichMedia = hasDanmuRichMedia
							|| (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object);

						var hasDanmuRoleSemantic = HasDanmuRoleSemanticFallback(root, infoNode, userId);
						isBroadcaster = ResolveDanmuIsBroadcaster(root, infoNode, userId);

						if (hasDanmuRichMedia || hasDanmuRoleSemantic)
						{
							emotes = new List<IChatEmote>();
							emoteIdentities = new HashSet<string>(StringComparer.Ordinal);
							richImages = new List<BilibiliRichImage>();
							richImageIds = new HashSet<string>(StringComparer.Ordinal);
							TryExtractDanmuRichMedia(root, infoNode, text, userId, userName, emotes, emoteIdentities, richImages, richImageIds);
						}
					}
				}
				else
				{
					switch (resolvedCommand)
					{
						case "LIVE_OPEN_PLATFORM_DM":
						if (root.TryGetProperty("data", out var dmDataNode) && dmDataNode.ValueKind == JsonValueKind.Object)
						{
							text = GetPropertyString(dmDataNode, "msg");
							userId = GetPropertyString(dmDataNode, "uid", "0");
							userName = GetPropertyString(dmDataNode, "uname", "Bilibili");
							var hasOpenLiveRichMedia = dmDataNode.TryGetProperty("emoji_img_url", out _)
								|| dmDataNode.TryGetProperty("emoji_img", out _)
								|| dmDataNode.TryGetProperty("uface", out _)
								|| dmDataNode.TryGetProperty("face", out _)
								|| dmDataNode.TryGetProperty("user", out _)
								|| dmDataNode.TryGetProperty("user_info", out _)
								|| dmDataNode.TryGetProperty("fans_medal", out _)
								|| dmDataNode.TryGetProperty("medal", out _)
								|| dmDataNode.TryGetProperty("medal_info", out _)
								|| dmDataNode.TryGetProperty("badge_info", out _)
								|| dmDataNode.TryGetProperty("badge", out _)
								|| dmDataNode.TryGetProperty("title_info", out _)
								|| dmDataNode.TryGetProperty("nameplate", out _)
								|| dmDataNode.TryGetProperty("wealth_level", out _)
								|| dmDataNode.TryGetProperty("guard_info", out _)
								|| dmDataNode.TryGetProperty("icon", out _)
								|| dmDataNode.TryGetProperty("extra", out _);

							if (hasOpenLiveRichMedia)
							{
								emotes = new List<IChatEmote>();
								emoteIdentities = new HashSet<string>(StringComparer.Ordinal);
								richImages = new List<BilibiliRichImage>();
								richImageIds = new HashSet<string>(StringComparer.Ordinal);
								TryExtractOpenLiveRichMedia(dmDataNode, text, userId, emotes, emoteIdentities, richImages, richImageIds);
							}
						}
						break;

					case "SEND_GIFT":
					case "LIVE_OPEN_PLATFORM_SEND_GIFT":
						if (root.TryGetProperty("data", out var giftDataNode) && giftDataNode.ValueKind == JsonValueKind.Object)
						{
							userId = GetPropertyString(giftDataNode, "uid", "0");
							userName = GetPropertyString(giftDataNode, "uname", "Bilibili");
							var giftName = GetPropertyString(giftDataNode, "giftName", GetPropertyString(giftDataNode, "gift_name", "礼物"));
							var giftNum = GetPropertyString(giftDataNode, "num", GetPropertyString(giftDataNode, "gift_num", "1"));
							text = $"{userName} 赠送 {giftNum}x{giftName}";
						}
						break;

					case "SUPER_CHAT_MESSAGE":
					case "LIVE_OPEN_PLATFORM_SUPER_CHAT":
						if (root.TryGetProperty("data", out var scDataNode) && scDataNode.ValueKind == JsonValueKind.Object)
						{
							userId = GetPropertyString(scDataNode, "uid", "0");
							userName = GetPropertyString(scDataNode, "uname", "Bilibili");
							text = GetPropertyString(scDataNode, "message", "SuperChat");
						}
						break;

					case "INTERACT_WORD":
						if (root.TryGetProperty("data", out var interactDataNode) && interactDataNode.ValueKind == JsonValueKind.Object)
						{
							userId = GetPropertyString(interactDataNode, "uid", "0");
							userName = GetPropertyString(interactDataNode, "uname", "Bilibili");
							text = $"欢迎 {userName} 进入直播间";
						}
						break;

						case "LIKE_INFO_V3_CLICK":
						case "LIVE_OPEN_PLATFORM_LIKE":
						if (root.TryGetProperty("data", out var likeDataNode) && likeDataNode.ValueKind == JsonValueKind.Object)
						{
							userId = GetPropertyString(likeDataNode, "uid", "0");
							userName = GetPropertyString(likeDataNode, "uname", "Bilibili");
							text = GetPropertyString(likeDataNode, "like_text", $"{userName} 点赞了直播间");
						}
						break;
					}
				}

				if (string.IsNullOrWhiteSpace(text))
				{
					if (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
					{
						text = GetPropertyString(dataNode, "message");
						if (string.IsNullOrWhiteSpace(text))
						{
							text = GetPropertyString(dataNode, "msg");
						}
					}

					if (string.IsNullOrWhiteSpace(text))
					{
						return null;
					}
				}

				if (string.IsNullOrWhiteSpace(senderDisplayName))
				{
					senderDisplayName = userName;
				}

				if (resolvedCommand.StartsWith("DANMU_MSG", StringComparison.OrdinalIgnoreCase))
				{
					#if BADGE_DEBUG
					Log.Information("[TASK14DBG][CATCORE] " + $"stage=displayname-before-apply uid={userId} uname={Task14ToLogValue(userName)} before={Task14ToLogValue(senderDisplayName)}");
					#endif
					var beforeMedalApply = senderDisplayName;
					senderDisplayName = ApplyDanmuDisplayNamePrefix(senderDisplayName, danmuMedalPrefix);
					#if BADGE_DEBUG
					Log.Information("[TASK14DBG][CATCORE] " + $"stage=displayname-after-medal uid={userId} hasMedalPrefix={!string.IsNullOrWhiteSpace(danmuMedalPrefix)} before={Task14ToLogValue(beforeMedalApply)} after={Task14ToLogValue(senderDisplayName)}");
					#endif

					var hasHonorTagImage = richImages != null
						&& richImages.Any(image => string.Equals(image.Kind, "tag", StringComparison.Ordinal));
					if (!hasHonorTagImage)
					{
						var beforeHonorApply = senderDisplayName;
						senderDisplayName = ApplyDanmuDisplayNamePrefix(senderDisplayName, danmuHonorPrefix);
						#if BADGE_DEBUG
						Log.Information("[TASK14DBG][CATCORE] " + $"stage=displayname-after-honor uid={userId} hasHonorPrefix={!string.IsNullOrWhiteSpace(danmuHonorPrefix)} before={Task14ToLogValue(beforeHonorApply)} after={Task14ToLogValue(senderDisplayName)}");
						#endif
					}
					else
					{
						#if BADGE_DEBUG
						Log.Information("[TASK14DBG][CATCORE] " + $"stage=displayname-honor-skipped uid={userId} reason=honor_tag_image_present hasHonorPrefix={!string.IsNullOrWhiteSpace(danmuHonorPrefix)} current={Task14ToLogValue(senderDisplayName)}");
						#endif
					}
				}

				var sender = new BilibiliUser(userId, userName, senderDisplayName, color, isBroadcaster, isModerator, senderBadges);
				#if BADGE_DEBUG
				_logger.Information("[BADGE_SVC] after-assign uid={UserId} badgeCount={BadgeCount}", sender.Id, sender.Badges.Count);
				#endif
				string messageId;
				if (root.TryGetProperty("msg_id", out var msgIdNode))
				{
					messageId = GetElementString(msgIdNode, string.Empty);
					if (string.IsNullOrWhiteSpace(messageId))
					{
						messageId = Guid.NewGuid().ToString("N");
					}
				}
				else
				{
					messageId = Guid.NewGuid().ToString("N");
				}

				var emoteCount = emotes?.Count ?? 0;
				var richImageCount = richImages?.Count ?? 0;
				if (emoteCount > 0 || richImageCount > 0)
				{
					_logger.Debug("BILI_RICH_MEDIA_PARSED cmd={Command} user={User} uid={UserId} emotes={EmoteCount} images={ImageCount}", resolvedCommand, userName, userId, emoteCount, richImageCount);
				}

				ReadOnlyCollection<IChatEmote>? emoteCollection = null;
				if (emotes != null && emotes.Count > 0)
				{
					emotes.Sort((left, right) => right.StartIndex.CompareTo(left.StartIndex));
					emoteCollection = new ReadOnlyCollection<IChatEmote>(emotes);
				}

				IReadOnlyDictionary<string, string>? metadata = null;
				if (emoteCount > 0 || richImageCount > 0)
				{
					var dictionary = new Dictionary<string, string>(StringComparer.Ordinal)
					{
						["cmd"] = resolvedCommand
					};

					if (richImages != null && richImages.Count > 0)
					{
						AddRichImageMetadata(dictionary, richImages);
					}

					if (emoteCount > 0)
					{
						dictionary["bili.emote.count"] = emoteCount.ToString(CultureInfo.InvariantCulture);
					}

					metadata = dictionary;
				}

				return new BilibiliMessage(messageId, false, false, false, text, sender, channel, emoteCollection, metadata);
			}
			catch
			{
				return null;
			}
		}

		private ReadOnlyCollection<IChatBadge>? BuildDanmuBadges(JsonElement infoNode, bool showBadge, string userId, string userName)
		{
			try
			{
				return BuildDanmuBadgesAsync(infoNode, showBadge, userId, userName).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "[BADGE_SVC] build-badge-sync-failed uid={UserId} user={UserName}", userId, userName);
				return null;
			}
		}

		private async Task<ReadOnlyCollection<IChatBadge>?> BuildDanmuBadgesAsync(JsonElement infoNode, bool showBadge, string userId, string userName)
		{
			if (!showBadge)
			{
				return null;
			}

			if (!TryGetArrayElement(infoNode, 3, out var medalNode) || medalNode.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			if (!TryGetArrayElement(medalNode, 0, out var medalLevelNode)
				|| !TryGetArrayElement(medalNode, 1, out var medalNameNode))
			{
				return null;
			}

			var medalName = GetElementString(medalNameNode, string.Empty);
			var normalizedMedalName = medalName.Replace("<", string.Empty).Replace(">", string.Empty).Trim();
			var medalLevel = GetElementInt(medalLevelNode, 0);
			if (string.IsNullOrWhiteSpace(normalizedMedalName) || medalLevel <= 0)
			{
				return null;
			}

			var guardLevel = 0;
			if (TryGetArrayElement(medalNode, 10, out var guardNode))
			{
				guardLevel = Math.Max(0, GetElementInt(guardNode, 0));
			}

			var badge = new BilibiliChatBadge
			{
				Name = normalizedMedalName,
				Level = medalLevel,
				Guard = guardLevel
			};
			badge.setMedalColorByLevel(medalLevel, guardLevel);

			#if BADGE_DEBUG
			_logger.Information("[BADGE_SVC] before-create uid={UserId} user={UserName} medal={Medal} level={Level} guard={Guard}", userId, userName, normalizedMedalName, medalLevel, guardLevel);
			#endif

			await badge.genImage().ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(badge.Uri))
			{
				return null;
			}

			return new ReadOnlyCollection<IChatBadge>(new List<IChatBadge> { badge });
		}

		private void TryExtractDanmuRichMedia(JsonElement root, JsonElement infoNode, string text, string userId, string userName, List<IChatEmote> emotes,
			HashSet<string> emoteIdentities,
			List<BilibiliRichImage> richImages, HashSet<string> richImageIds)
		{
			var hasInfo0 = TryGetArrayElement(infoNode, 0, out var info0Node) && info0Node.ValueKind == JsonValueKind.Array;
			if (hasInfo0)
			{
				if (TryGetArrayElement(info0Node, 13, out var singleEmoteNode) && singleEmoteNode.ValueKind == JsonValueKind.Object)
				{
					var emoteUrl = GetPropertyString(singleEmoteNode, "url");
					var emoteName = string.IsNullOrWhiteSpace(text) ? GetPropertyString(singleEmoteNode, "emoji") : text;
					if (string.IsNullOrWhiteSpace(emoteName))
					{
						emoteName = GetPropertyString(singleEmoteNode, "emoticon_unique", "[BILI_EMOTE]");
					}

					var emoteSeed = GetPropertyString(singleEmoteNode, "emoticon_id", GetPropertyString(singleEmoteNode, "emoticon_unique", emoteName));
					AddInlineEmoteIfPresent(text, emoteName, emoteUrl, emoteSeed, emotes, emoteIdentities);
				}

				JsonElement extraJsonNode;
				var hasExtraJson = TryExtractExtraNode(info0Node, out extraJsonNode);
				if (hasExtraJson)
				{
					AppendExtraEmotes(extraJsonNode, text, emotes, emoteIdentities);
					TryExtractRichImagesFromNode(extraJsonNode, userId, richImages, richImageIds);
				}

				if (TryGetArrayElement(info0Node, 15, out var richNode) && richNode.ValueKind == JsonValueKind.Object)
				{
					TryExtractRichImagesFromNode(richNode, userId, richImages, richImageIds);
				}
			}

			if (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
			{
				TryExtractRichImagesFromNode(dataNode, userId, richImages, richImageIds);
			}

			const int minimumRichImageCountForDanmu = 4;
			if (richImages.Count < minimumRichImageCountForDanmu)
			{
				const int fallbackMaxDepth = 6;
				TryExtractDanmuRichImagesWithFallback(infoNode, userId, richImages, richImageIds, 0, fallbackMaxDepth, minimumRichImageCountForDanmu);
				if (richImages.Count < minimumRichImageCountForDanmu)
				{
					TryExtractDanmuRichImagesWithFallback(root, userId, richImages, richImageIds, 0, fallbackMaxDepth, minimumRichImageCountForDanmu);
				}
			}

			TryAddDanmuRoleFallbackImage(root, infoNode, userId, richImages, richImageIds);
			TryAddDanmuAvatarFallbackImage(root, infoNode, userId, richImages, richImageIds);
		}

		private bool HasDanmuRoleSemanticFallback(JsonElement root, JsonElement infoNode, string userId)
		{
			return ResolveDanmuGuardLevel(infoNode) > 0
				|| ResolveDanmuIsBroadcaster(root, infoNode, userId)
				|| ResolveDanmuHasFansMedal(infoNode)
				|| ResolveDanmuHasHonorLevel(infoNode);
		}

		private void TryAddDanmuRoleFallbackImage(JsonElement root, JsonElement infoNode, string userId,
			List<BilibiliRichImage> richImages, HashSet<string> richImageIds)
		{
			if (ResolveDanmuIsBroadcaster(root, infoNode, userId))
			{
				var broadcasterUrl = BuildInternalStaticImageUrl("BilibiliLiveBroadcaster.png");
				var broadcasterId = CreateBilibiliImageId("badge", $"{userId}_broadcaster", broadcasterUrl);
				TryAddRichImage(richImages, richImageIds, new BilibiliRichImage(broadcasterId, broadcasterUrl, false, "badge", 110));
			}

			var badgeAdded = false;

			if (!richImages.Any(image => string.Equals(image.Kind, "badge", StringComparison.Ordinal)))
			{
				if (!badgeAdded)
				{
					var guardLevel = ResolveDanmuGuardLevel(infoNode);
					var guardIconFileName = guardLevel switch
					{
						1 => "BilibiliLiveGuard1.png",
						2 => "BilibiliLiveGuard2.png",
						3 => "BilibiliLiveGuard3.png",
						_ => string.Empty
					};

					if (!string.IsNullOrWhiteSpace(guardIconFileName))
					{
						var guardUrl = BuildInternalStaticImageUrl(guardIconFileName);
						var guardId = CreateBilibiliImageId("badge", $"{userId}_guard_{guardLevel.ToString(CultureInfo.InvariantCulture)}", guardUrl);
						TryAddRichImage(richImages, richImageIds, new BilibiliRichImage(guardId, guardUrl, false, "badge", 110));
						badgeAdded = true;
					}
				}

				// Keep fans medal fallback as colored text prefix to avoid fake-looking placeholder badge blocks.
			}

			if (!richImages.Any(image => string.Equals(image.Kind, "tag", StringComparison.Ordinal)))
			{
				var honorLevel = ResolveDanmuHonorLevel(infoNode);
				if (honorLevel > 0)
				{
					var honorUrl = ResolveDanmuHonorLevelFallbackUrl(honorLevel);
					if (!string.IsNullOrWhiteSpace(honorUrl))
					{
						var honorId = CreateBilibiliImageId("tag", $"{userId}_honor", honorUrl);
						TryAddRichImage(richImages, richImageIds, new BilibiliRichImage(honorId, honorUrl, false, "tag", 110));
					}
				}
			}
		}

		private void TryAddDanmuAvatarFallbackImage(JsonElement root, JsonElement infoNode, string userId,
			List<BilibiliRichImage> richImages, HashSet<string> richImageIds)
		{
			if (richImages.Any(image => string.Equals(image.Kind, "avatar", StringComparison.Ordinal)))
			{
				return;
			}

			if (!TryFindDanmuAvatarUrl(infoNode, 0, 6, out var avatarUrl)
				&& !TryFindDanmuAvatarUrl(root, 0, 6, out avatarUrl))
			{
				return;
			}

			var avatarId = CreateBilibiliImageId("avatar", userId, avatarUrl);
			TryAddRichImage(richImages, richImageIds, new BilibiliRichImage(avatarId, avatarUrl, false, "avatar", 110));
		}

		private static bool TryFindDanmuAvatarUrl(JsonElement node, int depth, int maxDepth, out string avatarUrl)
		{
			avatarUrl = string.Empty;
			if (depth > maxDepth)
			{
				return false;
			}

			if (node.ValueKind == JsonValueKind.Object)
			{
				avatarUrl = FirstNonEmpty(
					GetPropertyString(node, "uface"),
					GetPropertyString(node, "face"),
					GetNestedPropertyString(node, "user", "base", "face"),
					GetNestedPropertyString(node, "user_info", "face"));

				if (!string.IsNullOrWhiteSpace(avatarUrl))
				{
					return true;
				}

				if (depth >= maxDepth)
				{
					return false;
				}

				foreach (var property in node.EnumerateObject())
				{
					if (TryFindDanmuAvatarUrl(property.Value, depth + 1, maxDepth, out avatarUrl))
					{
						return true;
					}
				}

				return false;
			}

			if (node.ValueKind != JsonValueKind.Array || depth >= maxDepth)
			{
				return false;
			}

			foreach (var item in node.EnumerateArray())
			{
				if (TryFindDanmuAvatarUrl(item, depth + 1, maxDepth, out avatarUrl))
				{
					return true;
				}
			}

			return false;
		}

		private string ResolveDanmuHonorLevelFallbackUrl(int honorLevel)
		{
			if (TryGetDanmuWealthLevelIconUrl(honorLevel, out var honorUrl))
			{
				return honorUrl;
			}

			return BuildInternalStaticImageUrl("BilibiliLiveHonorLevel.png");
		}

		private bool TryGetDanmuWealthLevelIconUrl(int level, out string iconUrl)
		{
			iconUrl = string.Empty;
			if (level <= 0)
			{
				return false;
			}

			lock (_danmuWealthIconCacheLock)
			{
				if (!_danmuWealthLevelIconUrls.TryGetValue(level, out var cachedUrl) || string.IsNullOrWhiteSpace(cachedUrl))
				{
					return false;
				}

				iconUrl = cachedUrl;
				return true;
			}
		}

		private static bool ResolveDanmuHasFansMedal(JsonElement infoNode)
		{
			if (TryGetArrayElement(infoNode, 3, out var medalNode) && medalNode.ValueKind == JsonValueKind.Array)
			{
				var medalName = GetArrayString(medalNode, 1);
				var medalLevelRaw = GetArrayString(medalNode, 0);
				if (!string.IsNullOrWhiteSpace(medalName)
					&& int.TryParse(medalLevelRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var medalLevel)
					&& medalLevel > 0)
				{
					return true;
				}
			}

			return false;
		}

		private static bool ResolveDanmuHasHonorLevel(JsonElement infoNode)
		{
			return ResolveDanmuHonorLevel(infoNode) > 0;
		}

		private static int ResolveDanmuHonorLevel(JsonElement infoNode)
		{
			if (TryGetArrayElement(infoNode, 16, out var honorNode) && honorNode.ValueKind == JsonValueKind.Array)
			{
				if (TryGetArrayElement(honorNode, 0, out var levelNode))
				{
					return GetElementInt(levelNode, 0);
				}
			}

			return 0;
		}

		private static string ResolveDanmuHonorDisplayPrefix(JsonElement infoNode)
		{
			return string.Empty;
		}

		private static string ResolveDanmuMedalDisplayPrefix(JsonElement infoNode, JsonElement root)
		{
			var infoPrefix = ResolveDanmuMedalDisplayPrefix(infoNode);
			if (!string.IsNullOrWhiteSpace(infoPrefix))
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] " + $"stage=medal-prefix-root branch=info infoEmpty=false dataFallbackEntered=false hasMedalPrefix={!string.IsNullOrWhiteSpace(infoPrefix)}");
				#endif
				return infoPrefix;
			}

			if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] stage=medal-prefix-root branch=info-empty dataFallbackEntered=false reason=data_missing_or_invalid");
				#endif
				return string.Empty;
			}

			#if BADGE_DEBUG
			Log.Information("[TASK14DBG][CATCORE] stage=medal-prefix-root branch=info-empty dataFallbackEntered=true");
			#endif
			var dataPrefix = ResolveDanmuMedalDisplayPrefixFromData(dataNode);
			#if BADGE_DEBUG
			Log.Information("[TASK14DBG][CATCORE] " + $"stage=medal-prefix-root branch=data-fallback hasMedalPrefix={!string.IsNullOrWhiteSpace(dataPrefix)}");
			#endif
			return dataPrefix;
		}

		private static string ResolveDanmuMedalDisplayPrefix(JsonElement infoNode)
		{
			if (!TryGetArrayElement(infoNode, 3, out var medalNode) || medalNode.ValueKind != JsonValueKind.Array)
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] stage=medal-prefix-info result=empty reason=info[3]_missing_or_not_array");
				#endif
				return string.Empty;
			}

			if (!TryGetArrayElement(medalNode, 0, out var medalLevelNode)
				|| !TryGetArrayElement(medalNode, 1, out var medalNameNode))
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] stage=medal-prefix-info result=empty reason=medal_level_or_name_node_missing");
				#endif
				return string.Empty;
			}

			var medalName = GetElementString(medalNameNode, string.Empty);
			var medalLevelRaw = GetElementString(medalLevelNode, string.Empty);
			if (string.IsNullOrWhiteSpace(medalName)
				|| !int.TryParse(medalLevelRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var medalLevel)
				|| medalLevel <= 0)
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] " + $"stage=medal-prefix-info result=empty reason=invalid_name_or_level medalNameEmpty={string.IsNullOrWhiteSpace(medalName)} medalLevelRaw={Task14ToLogValue(medalLevelRaw)}");
				#endif
				return string.Empty;
			}

			var normalizedMedalName = medalName.Replace("<", string.Empty).Replace(">", string.Empty).Trim();
			if (string.IsNullOrWhiteSpace(normalizedMedalName))
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] stage=medal-prefix-info result=empty reason=normalized_name_empty");
				#endif
				return string.Empty;
			}

			if (normalizedMedalName.Length > 10)
			{
				normalizedMedalName = normalizedMedalName.Substring(0, 10) + "…";
			}

			var safeMedalName = EscapeTmpText(normalizedMedalName);
			var prefix = $"<color=#FFFFFF>{safeMedalName} {medalLevel.ToString(CultureInfo.InvariantCulture)}</color> ";
			#if BADGE_DEBUG
			Log.Information("[TASK14DBG][CATCORE] " + $"stage=medal-prefix-info result=ok medalName={Task14ToLogValue(normalizedMedalName)} medalLevel={medalLevel}");
			#endif
			return prefix;
		}

		private static string ResolveDanmuMedalDisplayPrefixFromData(JsonElement dataNode)
		{
			if (dataNode.ValueKind != JsonValueKind.Object)
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] stage=medal-prefix-data result=empty reason=data_not_object");
				#endif
				return string.Empty;
			}

			if (!TryResolveDanmuMedalFromDataNode(dataNode, "fans_medal", out var medalName, out var medalLevel)
				&& !TryResolveDanmuMedalFromDataNode(dataNode, "medal", out medalName, out medalLevel)
				&& !TryResolveDanmuMedalFromDataNode(dataNode, "medal_info", out medalName, out medalLevel))
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] stage=medal-prefix-data result=empty reason=fallback_nodes_not_found_or_invalid");
				#endif
				return string.Empty;
			}

			if (string.IsNullOrWhiteSpace(medalName) || medalLevel <= 0)
			{
				#if BADGE_DEBUG
				Log.Information("[TASK14DBG][CATCORE] " + $"stage=medal-prefix-data result=empty reason=invalid_name_or_level medalNameEmpty={string.IsNullOrWhiteSpace(medalName)} medalLevel={medalLevel}");
				#endif
				return string.Empty;
			}

			var safeMedalName = EscapeTmpText(medalName);
			var prefix = $"<color=#FFFFFF>{safeMedalName} {medalLevel.ToString(CultureInfo.InvariantCulture)}</color> ";
			#if BADGE_DEBUG
			Log.Information("[TASK14DBG][CATCORE] " + $"stage=medal-prefix-data result=ok medalName={Task14ToLogValue(medalName)} medalLevel={medalLevel}");
			#endif
			return prefix;
		}

		private static string Task14ToLogValue(string value, int maxLen = 64)
		{
			if (string.IsNullOrEmpty(value))
			{
				return string.Empty;
			}

			var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
			if (normalized.Length <= maxLen)
			{
				return normalized;
			}

			return normalized.Substring(0, maxLen);
		}

		private static bool TryResolveDanmuMedalFromDataNode(JsonElement dataNode, string propertyName, out string medalName, out int medalLevel)
		{
			medalName = string.Empty;
			medalLevel = 0;

			if (!dataNode.TryGetProperty(propertyName, out var medalNode) || medalNode.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			medalName = FirstNonEmpty(
				GetPropertyString(medalNode, "medal_name"),
				GetPropertyString(medalNode, "name"),
				GetPropertyString(medalNode, "medalName"));

			medalLevel = FirstPositiveInt(
				GetPropertyInt(medalNode, "medal_level", 0),
				GetPropertyInt(medalNode, "level", 0),
				GetPropertyInt(medalNode, "medalLevel", 0));

			return !string.IsNullOrWhiteSpace(medalName) && medalLevel > 0;
		}

		private static int FirstPositiveInt(params int[] values)
		{
			for (var i = 0; i < values.Length; i++)
			{
				if (values[i] > 0)
				{
					return values[i];
				}
			}

			return 0;
		}

		private static string ResolveDanmuMedalHtmlColor(JsonElement medalNode, int index, string fallback)
		{
			if (!TryGetArrayElement(medalNode, index, out var colorNode))
			{
				return fallback;
			}

			if (TryConvertToHtmlColor(colorNode, out var htmlColor))
			{
				return htmlColor;
			}

			return fallback;
		}

		private static bool TryConvertToHtmlColor(JsonElement colorNode, out string htmlColor)
		{
			htmlColor = string.Empty;
			var raw = GetElementString(colorNode, string.Empty)?.Trim() ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(raw) && TryNormalizeHtmlColor(raw, out htmlColor))
			{
				return true;
			}

			var rgbInt = GetElementInt(colorNode, -1);
			if (rgbInt < 0)
			{
				return false;
			}

			htmlColor = $"#{(rgbInt & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture)}";
			return true;
		}

		private static bool TryNormalizeHtmlColor(string raw, out string htmlColor)
		{
			htmlColor = string.Empty;
			if (string.IsNullOrWhiteSpace(raw))
			{
				return false;
			}

			var candidate = raw;
			if (candidate.StartsWith("#", StringComparison.Ordinal))
			{
				candidate = candidate.Substring(1);
			}
			else if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				candidate = candidate.Substring(2);
			}

			if (candidate.Length == 3)
			{
				candidate = string.Concat(candidate.Select(ch => new string(ch, 2)));
			}

			if (candidate.Length == 8)
			{
				candidate = candidate.Substring(2, 6);
			}

			if (candidate.Length == 6
				&& int.TryParse(candidate, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
			{
				htmlColor = $"#{candidate.ToUpperInvariant()}";
				return true;
			}

			if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rgbInt) && rgbInt >= 0)
			{
				htmlColor = $"#{(rgbInt & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture)}";
				return true;
			}

			return false;
		}

		private static string ToTmpMarkColor(string htmlColor, string alpha)
		{
			if (string.IsNullOrWhiteSpace(htmlColor))
			{
				return "#1F2D42CC";
			}

			var normalized = htmlColor.StartsWith("#", StringComparison.Ordinal) ? htmlColor.Substring(1) : htmlColor;
			if (normalized.Length == 8)
			{
				return $"#{normalized.ToUpperInvariant()}";
			}

			if (normalized.Length != 6)
			{
				return "#1F2D42CC";
			}

			return $"#{normalized.ToUpperInvariant()}{alpha}";
		}

		private static string EscapeTmpText(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return string.Empty;
			}

			return value
				.Replace("&", "&amp;")
				.Replace("<", "&lt;")
				.Replace(">", "&gt;");
		}

		private static string ApplyDanmuDisplayNamePrefix(string displayName, string prefix)
		{
			if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(displayName))
			{
				return displayName;
			}

			return displayName.StartsWith(prefix, StringComparison.Ordinal) ? displayName : $"{prefix}{displayName}";
		}

		private bool ResolveDanmuIsBroadcaster(JsonElement root, JsonElement infoNode, string userId)
		{
			var senderUid = ParsePositiveInt64(userId);
			if (senderUid <= 0)
			{
				return false;
			}

			if (_activeBroadcasterUserId > 0)
			{
				return senderUid == _activeBroadcasterUserId;
			}

			var ownerUid = ResolveDanmuOwnerUserId(root, infoNode);
			if (ownerUid > 0)
			{
				return senderUid == ownerUid;
			}

			return false;
		}

		private static int ResolveDanmuGuardLevel(JsonElement infoNode)
		{
			if (TryGetArrayElement(infoNode, 7, out var info7Node))
			{
				var guardLevel = GetElementInt(info7Node, 0);
				if (guardLevel > 0)
				{
					return guardLevel;
				}
			}

			if (TryGetArrayElement(infoNode, 3, out var medalNode)
				&& medalNode.ValueKind == JsonValueKind.Array
				&& TryGetArrayElement(medalNode, 10, out var medalGuardLevelNode))
			{
				var guardLevel = GetElementInt(medalGuardLevelNode, 0);
				if (guardLevel > 0)
				{
					return guardLevel;
				}
			}

			return 0;
		}

		private static long ResolveDanmuOwnerUserId(JsonElement root, JsonElement infoNode)
		{
			if (root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
			{
				var ownerUid = FirstPositiveLong(
					GetPropertyString(dataNode, "owner_uid"),
					GetPropertyString(dataNode, "room_owner_uid"),
					GetPropertyString(dataNode, "anchor_uid"),
					GetPropertyString(dataNode, "host_uid"),
					GetNestedPropertyString(dataNode, "anchor_info", "uid"),
					GetNestedPropertyString(dataNode, "room_owner", "uid"),
					GetNestedPropertyString(dataNode, "room_info", "uid"));

				if (ownerUid > 0)
				{
					return ownerUid;
				}
			}

			if (TryGetArrayElement(infoNode, 3, out var medalNode)
				&& medalNode.ValueKind == JsonValueKind.Array
				&& TryGetArrayElement(medalNode, 12, out var targetIdNode))
			{
				var targetUid = GetElementLong(targetIdNode, 0);
				if (targetUid > 0)
				{
					return targetUid;
				}
			}

			return 0;
		}

		private static long FirstPositiveLong(params string[] values)
		{
			for (var i = 0; i < values.Length; i++)
			{
				var parsed = ParsePositiveInt64(values[i]);
				if (parsed > 0)
				{
					return parsed;
				}
			}

			return 0;
		}

		private static long ParsePositiveInt64(string value)
		{
			if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
			{
				return parsed;
			}

			return 0;
		}

		private static long GetElementLong(JsonElement node, long fallback)
		{
			switch (node.ValueKind)
			{
				case JsonValueKind.Number:
					if (node.TryGetInt64(out var longValue))
					{
						return longValue;
					}

					if (node.TryGetInt32(out var intValue))
					{
						return intValue;
					}

					return fallback;

				case JsonValueKind.String:
					var raw = node.GetString();
					return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

				default:
					return fallback;
			}
		}

		private static string BuildInternalStaticImageUrl(string fileName)
		{
			return $"{CatCore.ConstantsBase.InternalApiServerUri}Statics/Images/{fileName}";
		}

		private void TryExtractDanmuRichImagesWithFallback(JsonElement node, string userId, List<BilibiliRichImage> richImages,
			HashSet<string> richImageIds, int depth, int maxDepth, int minimumRichImageCount)
		{
			if (depth > maxDepth || richImages.Count >= minimumRichImageCount)
			{
				return;
			}

			if (node.ValueKind == JsonValueKind.Object)
			{
				TryExtractRichImagesFromNode(node, userId, richImages, richImageIds);
				if (depth >= maxDepth || richImages.Count >= minimumRichImageCount)
				{
					return;
				}

				foreach (var property in node.EnumerateObject())
				{
					TryExtractDanmuRichImagesWithFallback(property.Value, userId, richImages, richImageIds, depth + 1, maxDepth, minimumRichImageCount);
					if (richImages.Count >= minimumRichImageCount)
					{
						return;
					}
				}

				return;
			}

			if (node.ValueKind != JsonValueKind.Array || depth >= maxDepth)
			{
				return;
			}

			foreach (var item in node.EnumerateArray())
			{
				TryExtractDanmuRichImagesWithFallback(item, userId, richImages, richImageIds, depth + 1, maxDepth, minimumRichImageCount);
				if (richImages.Count >= minimumRichImageCount)
				{
					return;
				}
			}
		}

		private void TryExtractOpenLiveRichMedia(JsonElement dataNode, string text, string userId, List<IChatEmote> emotes,
			HashSet<string> emoteIdentities,
			List<BilibiliRichImage> richImages, HashSet<string> richImageIds)
		{
			var emojiUrl = GetPropertyString(dataNode, "emoji_img_url");
			if (string.IsNullOrWhiteSpace(emojiUrl))
			{
				emojiUrl = GetPropertyString(dataNode, "emoji_img");
			}

			if (!string.IsNullOrWhiteSpace(emojiUrl) && !string.IsNullOrWhiteSpace(text))
			{
				AddInlineEmoteIfPresent(text, text, emojiUrl, "openlive_emoji", emotes, emoteIdentities);
			}

			TryExtractRichImagesFromNode(dataNode, userId, richImages, richImageIds);
		}

		private static bool TryExtractExtraNode(JsonElement info0Node, out JsonElement extraJsonNode)
		{
			extraJsonNode = default;
			if (!TryGetArrayElement(info0Node, 15, out var metadataNode) || metadataNode.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			if (!metadataNode.TryGetProperty("extra", out var extraNode))
			{
				return false;
			}

			if (extraNode.ValueKind == JsonValueKind.Object)
			{
				extraJsonNode = extraNode;
				return true;
			}

			if (extraNode.ValueKind != JsonValueKind.String)
			{
				return false;
			}

			var rawExtra = extraNode.GetString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(rawExtra))
			{
				return false;
			}

			try
			{
				extraJsonNode = JsonSerializer.Deserialize<JsonElement>(rawExtra);
				return extraJsonNode.ValueKind == JsonValueKind.Object;
			}
			catch
			{
				return false;
			}
		}

		private void AppendExtraEmotes(JsonElement extraNode, string text, List<IChatEmote> emotes, HashSet<string> emoteIdentities)
		{
			if (string.IsNullOrWhiteSpace(text)
				|| !extraNode.TryGetProperty("emots", out var emotsNode))
			{
				return;
			}

			if (emotsNode.ValueKind == JsonValueKind.Object)
			{
				foreach (var emoteProperty in emotsNode.EnumerateObject())
				{
					if (!TryExtractExtraEmote(emoteProperty.Value, emoteProperty.Name, out var emojiText, out var emoteUrl, out var emoteSeed))
					{
						continue;
					}

					AddInlineEmoteIfPresent(text, emojiText, emoteUrl, emoteSeed, emotes, emoteIdentities);
				}
				return;
			}

			if (emotsNode.ValueKind != JsonValueKind.Array)
			{
				return;
			}

			var emoteIndex = 0;
			foreach (var emoteNode in emotsNode.EnumerateArray())
			{
				var fallbackKey = emoteIndex.ToString(CultureInfo.InvariantCulture);
				if (TryExtractExtraEmote(emoteNode, fallbackKey, out var emojiText, out var emoteUrl, out var emoteSeed))
				{
					AddInlineEmoteIfPresent(text, emojiText, emoteUrl, emoteSeed, emotes, emoteIdentities);
				}

				emoteIndex++;
			}
		}

		private static bool TryExtractExtraEmote(JsonElement emoteNode, string fallbackKey, out string emojiText, out string emoteUrl, out string emoteSeed)
		{
			emojiText = string.Empty;
			emoteUrl = string.Empty;
			emoteSeed = string.Empty;

			if (emoteNode.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			emojiText = GetPropertyString(emoteNode, "emoji", fallbackKey);
			emoteUrl = GetPropertyString(emoteNode, "url");
			if (string.IsNullOrWhiteSpace(emojiText) || string.IsNullOrWhiteSpace(emoteUrl))
			{
				return false;
			}

			emoteSeed = GetPropertyString(emoteNode, "emoticon_id", GetPropertyString(emoteNode, "emoticon_unique", fallbackKey));
			return true;
		}

		private void TryExtractRichImagesFromNode(JsonElement node, string userId, List<BilibiliRichImage> richImages, HashSet<string> richImageIds)
		{
			if (node.ValueKind != JsonValueKind.Object)
			{
				return;
			}

			var avatarUrl = FirstNonEmpty(
				GetPropertyString(node, "uface"),
				GetPropertyString(node, "face"),
				GetNestedPropertyString(node, "user", "base", "face"),
				GetNestedPropertyString(node, "user_info", "face"));

			if (!string.IsNullOrWhiteSpace(avatarUrl))
			{
				var avatarId = CreateBilibiliImageId("avatar", userId, avatarUrl);
				TryAddRichImage(richImages, richImageIds, new BilibiliRichImage(avatarId, avatarUrl, false, "avatar", 110));
			}

			var badgeUrls = new List<string>(14);
			AddNestedPropertyStringValues(node, badgeUrls, "fans_medal", "medal_icon");
			AddNestedPropertyStringValues(node, badgeUrls, "fans_medal", "medal_icon_url");
			AddNestedPropertyStringValues(node, badgeUrls, "fans_medal", "icon");
			AddNestedPropertyStringValues(node, badgeUrls, "user", "medal", "guard_icon");
			AddNestedPropertyStringValues(node, badgeUrls, "medal", "medal_icon");
			AddNestedPropertyStringValues(node, badgeUrls, "medal", "guard_icon");
			AddNestedPropertyStringValues(node, badgeUrls, "medal", "icon");
			AddNestedPropertyStringValues(node, badgeUrls, "medal_info", "medal_icon");
			AddNestedPropertyStringValues(node, badgeUrls, "medal_info", "icon");
			AddNestedPropertyStringValues(node, badgeUrls, "medal_info", "icon_url");
			AddNestedPropertyStringValues(node, badgeUrls, "badge", "icon");
			AddNestedPropertyStringValues(node, badgeUrls, "badge", "icon_url");

			foreach (var badgeUrl in badgeUrls)
			{
				if (string.IsNullOrWhiteSpace(badgeUrl))
				{
					continue;
				}

				var badgeId = CreateBilibiliImageId("badge", userId, badgeUrl);
				TryAddRichImage(richImages, richImageIds, new BilibiliRichImage(badgeId, badgeUrl, false, "badge", 100));
			}

			var tagUrls = new List<string>(10);
			AddNestedPropertyStringValues(node, tagUrls, "icon", "prefix", "resource");
			AddNestedPropertyStringValues(node, tagUrls, "extra", "icon", "prefix", "resource");
			AddNestedPropertyStringValues(node, tagUrls, "title_info", "icon");
			AddNestedPropertyStringValues(node, tagUrls, "title_info", "icon_url");
			AddNestedPropertyStringValues(node, tagUrls, "badge_info", "icon");
			AddNestedPropertyStringValues(node, tagUrls, "badge_info", "icon_url");
			AddNestedPropertyStringValues(node, tagUrls, "title_info", "image");
			AddNestedPropertyStringValues(node, tagUrls, "title_info", "web_pic_url");
			AddNestedPropertyStringValues(node, tagUrls, "nameplate", "image");
			AddNestedPropertyStringValues(node, tagUrls, "wealth_level", "icon");
			AddNestedPropertyStringValues(node, tagUrls, "guard_info", "icon");

			foreach (var tagUrl in tagUrls)
			{
				if (string.IsNullOrWhiteSpace(tagUrl))
				{
					continue;
				}

				var tagId = CreateBilibiliImageId("tag", userId, tagUrl);
				TryAddRichImage(richImages, richImageIds, new BilibiliRichImage(tagId, tagUrl, false, "tag", 95));
			}
		}

		private void TryAddRichImage(List<BilibiliRichImage> richImages, HashSet<string> richImageIds, BilibiliRichImage image)
		{
			var normalizedUrl = NormalizeBilibiliImageUrlWithLog(image.Url, $"richimage:{image.Kind}");

			if (string.IsNullOrWhiteSpace(image.Id)
				|| string.IsNullOrWhiteSpace(normalizedUrl)
				|| !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out _)
				|| !richImageIds.Add(image.Id))
			{
				return;
			}

			richImages.Add(new BilibiliRichImage(image.Id, normalizedUrl, image.Animated, image.Kind, image.ForcedHeight));
		}

		private void AddInlineEmoteIfPresent(string messageText, string emoteName, string emoteUrl, string seed, List<IChatEmote> emotes,
			HashSet<string> emoteIdentities)
		{
			var normalizedEmoteUrl = NormalizeBilibiliImageUrlWithLog(emoteUrl, "emote");

			if (string.IsNullOrWhiteSpace(messageText)
				|| string.IsNullOrWhiteSpace(emoteName)
				|| string.IsNullOrWhiteSpace(normalizedEmoteUrl)
				|| !Uri.TryCreate(normalizedEmoteUrl, UriKind.Absolute, out _))
			{
				return;
			}

			var startIndex = messageText.IndexOf(emoteName, StringComparison.Ordinal);
			if (startIndex < 0)
			{
				return;
			}

			var emoteId = CreateBilibiliImageId("emote", seed, normalizedEmoteUrl);
			var emoteIdentity = $"{emoteId}|{emoteName}";
			if (!emoteIdentities.Add(emoteIdentity))
			{
				return;
			}

			emotes.Add(new BilibiliEmote(emoteId, emoteName, startIndex, normalizedEmoteUrl, IsAnimatedUrl(normalizedEmoteUrl)));
		}

		private string NormalizeBilibiliImageUrlWithLog(string url, string source)
		{
			var normalizedUrl = NormalizeBilibiliImageUrl(url);
			if (!string.Equals(url, normalizedUrl, StringComparison.Ordinal))
			{
				_logger.Debug("BILI_MEDIA_URL_NORMALIZED source={Source} from={RawUrl} to={NormalizedUrl}", source, url, normalizedUrl);
			}

			return normalizedUrl;
		}

		private static void AddRichImageMetadata(Dictionary<string, string> metadata, List<BilibiliRichImage> richImages)
		{
			metadata["bili.richimage.count"] = richImages.Count.ToString(CultureInfo.InvariantCulture);
			for (var i = 0; i < richImages.Count; i++)
			{
				var image = richImages[i];
				metadata[$"bili.richimage.{i}.kind"] = image.Kind;
				metadata[$"bili.richimage.{i}.id"] = image.Id;
				metadata[$"bili.richimage.{i}.url"] = image.Url;
				metadata[$"bili.richimage.{i}.animated"] = image.Animated ? "1" : "0";
				metadata[$"bili.richimage.{i}.height"] = image.ForcedHeight.ToString(CultureInfo.InvariantCulture);
			}
		}

		private static string CreateBilibiliImageId(string kind, string seed, string url)
		{
			var sanitizedSeed = SanitizeForIdentifier(seed);
			if (string.IsNullOrWhiteSpace(sanitizedSeed))
			{
				sanitizedSeed = "unknown";
			}

			return $"Bili_{kind}_{sanitizedSeed}_{GetDeterministicHash(url)}";
		}

		private static string SanitizeForIdentifier(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return string.Empty;
			}

			var builder = new StringBuilder(value.Length);
			foreach (var ch in value)
			{
				if (char.IsLetterOrDigit(ch))
				{
					builder.Append(ch);
				}
				else if (builder.Length > 0 && builder[builder.Length - 1] != '_')
				{
					builder.Append('_');
				}
			}

			return builder.ToString().Trim('_');
		}

		private static string GetDeterministicHash(string value)
		{
			unchecked
			{
				uint hash = 2166136261;
				foreach (var ch in value)
				{
					hash ^= ch;
					hash *= 16777619;
				}

				return hash.ToString("x8", CultureInfo.InvariantCulture);
			}
		}

		private static string GetNestedPropertyString(JsonElement node, params string[] path)
		{
			var current = node;
			for (var i = 0; i < path.Length; i++)
			{
				if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(path[i], out current))
				{
					return string.Empty;
				}
			}

			return GetElementString(current, string.Empty);
		}

		private static string FirstNonEmpty(params string[] values)
		{
			for (var i = 0; i < values.Length; i++)
			{
				if (!string.IsNullOrWhiteSpace(values[i]))
				{
					return values[i];
				}
			}

			return string.Empty;
		}

		private static bool TryGetArrayElement(JsonElement arrayNode, int index, out JsonElement element)
		{
			element = default;
			if (arrayNode.ValueKind != JsonValueKind.Array || index < 0 || index >= arrayNode.GetArrayLength())
			{
				return false;
			}

			element = arrayNode[index];
			return true;
		}

		private static string GetNestedPropertyStringOrFirstArrayString(JsonElement node, params string[] path)
		{
			var current = node;
			for (var i = 0; i < path.Length; i++)
			{
				if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(path[i], out current))
				{
					return string.Empty;
				}
			}

			if (current.ValueKind == JsonValueKind.String)
			{
				return current.GetString() ?? string.Empty;
			}

			if (current.ValueKind != JsonValueKind.Array)
			{
				return string.Empty;
			}

			foreach (var item in current.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.String)
				{
					continue;
				}

				var value = item.GetString();
				if (!string.IsNullOrWhiteSpace(value))
				{
					return value ?? string.Empty;
				}
			}

			return string.Empty;
		}

		private static void AddNestedPropertyStringValues(JsonElement node, List<string> values, params string[] path)
		{
			var current = node;
			for (var i = 0; i < path.Length; i++)
			{
				if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(path[i], out current))
				{
					return;
				}
			}

			if (current.ValueKind == JsonValueKind.String)
			{
				var value = current.GetString();
				if (!string.IsNullOrWhiteSpace(value))
				{
					values.Add(value ?? string.Empty);
				}

				return;
			}

			if (current.ValueKind != JsonValueKind.Array)
			{
				return;
			}

			foreach (var item in current.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.String)
				{
					continue;
				}

				var value = item.GetString();
				if (!string.IsNullOrWhiteSpace(value))
				{
					values.Add(value ?? string.Empty);
				}
			}
		}

		private static string NormalizeBilibiliImageUrl(string url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return string.Empty;
			}

			var normalizedUrl = url.Trim();
			if (normalizedUrl.StartsWith("//", StringComparison.Ordinal))
			{
				normalizedUrl = $"https:{normalizedUrl}";
			}
			else if (normalizedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
			{
				if (!IsLoopbackHttpUrl(normalizedUrl))
				{
					normalizedUrl = $"https://{normalizedUrl.Substring("http://".Length)}";
				}
			}

			if (!normalizedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				&& !normalizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return normalizedUrl;
			}

			if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var parsedUri))
			{
				return normalizedUrl;
			}

			// Bilibili CDN often appends transform suffixes like "@96w_96h_1c_1s.webp".
			// Strip suffix to fall back to source format (png/jpg/gif), which Unity decodes reliably.
			var absolutePath = parsedUri.AbsolutePath;
			var atIndex = absolutePath.IndexOf('@');
			if (atIndex <= 0)
			{
				return normalizedUrl;
			}

			var sourcePath = absolutePath.Substring(0, atIndex);
			if (HasSupportedImageExtension(sourcePath))
			{
				return RebuildBilibiliImageUrl(parsedUri, sourcePath);
			}

			var transformSuffix = absolutePath.Substring(atIndex + 1);
			var noWebpTransform = StripWebpTransformSuffix(transformSuffix);
			if (!string.IsNullOrEmpty(noWebpTransform))
			{
				// Keep transform parameters but remove explicit webp output for Unity decoder compatibility.
				return RebuildBilibiliImageUrl(parsedUri, $"{sourcePath}@{noWebpTransform}");
			}

			return RebuildBilibiliImageUrl(parsedUri, sourcePath);
		}

		private static bool IsLoopbackHttpUrl(string url)
		{
			if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
			{
				return false;
			}

			if (!string.Equals(parsedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			return parsedUri.IsLoopback;
		}

		private static string RebuildBilibiliImageUrl(Uri parsedUri, string path)
		{
			var rebuiltUrl = $"{parsedUri.Scheme}://{parsedUri.Authority}{path}";
			if (!string.IsNullOrEmpty(parsedUri.Query))
			{
				rebuiltUrl += parsedUri.Query;
			}

			return rebuiltUrl;
		}

		private static string StripWebpTransformSuffix(string transformSuffix)
		{
			if (string.IsNullOrWhiteSpace(transformSuffix))
			{
				return string.Empty;
			}

			const string webpSuffix = ".webp";
			if (!transformSuffix.EndsWith(webpSuffix, StringComparison.OrdinalIgnoreCase))
			{
				return string.Empty;
			}

			return transformSuffix.Substring(0, transformSuffix.Length - webpSuffix.Length);
		}

		private static bool HasSupportedImageExtension(string path)
		{
			return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsAnimatedUrl(string url)
		{
			return url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
				|| url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
		}

		private readonly struct BilibiliRichImage
		{
			public string Id { get; }
			public string Url { get; }
			public bool Animated { get; }
			public string Kind { get; }
			public int ForcedHeight { get; }

			public BilibiliRichImage(string id, string url, bool animated, string kind, int forcedHeight)
			{
				Id = id;
				Url = url;
				Animated = animated;
				Kind = kind;
				ForcedHeight = forcedHeight;
			}
		}

		private async Task<bool> PrepareDefaultAuthAsync(BilibiliConfig config)
		{
			var normalizedCookies = BilibiliCookieHelper.StandardizeCookieOrder(config.Cookies);
			if (string.IsNullOrWhiteSpace(normalizedCookies))
			{
				_logger.Warning("Bilibili default auth requires non-empty cookies");
				return false;
			}

			_resolvedUserId = ResolveUserIdFromCookie(normalizedCookies);
			_activeBroadcasterUserId = 0;
			_buvid3 = BilibiliCookieHelper.GetCookieValue(normalizedCookies, "buvid3");
			if (string.IsNullOrWhiteSpace(_buvid3))
			{
				_buvid3 = await GetBuvidFromApiAsync(normalizedCookies).ConfigureAwait(false);
				if (string.IsNullOrWhiteSpace(_buvid3))
				{
					_logger.Warning("Bilibili buvid resolution failed");
					return false;
				}
			}

			await RefreshDanmuWealthIconCacheAsync(normalizedCookies).ConfigureAwait(false);
			await RefreshActiveBroadcasterUserIdAsync(config.RoomId, normalizedCookies).ConfigureAwait(false);

			var (chatToken, endpoint, errorCode) = await GetChatTokenAsync(config.RoomId, normalizedCookies).ConfigureAwait(false);
			_chatToken = chatToken;
			if (string.IsNullOrWhiteSpace(_chatToken))
			{
				if (errorCode.HasValue)
				{
					_logger.Warning("Bilibili chat token resolution failed with code={Code}", errorCode.Value);
				}
				else
				{
					_logger.Warning("Bilibili chat token resolution failed");
				}

				return false;
			}

			if (TryUseReliableDefaultSocketEndpoint(endpoint, out var selectedEndpoint, out var fallbackReason))
			{
				_defaultSocketUri = selectedEndpoint;
			}
			else
			{
				_defaultSocketUri = LIVE_SOCKET_URL;
				_logger.Warning("BILI_ENDPOINT_FALLBACK_TO_443 reason={Reason} endpoint={Endpoint}", fallbackReason, RedactEndpoint(endpoint));
			}
			_logger.Information("Bilibili default token ready: token_len={TokenLength}, endpoint={Endpoint}", _chatToken.Length, RedactEndpoint(_defaultSocketUri));

			return true;
		}

		private async Task RefreshDanmuWealthIconCacheAsync(string cookies)
		{
			if (string.IsNullOrWhiteSpace(cookies))
			{
				return;
			}

			const string wealthApiUrl = "https://api.live.bilibili.com/xlive/general-interface/v1/content/get?key=wealth";
			try
			{
				var (ok, body) = await BilibiliAuthHttpClient.GetAsync(wealthApiUrl, cookies).ConfigureAwait(false);
				if (!ok)
				{
					return;
				}

				using var document = JsonDocument.Parse(body);
				var root = document.RootElement;
				if (!TryGetCode(root, out var code) || code != 0)
				{
					return;
				}

				if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
				{
					return;
				}

				var parsedMap = ExtractDanmuWealthLevelIconMap(dataNode);
				if (parsedMap.Count == 0)
				{
					return;
				}

				lock (_danmuWealthIconCacheLock)
				{
					_danmuWealthLevelIconUrls = parsedMap;
				}
			}
			catch
			{
				// wealth icon cache is best effort and should not block auth flow
			}
		}

		private async Task RefreshActiveBroadcasterUserIdAsync(long roomId, string cookies)
		{
			_activeBroadcasterUserId = 0;
			if (roomId <= 0 || string.IsNullOrWhiteSpace(cookies))
			{
				return;
			}

			try
			{
				var url = ROOM_INFO_API_URL + roomId.ToString(CultureInfo.InvariantCulture);
				var (ok, body) = await BilibiliAuthHttpClient.GetAsync(url, cookies).ConfigureAwait(false);
				if (!ok)
				{
					_logger.Warning("BILI_ROOM_INFO_FETCH_FAILED room={RoomId}", roomId);
					return;
				}

				using var document = JsonDocument.Parse(body);
				var root = document.RootElement;
				if (!TryGetCode(root, out var code) || code != 0)
				{
					_logger.Warning("BILI_ROOM_INFO_INVALID room={RoomId} code={Code}", roomId, code);
					return;
				}

				if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
				{
					return;
				}

				var broadcasterUid = FirstPositiveLong(
					GetNestedPropertyString(dataNode, "room_info", "uid"),
					GetNestedPropertyString(dataNode, "anchor_info", "base_info", "uid"),
					GetNestedPropertyString(dataNode, "anchor_info", "uid"),
					GetPropertyString(dataNode, "uid"),
					GetPropertyString(dataNode, "anchor_uid"),
					GetPropertyString(dataNode, "owner_uid"));

				if (broadcasterUid <= 0)
				{
					_logger.Warning("BILI_ROOM_INFO_BROADCASTER_MISSING room={RoomId}", roomId);
					return;
				}

				_activeBroadcasterUserId = broadcasterUid;
				_logger.Information("BILI_ROOM_INFO_BROADCASTER room={RoomId} uid={UserId}", roomId, broadcasterUid);
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "BILI_ROOM_INFO_FETCH_EXCEPTION room={RoomId}", roomId);
			}
		}

		private static Dictionary<int, string> ExtractDanmuWealthLevelIconMap(JsonElement dataNode)
		{
			var map = new Dictionary<int, string>();
			TryCollectDanmuWealthLevelIcons(dataNode, map, 0, 8);

			if (dataNode.ValueKind == JsonValueKind.Object
				&& dataNode.TryGetProperty("content", out var contentNode))
			{
				if (contentNode.ValueKind == JsonValueKind.Object || contentNode.ValueKind == JsonValueKind.Array)
				{
					TryCollectDanmuWealthLevelIcons(contentNode, map, 0, 8);
				}
				else if (contentNode.ValueKind == JsonValueKind.String)
				{
					var contentText = contentNode.GetString();
					if (!string.IsNullOrWhiteSpace(contentText))
					{
						try
						{
							using var contentDocument = JsonDocument.Parse(contentText!);
							TryCollectDanmuWealthLevelIcons(contentDocument.RootElement, map, 0, 8);
						}
						catch
						{
							// keep original path unchanged when content string is not valid JSON
						}
					}
				}
			}

			return map;
		}

		private static void TryCollectDanmuWealthLevelIcons(JsonElement node, Dictionary<int, string> map, int depth, int maxDepth)
		{
			if (depth > maxDepth)
			{
				return;
			}

			if (node.ValueKind == JsonValueKind.Object)
			{
				var level = FirstPositiveLong(
					GetPropertyString(node, "level"),
					GetPropertyString(node, "wealth_level"),
					GetPropertyString(node, "lv"),
					GetPropertyString(node, "grade"),
					GetPropertyString(node, "id"));

				var iconUrl = FirstNonEmpty(
					GetPropertyString(node, "icon"),
					GetPropertyString(node, "icon_url"),
					GetPropertyString(node, "url"),
					GetPropertyString(node, "image"),
					GetPropertyString(node, "pic"),
					GetPropertyString(node, "web_pic_url"),
					GetNestedPropertyStringOrFirstArrayString(node, "icon", "prefix", "resource"));

				if (level > 0 && level <= int.MaxValue && !string.IsNullOrWhiteSpace(iconUrl))
				{
					var normalizedUrl = NormalizeBilibiliImageUrl(iconUrl);
					if (!string.IsNullOrWhiteSpace(normalizedUrl) && Uri.TryCreate(normalizedUrl, UriKind.Absolute, out _))
					{
						map[(int)level] = normalizedUrl;
					}
				}

				foreach (var property in node.EnumerateObject())
				{
					TryCollectDanmuWealthLevelIcons(property.Value, map, depth + 1, maxDepth);
				}

				return;
			}

			if (node.ValueKind != JsonValueKind.Array)
			{
				return;
			}

			foreach (var item in node.EnumerateArray())
			{
				TryCollectDanmuWealthLevelIcons(item, map, depth + 1, maxDepth);
			}
		}

		private async Task<string> GetBuvidFromApiAsync(string cookies)
		{
			var (ok, body) = await BilibiliAuthHttpClient.GetAsync("https://api.bilibili.com/x/frontend/finger/spi", cookies).ConfigureAwait(false);
			if (!ok)
			{
				return string.Empty;
			}

			try
			{
				using var document = JsonDocument.Parse(body);
				var root = document.RootElement;
				if (!TryGetCode(root, out var code) || code != 0)
				{
					return string.Empty;
				}

				if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
				{
					return string.Empty;
				}

				return GetPropertyString(dataNode, "b_3");
			}
			catch
			{
				return string.Empty;
			}
		}

		private async Task<(string token, string endpoint, int? code)> GetChatTokenAsync(long roomId, string cookies)
		{
			if (roomId <= 0)
			{
				return (string.Empty, string.Empty, null);
			}

			Dictionary<string, string> signedParams;
			try
			{
				signedParams = await WbiUtils.SignParametersAsync(new Dictionary<string, string>
				{
					["id"] = roomId.ToString(CultureInfo.InvariantCulture),
					["type"] = "0",
					["web_location"] = "444.8"
				}, cookies).ConfigureAwait(false);
			}
			catch
			{
				signedParams = new Dictionary<string, string>();
			}

			if (signedParams.Count > 0)
			{
				var signedQuery = BuildQueryString(signedParams);
				var signedUrl = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?{signedQuery}";
				var signedResult = await TryFetchChatTokenFromUrlAsync(signedUrl, cookies).ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(signedResult.token))
				{
					return signedResult;
				}

				if (signedResult.code == -352)
				{
					_logger.Warning("BILI_DANMUINFO_352_BLOCK_FALLBACK room={RoomId}", roomId);
					return signedResult;
				}
			}

			var fallbackUrl = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?type=0&web_location=444.8&id={roomId.ToString(CultureInfo.InvariantCulture)}";
			return await TryFetchChatTokenFromUrlAsync(fallbackUrl, cookies).ConfigureAwait(false);
		}

		private async Task<(string token, string endpoint, int? code)> TryFetchChatTokenFromUrlAsync(string url, string cookies)
		{
			var (ok, body) = await BilibiliAuthHttpClient.GetAsync(url, cookies).ConfigureAwait(false);
			if (!ok)
			{
				return (string.Empty, string.Empty, null);
			}

			try
			{
				using var document = JsonDocument.Parse(body);
				var root = document.RootElement;
				if (!TryGetCode(root, out var code))
				{
					_logger.Warning("BILI_DANMUINFO_CODE code=unknown message=parse_failed");
					return (string.Empty, string.Empty, null);
				}

				var message = string.Empty;
				if (root.TryGetProperty("message", out var messageNode))
				{
					message = GetElementString(messageNode, string.Empty);
				}

				_logger.Information("BILI_DANMUINFO_CODE code={Code} message={Message}", code, message);
				if (code != 0)
				{
					return (string.Empty, string.Empty, code);
				}

				if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
				{
					return (string.Empty, string.Empty, code);
				}

				var token = GetPropertyString(dataNode, "token");
				var endpoint = ResolveDefaultSocketEndpoint(dataNode);
				return (token, endpoint, code);
			}
			catch
			{
				return (string.Empty, string.Empty, null);
			}
		}

		private static bool TryUseReliableDefaultSocketEndpoint(string endpoint, out string selectedEndpoint, out string reason)
		{
			selectedEndpoint = LIVE_SOCKET_URL;
			reason = string.Empty;
			if (string.IsNullOrWhiteSpace(endpoint))
			{
				reason = "empty_endpoint";
				return false;
			}

			var normalizedEndpoint = endpoint.Trim();

			if (!Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out var uri))
			{
				reason = "invalid_uri";
				return false;
			}

			if (!string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
			{
				reason = "scheme_not_wss";
				return false;
			}

			selectedEndpoint = normalizedEndpoint;
			return true;
		}

		private static string ResolveDefaultSocketEndpoint(JsonElement dataNode)
		{
			try
			{
				if (!dataNode.TryGetProperty("host_list", out var hostListNode) || hostListNode.ValueKind != JsonValueKind.Array)
				{
					return string.Empty;
				}

				var defaultWssPort = GetPropertyInt(dataNode, "wss_port", 443);
				if (defaultWssPort <= 0)
				{
					defaultWssPort = 443;
				}

				foreach (var hostNode in hostListNode.EnumerateArray())
				{
					if (hostNode.ValueKind != JsonValueKind.Object)
					{
						continue;
					}

					var host = GetPropertyString(hostNode, "host");
					if (string.IsNullOrWhiteSpace(host))
					{
						continue;
					}

					var wssPort = GetPropertyInt(hostNode, "wss_port", defaultWssPort);
					if (wssPort <= 0)
					{
						wssPort = defaultWssPort;
					}

					return $"wss://{host}:{wssPort}/sub";
				}
			}
			catch
			{
				// ignore parse error and fallback to constant endpoint
			}

			return string.Empty;
		}

		private static int GetPropertyInt(JsonElement objectNode, string propertyName, int fallback)
		{
			if (objectNode.ValueKind != JsonValueKind.Object || !objectNode.TryGetProperty(propertyName, out var propertyNode))
			{
				return fallback;
			}

			return GetElementInt(propertyNode, fallback);
		}

		private static int GetElementInt(JsonElement node, int fallback)
		{
			switch (node.ValueKind)
			{
				case JsonValueKind.Number:
					if (node.TryGetInt32(out var intValue))
					{
						return intValue;
					}

					if (node.TryGetInt64(out var longValue) && longValue > 0 && longValue <= int.MaxValue)
					{
						return (int)longValue;
					}

					return fallback;

				case JsonValueKind.String:
					var raw = node.GetString();
					return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

				default:
					return fallback;
			}
		}

		private static string RedactEndpoint(string endpoint)
		{
			if (string.IsNullOrWhiteSpace(endpoint))
			{
				return string.Empty;
			}

			if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
			{
				return "***";
			}

			return $"{uri.Scheme}://***:{uri.Port}{uri.AbsolutePath}";
		}

		private static bool TryGetCode(JsonElement root, out int code)
		{
			code = -1;
			if (!root.TryGetProperty("code", out var codeNode))
			{
				return false;
			}

			if (codeNode.ValueKind == JsonValueKind.Number)
			{
				code = codeNode.GetInt32();
				return true;
			}

			if (codeNode.ValueKind == JsonValueKind.String)
			{
				return int.TryParse(codeNode.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
			}

			return false;
		}

		private static string BuildQueryString(Dictionary<string, string> parameters)
		{
			return string.Join("&", parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
		}

		private static long ResolveUserIdFromCookie(string cookies)
		{
			var userIdText = BilibiliCookieHelper.GetCookieValue(cookies, "DedeUserID");
			if (long.TryParse(userIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUserId) && parsedUserId > 0)
			{
				return parsedUserId;
			}

			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}

		private bool IsConfigReady(BilibiliConfig config, out string reason)
		{
			reason = string.Empty;

			if (!config.Enabled)
			{
				reason = "bilibili config disabled";
				return false;
			}

			if (config.RoomId <= 0)
			{
				reason = "room_id<=0";
				return false;
			}

			if (string.IsNullOrWhiteSpace(config.Cookies))
			{
				reason = "qr cookies missing";
				return false;
			}

			return true;
		}

		private static int ReadEnvInt(string name, int defaultValue)
		{
			var raw = Environment.GetEnvironmentVariable(name);
			if (string.IsNullOrWhiteSpace(raw))
			{
				return defaultValue;
			}

			if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
			{
				return parsed;
			}

			return defaultValue;
		}

		private void HeartbeatTimerOnElapsed(object sender, System.Timers.ElapsedEventArgs e)
		{
			SendHeartbeatPacket();
		}

		private void SendHeartbeatPacket()
		{
			if (!_webSocketClient.IsConnected || Volatile.Read(ref _isAuthenticated) == 0)
			{
				return;
			}

			_webSocketClient.Send(_authFacade.CreateHeartbeatPacket());
			Interlocked.Exchange(ref _lastHeartbeatSentTicksUtc, DateTime.UtcNow.Ticks);
		}

		private async void HeartbeatAckWatchdogTimerOnElapsed(object sender, System.Timers.ElapsedEventArgs e)
		{
			if (!_isStarted || _heartbeatAckTimeoutMs <= 0 || Volatile.Read(ref _isAuthenticated) == 0)
			{
				return;
			}

			var sentTicks = Interlocked.Read(ref _lastHeartbeatSentTicksUtc);
			if (sentTicks <= 0)
			{
				return;
			}

			var ackTicks = Interlocked.Read(ref _lastHeartbeatAckTicksUtc);
			if (ackTicks >= sentTicks)
			{
				Interlocked.Exchange(ref _heartbeatAckTimeoutTriggered, 0);
				return;
			}

			var elapsedMs = (DateTime.UtcNow.Ticks - sentTicks) / TimeSpan.TicksPerMillisecond;
			if (elapsedMs < _heartbeatAckTimeoutMs)
			{
				return;
			}

			if (Interlocked.CompareExchange(ref _heartbeatAckTimeoutTriggered, 1, 0) != 0)
			{
				return;
			}

			_logger.Warning("Bilibili heartbeat ACK timeout ({ElapsedMs}ms), reconnecting", elapsedMs);
			await ConnectCoreAsync(true).ConfigureAwait(false);
		}

		private static bool TryParseAuthAck(string body, out string code, out string message)
		{
			code = string.Empty;
			message = string.Empty;

			if (string.IsNullOrWhiteSpace(body))
			{
				return false;
			}

			try
			{
				using var document = JsonDocument.Parse(body);
				var root = document.RootElement;
				if (!root.TryGetProperty("code", out var codeNode))
				{
					return false;
				}

				code = GetElementString(codeNode, string.Empty);
				if (root.TryGetProperty("message", out var messageNode))
				{
					message = GetElementString(messageNode, string.Empty);
				}

				return true;
			}
			catch
			{
				return false;
			}
		}

		private static string GetArrayString(JsonElement arrayNode, int index, string fallback = "")
		{
			if (arrayNode.ValueKind != JsonValueKind.Array || arrayNode.GetArrayLength() <= index || index < 0)
			{
				return fallback;
			}

			return GetElementString(arrayNode[index], fallback);
		}

		private static string GetPropertyString(JsonElement objectNode, string propertyName, string fallback = "")
		{
			if (objectNode.ValueKind != JsonValueKind.Object || !objectNode.TryGetProperty(propertyName, out var propertyNode))
			{
				return fallback;
			}

			return GetElementString(propertyNode, fallback);
		}

		private static string GetElementString(JsonElement node, string fallback)
		{
			switch (node.ValueKind)
			{
				case JsonValueKind.String:
					return node.GetString() ?? fallback;
				case JsonValueKind.Number:
				case JsonValueKind.True:
				case JsonValueKind.False:
					return node.GetRawText();
				default:
					return fallback;
			}
		}

		private void SendMessageToChannel(string message)
		{
			if (string.IsNullOrWhiteSpace(message))
			{
				return;
			}

			var channel = _currentChannel ?? DefaultChannel;
			if (channel == null)
			{
				_logger.Debug("[DEBUG_FEEDBACK_ECHO] Skip synthetic echo because no bilibili channel is available");
				return;
			}

			var syntheticSender = new BilibiliUser(
				"srm",
				"SongRequestManager",
				"点歌姬",
				"#FFFFFF",
				false,
				false);

			var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["source"] = "srm",
				["bili.synthetic"] = "1",
				["cmd"] = "plugin_message"
			};

			var syntheticMessage = new BilibiliMessage(
				Guid.NewGuid().ToString("N"),
				true,
				false,
				false,
				"[点歌姬] " + message,
				syntheticSender,
				channel,
				metadata: metadata);

			_logger.Debug("[DEBUG_FEEDBACK_ECHO] Emitting synthetic bilibili message. id={MessageId} channel={ChannelId} source={Source}",
				syntheticMessage.Id,
				channel.Id,
				metadata["source"]);

			OnTextMessageReceived?.Invoke(this, syntheticMessage);
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_settingsService.OnConfigChanged -= SettingsServiceOnConfigChanged;
			_heartbeatTimer.Elapsed -= HeartbeatTimerOnElapsed;
			_heartbeatAckWatchdogTimer.Elapsed -= HeartbeatAckWatchdogTimerOnElapsed;
			_heartbeatTimer.Dispose();
			_heartbeatAckWatchdogTimer.Dispose();
			_connectLocker.Dispose();
			_webSocketClient.Dispose();
		}
	}
}
