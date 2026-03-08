using System;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Linq;
using WebSocketSharp;
using WebSocketSharp.Net;

namespace CatCore.Services.Bilibili.Internal
{
	internal sealed class BilibiliWebSocketClient : IDisposable
	{
		private readonly object _lock = new();
		private WebSocket? _client;

		public bool IsConnected
		{
			get
			{
				lock (_lock)
				{
					return _client != null && IsClientConnectedUnsafe(_client);
				}
			}
		}

		public event Action? Opened;
		public event Action? Closed;
		public event Action<Exception?>? Error;
		public event Action<byte[]>? DataReceived;

		public void Connect(string uri, bool forceReconnect, string userAgent, string origin)
		{
			lock (_lock)
			{
				if (forceReconnect)
				{
					DisposeClient();
				}

				if (_client != null)
				{
					return;
				}

				_client = CreateClient(uri, userAgent, origin);
				try
				{
					AttachEventHandlers(_client);
					_client.ConnectAsync();
				}
				catch
				{
					DisposeClient();
					throw;
				}
			}
		}

		public void Disconnect()
		{
			lock (_lock)
			{
				DisposeClient();
			}
		}

		public void Send(byte[] data)
		{
			if (data == null || data.Length == 0)
			{
				return;
			}

			lock (_lock)
			{
				if (_client == null || !IsClientOpenUnsafe(_client))
				{
					return;
				}

				_client.Send(data);
			}
		}

		public void Dispose()
		{
			lock (_lock)
			{
				DisposeClient();
			}
		}

		private WebSocket CreateClient(string uri, string userAgent, string origin)
		{
			var client = new WebSocket(uri);
			ApplyConnectionHeaders(client, userAgent, origin);
			ApplyTlsConfiguration(client, uri);
			return client;
		}

		private static void ApplyConnectionHeaders(WebSocket client, string userAgent, string origin)
		{
			if (!string.IsNullOrWhiteSpace(origin))
			{
				client.Origin = origin;
			}

			_ = userAgent;
		}

		private static void ApplyTlsConfiguration(WebSocket client, string uri)
		{
			var sslConfiguration = client.SslConfiguration;
			if (sslConfiguration == null)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration is null; TLS protocol tuning skipped.");
				return;
			}

			if (!TryParseEnumValue("Tls12", out var tls12Value))
			{
				if (TryBuildLegacyTlsFallback(out var legacyTlsValue, out var enabledNames))
				{
					TrySetEnabledSslProtocols(sslConfiguration, legacyTlsValue,
						$"websocket-sharp Tls12 enum unavailable; fallback to {enabledNames} for TLS handshake compatibility.");
				}
				else
				{
					ReportTlsDiagnostic("websocket-sharp TLS enum does not contain Tls12/Tls11/Tls; TLS protocol tuning skipped.");
				}
			}
			else
			{
				TrySetEnabledSslProtocols(sslConfiguration, tls12Value, "websocket-sharp TLS protocol locked to Tls12.");
			}

			try
			{
				ConfigureTargetHost(sslConfiguration, uri);
			}
			catch (Exception ex)
			{
				ReportTlsDiagnostic("websocket-sharp TargetHost configuration failed unexpectedly.", ex);
			}

			try
			{
				ConfigureCertificateValidationCallback(sslConfiguration);
			}
			catch (Exception ex)
			{
				ReportTlsDiagnostic("websocket-sharp certificate callback configuration failed unexpectedly.", ex);
			}
		}

		private static void ConfigureTargetHost(ClientSslConfiguration sslConfiguration, string uri)
		{
			Uri? parsedUri;
			try
			{
				parsedUri = new Uri(uri);
			}
			catch (Exception ex)
			{
				ReportTlsDiagnostic($"websocket-sharp URI parse failed for TargetHost configuration: {uri}", ex);
				return;
			}

			var host = parsedUri.Host;
			if (!IPAddress.TryParse(host, out _))
			{
				return;
			}

			try
			{
				sslConfiguration.TargetHost = "broadcastlv.chat.bilibili.com";
				ReportTlsDiagnostic($"websocket-sharp TargetHost set to broadcastlv.chat.bilibili.com for IP endpoint {host}.");
			}
			catch (Exception ex)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration.TargetHost set failed.", ex);
			}
		}

		private static void ConfigureCertificateValidationCallback(ClientSslConfiguration sslConfiguration)
		{
			var callback = new RemoteCertificateValidationCallback((sender, certificate, chain, sslPolicyErrors) =>
			{
				try
				{
					var chainDetails = chain == null
						? "chain=null"
						: string.Join(";", chain.ChainStatus.Select(s => $"{s.Status}:{s.StatusInformation?.Trim()}"));
					if (string.IsNullOrWhiteSpace(chainDetails))
					{
						chainDetails = "chain=ok";
					}

					System.Diagnostics.Trace.TraceWarning("websocket-sharp cert validation: SslPolicyErrors={0}, ChainStatus={1}", sslPolicyErrors, chainDetails);
				}
				catch
				{
					// logging failures must not alter TLS decision
				}

				if (sslPolicyErrors == SslPolicyErrors.None)
				{
					return true;
				}

				if ((sslPolicyErrors & ~(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
				{
					return false;
				}

				if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 && !IsBilibiliChatCertificate(certificate))
				{
					return false;
				}

				if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) != 0 && !HasOnlyIgnorableChainErrors(chain))
				{
					return false;
				}

				ReportTlsDiagnostic($"websocket-sharp cert validation relaxed allow: SslPolicyErrors={sslPolicyErrors}");
				return true;
			});

			try
			{
				sslConfiguration.ServerCertificateValidationCallback = callback;
				ReportTlsDiagnostic("websocket-sharp certificate validation callback installed.");
			}
			catch (Exception ex)
			{
				ReportTlsDiagnostic("websocket-sharp ServerCertificateValidationCallback set failed.", ex);
			}
		}

		private static bool IsBilibiliChatCertificate(X509Certificate? certificate)
		{
			if (certificate == null)
			{
				return false;
			}

			try
			{
				var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
				var dnsName = certificate2.GetNameInfo(X509NameType.DnsName, false);
				return !string.IsNullOrWhiteSpace(dnsName)
					&& (dnsName.Equals("broadcastlv.chat.bilibili.com", StringComparison.OrdinalIgnoreCase)
						|| dnsName.EndsWith(".chat.bilibili.com", StringComparison.OrdinalIgnoreCase)
						|| dnsName.Equals("*.chat.bilibili.com", StringComparison.OrdinalIgnoreCase));
			}
			catch
			{
				return false;
			}
		}

		private static bool HasOnlyIgnorableChainErrors(X509Chain? chain)
		{
			if (chain == null)
			{
				return false;
			}

			foreach (var status in chain.ChainStatus)
			{
				if ((status.Status & ~(X509ChainStatusFlags.UntrustedRoot
					| X509ChainStatusFlags.PartialChain
					| X509ChainStatusFlags.RevocationStatusUnknown
					| X509ChainStatusFlags.OfflineRevocation)) != 0)
				{
					return false;
				}
			}

			return true;
		}

		private static void TrySetEnabledSslProtocols(ClientSslConfiguration sslConfiguration, SslProtocols protocolValue, string diagnosticMessage)
		{
			try
			{
				sslConfiguration.EnabledSslProtocols = protocolValue;
			}
			catch (Exception ex)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration.EnabledSslProtocols set failed.", ex);
				return;
			}

			ReportTlsDiagnostic(diagnosticMessage);
		}

		private static void ReportTlsDiagnostic(string message, Exception? exception = null)
		{
			try
			{
				if (exception == null)
				{
					System.Diagnostics.Trace.TraceWarning(message);
				}
				else
				{
					System.Diagnostics.Trace.TraceWarning("{0} {1}", message, exception);
				}
			}
			catch
			{
				// diagnostics must never break websocket creation
			}
		}

		private static bool TryBuildLegacyTlsFallback(out SslProtocols value, out string enabledNames)
		{
			value = default;
			enabledNames = string.Empty;

			var selectedNames = new System.Collections.Generic.List<string>();
			var merged = default(SslProtocols);

			if (TryParseEnumValue("Tls11", out var tls11Value))
			{
				merged |= tls11Value;
				selectedNames.Add("Tls11");
			}

			if (TryParseEnumValue("Tls", out var tlsValue))
			{
				merged |= tlsValue;
				selectedNames.Add("Tls");
			}

			if (merged == default)
			{
				return false;
			}

			value = merged;
			enabledNames = string.Join("|", selectedNames);
			return true;
		}

		private static bool TryParseEnumValue(string memberName, out SslProtocols value)
		{
			value = default;
			try
			{
				value = (SslProtocols)Enum.Parse(typeof(SslProtocols), memberName, ignoreCase: false);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private void AttachEventHandlers(WebSocket client)
		{
			client.OnOpen += HandleClientOpened;
			client.OnClose += HandleClientClosed;
			client.OnError += HandleClientError;
			client.OnMessage += HandleClientMessage;
		}

		private void DetachEventHandlers(WebSocket client)
		{
			try
			{
				client.OnOpen -= HandleClientOpened;
			}
			catch
			{
				// ignored
			}

			try
			{
				client.OnClose -= HandleClientClosed;
			}
			catch
			{
				// ignored
			}

			try
			{
				client.OnError -= HandleClientError;
			}
			catch
			{
				// ignored
			}

			try
			{
				client.OnMessage -= HandleClientMessage;
			}
			catch
			{
				// ignored
			}
		}

		private void DisposeClient()
		{
			if (_client == null)
			{
				return;
			}

			var client = _client;
			_client = null;

			try
			{
				DetachEventHandlers(client);

				if (IsClientConnectedUnsafe(client))
				{
					client.Close();
				}
			}
			catch
			{
				// ignored
			}

		}

		private void HandleClientOpened(object? sender, EventArgs e)
		{
			Opened?.Invoke();
		}

		private void HandleClientClosed(object? sender, CloseEventArgs e)
		{
			lock (_lock)
			{
				DisposeClient();
			}

			Closed?.Invoke();
		}

		private void HandleClientError(object? sender, ErrorEventArgs e)
		{
			Error?.Invoke(ExtractException(e));
		}

		private void HandleClientMessage(object? sender, MessageEventArgs e)
		{
			if (!e.IsBinary || e.RawData == null || e.RawData.Length == 0)
			{
				return;
			}

			DataReceived?.Invoke(e.RawData);
		}

		private static bool IsClientConnectedUnsafe(WebSocket client)
		{
			return client.ReadyState == WebSocketState.Open
				|| client.ReadyState == WebSocketState.Connecting;
		}

		private static bool IsClientOpenUnsafe(WebSocket client)
		{
			return client.ReadyState == WebSocketState.Open;
		}

		private static Exception? ExtractException(ErrorEventArgs eventArgs)
		{
			if (eventArgs.Exception != null)
			{
				if (!string.IsNullOrWhiteSpace(eventArgs.Message)
					&& !string.Equals(eventArgs.Exception.Message, eventArgs.Message, StringComparison.Ordinal))
				{
					return new Exception($"{eventArgs.Exception.Message} | websocket-sharp={eventArgs.Message}", eventArgs.Exception);
				}

				return eventArgs.Exception;
			}

			if (!string.IsNullOrWhiteSpace(eventArgs.Message))
			{
				return new Exception($"websocket-sharp OnError: {eventArgs.Message}");
			}

			return new Exception("websocket-sharp OnError without exception/message.");
		}
	}
}
