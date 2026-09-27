using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace CatCore.Services.Overlay
{
	internal sealed class OverlayWebSocketService : IOverlayWebSocketService
	{
		private static readonly string[] AVAILABLE_CHANNELS = { "twitch", "twitch_raw", "bilibili", "bilibili_raw" };
		private static readonly HashSet<string> AVAILABLE_CHANNELS_SET = new HashSet<string>(AVAILABLE_CHANNELS, StringComparer.Ordinal);

		private readonly ILogger _logger;
		private readonly object _stateLock = new object();
		private readonly ConcurrentDictionary<string, OverlayClientSession> _sessions = new ConcurrentDictionary<string, OverlayClientSession>(StringComparer.Ordinal);

		private HttpListener? _listener;
		private CancellationTokenSource? _cancellationTokenSource;
		private Task? _acceptLoopTask;
		private bool _isRunning;
		private bool _disposed;

		public OverlayWebSocketService(ILogger logger)
		{
			_logger = logger;
		}

		public void Start(Uri webApiUri)
		{
			if (webApiUri == null)
			{
				throw new ArgumentNullException(nameof(webApiUri));
			}

			lock (_stateLock)
			{
				if (_disposed || _isRunning)
				{
					return;
				}

				var prefix = BuildOverlayPrefix(webApiUri);
				_listener = new HttpListener();
				_listener.Prefixes.Add(prefix);
				_cancellationTokenSource = new CancellationTokenSource();

				_listener.Start();
				_isRunning = true;
				_acceptLoopTask = Task.Run(() => AcceptLoopAsync(_listener, _cancellationTokenSource.Token));

				_logger.Information("Overlay websocket server started at {Prefix}", prefix);
			}
		}

		public void Stop()
		{
			HttpListener? listener;
			CancellationTokenSource? cancellationTokenSource;
			Task? acceptLoopTask;

			lock (_stateLock)
			{
				if (!_isRunning)
				{
					return;
				}

				_isRunning = false;
				listener = _listener;
				cancellationTokenSource = _cancellationTokenSource;
				acceptLoopTask = _acceptLoopTask;

				_listener = null;
				_cancellationTokenSource = null;
				_acceptLoopTask = null;
			}

			try
			{
				cancellationTokenSource?.Cancel();
			}
			catch
			{
				// ignored
			}

			try
			{
				listener?.Close();
			}
			catch
			{
				// ignored
			}

			foreach (var session in _sessions.Values)
			{
				_ = session.CloseAsync(WebSocketCloseStatus.NormalClosure, "server_shutdown", CancellationToken.None);
			}

			_sessions.Clear();

			if (acceptLoopTask != null)
			{
				try
				{
					acceptLoopTask.Wait(TimeSpan.FromSeconds(2));
				}
				catch
				{
					// ignored
				}
			}

			cancellationTokenSource?.Dispose();
			_logger.Information("Overlay websocket server stopped");
		}

		public void BroadcastData(string channel, string data)
		{
			if (!_isRunning || string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(data) || !AVAILABLE_CHANNELS_SET.Contains(channel))
			{
				return;
			}

			var payload = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
			{
				["cmd"] = "data",
				["channel"] = channel,
				["data"] = data
			});

			foreach (var session in _sessions.Values)
			{
				if (!session.IsSubscribed(channel))
				{
					continue;
				}

				_ = session.SendAsync(payload, CancellationToken.None);
			}
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			Stop();
		}

		private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				HttpListenerContext? context = null;
				try
				{
					context = await listener.GetContextAsync().ConfigureAwait(false);
				}
				catch (HttpListenerException)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						break;
					}

					continue;
				}
				catch (ObjectDisposedException)
				{
					break;
				}
				catch (InvalidOperationException)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						break;
					}

					continue;
				}
				catch (Exception e)
				{
					_logger.Warning(e, "Overlay websocket accept loop exception");
					continue;
				}

				if (context != null)
				{
					_ = Task.Run(() => HandleContextAsync(context, cancellationToken));
				}
			}
		}

		private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
		{
			if (!context.Request.IsWebSocketRequest)
			{
				context.Response.StatusCode = 400;
				context.Response.Close();
				return;
			}

			OverlayClientSession? session = null;
			try
			{
				var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
				session = new OverlayClientSession(wsContext.WebSocket);
				_sessions[session.Id] = session;

				await ReceiveLoopAsync(session, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception e)
			{
				_logger.Debug(e, "Overlay websocket client handling failed");
			}
			finally
			{
				if (session != null)
				{
					_sessions.TryRemove(session.Id, out _);
					session.Dispose();
				}
			}
		}

		private async Task ReceiveLoopAsync(OverlayClientSession session, CancellationToken cancellationToken)
		{
			var receiveBuffer = new byte[4096];
			while (!cancellationToken.IsCancellationRequested)
			{
				var message = await ReceiveTextAsync(session.Socket, receiveBuffer, cancellationToken).ConfigureAwait(false);
				if (message == null)
				{
					break;
				}

				await HandleCommandAsync(session, message, cancellationToken).ConfigureAwait(false);
			}
		}

		private async Task HandleCommandAsync(OverlayClientSession session, string message, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(message))
			{
				return;
			}

			try
			{
				using var doc = JsonDocument.Parse(message);
				if (!doc.RootElement.TryGetProperty("cmd", out var cmdNode) || cmdNode.ValueKind != JsonValueKind.String)
				{
					return;
				}

				var cmd = cmdNode.GetString() ?? string.Empty;
				switch (cmd)
				{
					case "sub":
						session.Subscribe(ReadRequestedChannels(doc.RootElement));
						await SendChannelsAckAsync(session, "sub", cancellationToken).ConfigureAwait(false);
						break;
					case "unsub":
						session.Unsubscribe(ReadRequestedChannels(doc.RootElement));
						await SendChannelsAckAsync(session, "unsub", cancellationToken).ConfigureAwait(false);
						break;
					case "ping":
						await session.SendCommandAsync("pong", null, cancellationToken).ConfigureAwait(false);
						break;
					case "disconnect":
						await session.SendCommandAsync("disconnected", null, cancellationToken).ConfigureAwait(false);
						await session.CloseAsync(WebSocketCloseStatus.NormalClosure, "client_disconnect", cancellationToken).ConfigureAwait(false);
						break;
				}
			}
			catch
			{
				// ignore malformed client payloads
			}
		}

		private async Task SendChannelsAckAsync(OverlayClientSession session, string command, CancellationToken cancellationToken)
		{
			var channels = AVAILABLE_CHANNELS.Where(session.IsSubscribed).ToArray();
			await session.SendCommandAsync(command, channels, cancellationToken).ConfigureAwait(false);
		}

		private static IEnumerable<string> ReadRequestedChannels(JsonElement root)
		{
			if (!root.TryGetProperty("channels", out var channelsNode) || channelsNode.ValueKind != JsonValueKind.Array)
			{
				return Array.Empty<string>();
			}

			var channels = new List<string>();
			foreach (var item in channelsNode.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.String)
				{
					continue;
				}

				var channel = item.GetString();
				if (channel != null && AVAILABLE_CHANNELS_SET.Contains(channel))
				{
					channels.Add(channel);
				}
			}

			return channels;
		}

		private static async Task<string?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
		{
			if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
			{
				return null;
			}

			using var messageStream = new MemoryStream();
			while (true)
			{
				WebSocketReceiveResult result;
				try
				{
					result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
				}
				catch
				{
					return null;
				}

				if (result.MessageType == WebSocketMessageType.Close)
				{
					try
					{
						if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
						{
							await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
						}
					}
					catch
					{
						// ignored
					}

					return null;
				}

				if (result.MessageType != WebSocketMessageType.Text)
				{
					continue;
				}

				if (result.Count > 0)
				{
					messageStream.Write(buffer, 0, result.Count);
				}

				if (result.EndOfMessage)
				{
					break;
				}
			}

			return Encoding.UTF8.GetString(messageStream.ToArray());
		}

		private static string BuildOverlayPrefix(Uri webApiUri)
		{
			// 配置网页使用 8338，Overlay 使用前一个端口 8337，避开 ChatPlexSDK 默认占用的 8339。
			var websocketPort = webApiUri.Port <= 1 ? ushort.MaxValue : webApiUri.Port - 1;
			var host = string.IsNullOrWhiteSpace(webApiUri.Host) ? "localhost" : webApiUri.Host;
			return $"http://{host}:{websocketPort}/";
		}

		private sealed class OverlayClientSession : IDisposable
		{
			private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
			private readonly object _channelsLock = new object();
			private readonly HashSet<string> _subscribedChannels = new HashSet<string>(StringComparer.Ordinal);

			public OverlayClientSession(WebSocket socket)
			{
				Socket = socket;
				Id = Guid.NewGuid().ToString("N");
			}

			public string Id { get; }
			public WebSocket Socket { get; }

			public bool IsSubscribed(string channel)
			{
				lock (_channelsLock)
				{
					return _subscribedChannels.Contains(channel);
				}
			}

			public void Subscribe(IEnumerable<string> channels)
			{
				lock (_channelsLock)
				{
					foreach (var channel in channels)
					{
						_subscribedChannels.Add(channel);
					}
				}
			}

			public void Unsubscribe(IEnumerable<string> channels)
			{
				lock (_channelsLock)
				{
					foreach (var channel in channels)
					{
						_subscribedChannels.Remove(channel);
					}
				}
			}

			public Task SendCommandAsync(string command, string[]? channels, CancellationToken cancellationToken)
			{
				var payload = new Dictionary<string, object>(StringComparer.Ordinal)
				{
					["cmd"] = command
				};

				if (channels != null)
				{
					payload["channels"] = channels;
				}

				var json = JsonSerializer.Serialize(payload);
				return SendAsync(json, cancellationToken);
			}

			public async Task SendAsync(string payload, CancellationToken cancellationToken)
			{
				if (Socket.State != WebSocketState.Open)
				{
					return;
				}

				var bytes = Encoding.UTF8.GetBytes(payload);
				await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
				try
				{
					if (Socket.State != WebSocketState.Open)
					{
						return;
					}

					await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
				}
				catch
				{
					// ignore single-client send failures
				}
				finally
				{
					_sendLock.Release();
				}
			}

			public async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
			{
				try
				{
					if (Socket.State == WebSocketState.Open || Socket.State == WebSocketState.CloseReceived)
					{
						await Socket.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
					}
				}
				catch
				{
					// ignored
				}
			}

			public void Dispose()
			{
				try
				{
					Socket.Dispose();
				}
				catch
				{
					// ignored
				}

				_sendLock.Dispose();
			}
		}
	}
}
