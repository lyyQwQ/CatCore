using System;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace CatCore.Services.Bilibili.Internal
{
	internal sealed class BilibiliWebSocketClient : IDisposable
	{
		private const string WebSocketSharpAssemblyName = "websocket-sharp";
		private const string WebSocketSharpTypeName = "WebSocketSharp.WebSocket";

		private static readonly object AssemblyLoadLock = new();
		private static Assembly? _resolvedWebSocketSharpAssembly;
		private static readonly ConcurrentDictionary<Type, BinaryFrameProperties> _binaryFramePropertiesCache = new();

		private readonly object _lock = new();

		private sealed class BinaryFrameProperties
		{
			public BinaryFrameProperties(Type argsType)
			{
				IsBinaryProperty = argsType.GetProperty("IsBinary", BindingFlags.Instance | BindingFlags.Public);
				RawDataProperty = argsType.GetProperty("RawData", BindingFlags.Instance | BindingFlags.Public);
			}

			public PropertyInfo? IsBinaryProperty { get; }
			public PropertyInfo? RawDataProperty { get; }
		}

		private object? _client;
		private PropertyInfo? _readyStateProperty;
		private PropertyInfo? _isAliveProperty;
		private PropertyInfo? _originProperty;
		private PropertyInfo? _customHeadersProperty;
		private MethodInfo? _connectAsyncMethod;
		private MethodInfo? _connectMethod;
		private MethodInfo? _closeMethod;
		private MethodInfo? _sendBinaryMethod;
		private MethodInfo? _disposeMethod;
		private EventInfo? _openedEvent;
		private EventInfo? _closedEvent;
		private EventInfo? _errorEvent;
		private EventInfo? _messageEvent;
		private Delegate? _openedHandler;
		private Delegate? _closedHandler;
		private Delegate? _errorHandler;
		private Delegate? _messageHandler;

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
					StartConnect(_client);
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
				if (_client == null || _sendBinaryMethod == null || !IsClientOpenUnsafe(_client))
				{
					return;
				}

				InvokeOrThrow(_sendBinaryMethod, _client, data);
			}
		}

		private void HandleClientOpened(object? sender, EventArgs e)
		{
			Opened?.Invoke();
		}

		private void HandleClientClosed(object? sender, EventArgs e)
		{
			lock (_lock)
			{
				DisposeClient();
			}

			Closed?.Invoke();
		}

		private void HandleClientError(object? sender, EventArgs e)
		{
			Error?.Invoke(ExtractException(e));
		}

		private void HandleClientMessage(object? sender, EventArgs e)
		{
			if (!TryExtractBinaryFrame(e, out var payload))
			{
				return;
			}

			DataReceived?.Invoke(payload);
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

				if (_closeMethod != null && IsClientConnectedUnsafe(client))
				{
					_closeMethod.Invoke(client, null);
				}
			}
			catch
			{
				// ignored
			}

			try
			{
				_disposeMethod?.Invoke(client, null);
			}
			catch
			{
				// ignored
			}

			ResetClientReflectionState();
		}

		public void Dispose()
		{
			lock (_lock)
			{
				DisposeClient();
			}
		}

		private static Assembly ResolveWebSocketSharpAssembly()
		{
			lock (AssemblyLoadLock)
			{
				if (_resolvedWebSocketSharpAssembly != null)
				{
					return _resolvedWebSocketSharpAssembly;
				}

				var loadedAssembly = AppDomain.CurrentDomain
					.GetAssemblies()
					.FirstOrDefault(a => string.Equals(a.GetName().Name, WebSocketSharpAssemblyName, StringComparison.OrdinalIgnoreCase));

				if (loadedAssembly != null)
				{
					_resolvedWebSocketSharpAssembly = loadedAssembly;
					return loadedAssembly;
				}

				var candidatePath = Path.Combine(AppContext.BaseDirectory, "Libs", "websocket-sharp.dll");
				if (!File.Exists(candidatePath))
				{
					throw new InvalidOperationException($"Unable to locate websocket-sharp assembly. Tried loaded assemblies and '{candidatePath}'.");
				}

				try
				{
					_resolvedWebSocketSharpAssembly = Assembly.LoadFrom(candidatePath);
					return _resolvedWebSocketSharpAssembly;
				}
				catch (Exception ex)
				{
					throw new InvalidOperationException($"Failed to load websocket-sharp from '{candidatePath}'.", ex);
				}
			}
		}

		private object CreateClient(string uri, string userAgent, string origin)
		{
			var assembly = ResolveWebSocketSharpAssembly();
			var webSocketType = assembly.GetType(WebSocketSharpTypeName, throwOnError: false);
			if (webSocketType == null)
			{
				throw new InvalidOperationException($"Type '{WebSocketSharpTypeName}' was not found in assembly '{assembly.FullName}'.");
			}

			var client = CreateWebSocketInstance(webSocketType, uri);

			_readyStateProperty = webSocketType.GetProperty("ReadyState", BindingFlags.Instance | BindingFlags.Public);
			_isAliveProperty = webSocketType.GetProperty("IsAlive", BindingFlags.Instance | BindingFlags.Public);
			_originProperty = webSocketType.GetProperty("Origin", BindingFlags.Instance | BindingFlags.Public);
			_customHeadersProperty = webSocketType.GetProperty("CustomHeaders", BindingFlags.Instance | BindingFlags.Public);
			_connectAsyncMethod = webSocketType.GetMethod("ConnectAsync", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
			_connectMethod = webSocketType.GetMethod("Connect", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
			_closeMethod = webSocketType.GetMethod("Close", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
			_sendBinaryMethod = webSocketType.GetMethod("Send", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte[]) }, null);
			_disposeMethod = webSocketType.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
			_openedEvent = webSocketType.GetEvent("OnOpen", BindingFlags.Instance | BindingFlags.Public);
			_closedEvent = webSocketType.GetEvent("OnClose", BindingFlags.Instance | BindingFlags.Public);
			_errorEvent = webSocketType.GetEvent("OnError", BindingFlags.Instance | BindingFlags.Public);
			_messageEvent = webSocketType.GetEvent("OnMessage", BindingFlags.Instance | BindingFlags.Public);

			if (_connectMethod == null && _connectAsyncMethod == null)
			{
				throw new MissingMethodException(webSocketType.FullName, "Connect/ConnectAsync");
			}

			if (_sendBinaryMethod == null)
			{
				throw new MissingMethodException(webSocketType.FullName, "Send(byte[])");
			}

			if (_openedEvent == null || _closedEvent == null || _errorEvent == null || _messageEvent == null)
			{
				throw new MissingMemberException(webSocketType.FullName, "OnOpen/OnClose/OnError/OnMessage");
			}

			ApplyConnectionHeaders(client, userAgent, origin);
			ApplyTlsConfiguration(client, webSocketType, uri);
			return client;
		}

		private void ApplyTlsConfiguration(object client, Type webSocketType, string uri)
		{
			var sslConfigurationProperty = webSocketType.GetProperty("SslConfiguration", BindingFlags.Instance | BindingFlags.Public);
			if (sslConfigurationProperty == null)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration property not found; TLS protocol tuning skipped.");
				return;
			}

			object? sslConfiguration;
			try
			{
				sslConfiguration = sslConfigurationProperty.GetValue(client);
			}
			catch (Exception ex)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration get failed; TLS protocol tuning skipped.", ex);
				return;
			}

			if (sslConfiguration == null)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration is null; TLS protocol tuning skipped.");
				return;
			}

			var enabledSslProtocolsProperty = sslConfiguration.GetType().GetProperty("EnabledSslProtocols", BindingFlags.Instance | BindingFlags.Public);
			if (enabledSslProtocolsProperty == null || !enabledSslProtocolsProperty.CanWrite)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration.EnabledSslProtocols missing or not writable; TLS protocol tuning skipped.");
			}

			if (enabledSslProtocolsProperty != null && enabledSslProtocolsProperty.CanWrite)
			{
				if (!TryParseEnumValue(enabledSslProtocolsProperty.PropertyType, "Tls12", out var tls12Value))
				{
					if (TryBuildLegacyTlsFallback(enabledSslProtocolsProperty.PropertyType, out var legacyTlsValue, out var enabledNames))
					{
						TrySetEnabledSslProtocols(enabledSslProtocolsProperty, sslConfiguration, legacyTlsValue,
							$"websocket-sharp Tls12 enum unavailable; fallback to {enabledNames} for TLS handshake compatibility.");
					}
					else
					{
						ReportTlsDiagnostic("websocket-sharp TLS enum does not contain Tls12/Tls11/Tls; TLS protocol tuning skipped.");
					}
				}
				else
				{
					TrySetEnabledSslProtocols(enabledSslProtocolsProperty, sslConfiguration, tls12Value,
						"websocket-sharp TLS protocol locked to Tls12.");
				}
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

		private static void ConfigureTargetHost(object sslConfiguration, string uri)
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

			var targetHostProperty = sslConfiguration.GetType().GetProperty("TargetHost", BindingFlags.Instance | BindingFlags.Public);
			if (targetHostProperty == null)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration.TargetHost property not found; SNI host override skipped.");
				return;
			}

			if (!targetHostProperty.CanWrite)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration.TargetHost is read-only; SNI host override skipped.");
				return;
			}

			try
			{
				targetHostProperty.SetValue(sslConfiguration, "broadcastlv.chat.bilibili.com");
				ReportTlsDiagnostic($"websocket-sharp TargetHost set to broadcastlv.chat.bilibili.com for IP endpoint {host}.");
			}
			catch (Exception ex)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration.TargetHost set failed.", ex);
			}
		}

		private static void ConfigureCertificateValidationCallback(object sslConfiguration)
		{
			var callbackProperty = sslConfiguration.GetType().GetProperty("ServerCertificateValidationCallback", BindingFlags.Instance | BindingFlags.Public);
			if (callbackProperty == null)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration.ServerCertificateValidationCallback property not found; certificate diagnostics skipped.");
				return;
			}

			if (!callbackProperty.CanWrite)
			{
				ReportTlsDiagnostic("websocket-sharp SslConfiguration.ServerCertificateValidationCallback is read-only; certificate diagnostics skipped.");
				return;
			}

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

				// Keep strict defaults, but tolerate common chain/name issues on older Windows trust stores.
				ReportTlsDiagnostic($"websocket-sharp cert validation relaxed allow: SslPolicyErrors={sslPolicyErrors}");
				return true;
			});

			if (callbackProperty.PropertyType.IsInstanceOfType(callback))
			{
				try
				{
					callbackProperty.SetValue(sslConfiguration, callback);
					ReportTlsDiagnostic("websocket-sharp certificate validation callback installed.");
				}
				catch (Exception ex)
				{
					ReportTlsDiagnostic("websocket-sharp ServerCertificateValidationCallback set failed.", ex);
				}

				return;
			}

			ReportTlsDiagnostic($"websocket-sharp ServerCertificateValidationCallback type mismatch: expected {callbackProperty.PropertyType.FullName}.");
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

		private void TrySetEnabledSslProtocols(PropertyInfo enabledSslProtocolsProperty, object sslConfiguration, object protocolValue, string diagnosticMessage)
		{
			try
			{
				enabledSslProtocolsProperty.SetValue(sslConfiguration, protocolValue);
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

		private static bool TryBuildLegacyTlsFallback(Type enumType, out object value, out string enabledNames)
		{
			value = Activator.CreateInstance(enumType) ?? 0;
			enabledNames = string.Empty;

			var selectedNames = new System.Collections.Generic.List<string>();
			ulong merged = 0;

			if (TryParseEnumValue(enumType, "Tls11", out var tls11Value))
			{
				merged |= Convert.ToUInt64(tls11Value, CultureInfo.InvariantCulture);
				selectedNames.Add("Tls11");
			}

			if (TryParseEnumValue(enumType, "Tls", out var tlsValue))
			{
				merged |= Convert.ToUInt64(tlsValue, CultureInfo.InvariantCulture);
				selectedNames.Add("Tls");
			}

			if (merged == 0)
			{
				return false;
			}

			value = Enum.ToObject(enumType, merged);
			enabledNames = string.Join("|", selectedNames);
			return true;
		}

		private static bool TryParseEnumValue(Type enumType, string memberName, out object value)
		{
			value = Activator.CreateInstance(enumType) ?? 0;
			if (!enumType.IsEnum)
			{
				return false;
			}

			try
			{
				value = Enum.Parse(enumType, memberName, ignoreCase: false);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static object CreateWebSocketInstance(Type webSocketType, string uri)
		{
			var constructor = webSocketType.GetConstructor(new[] { typeof(string) });
			if (constructor != null)
			{
				return constructor.Invoke(new object[] { uri });
			}

			constructor = webSocketType.GetConstructor(new[] { typeof(string), typeof(string[]) });
			if (constructor != null)
			{
				return constructor.Invoke(new object[] { uri, Array.Empty<string>() });
			}

			throw new MissingMethodException(webSocketType.FullName, ".ctor(string)");
		}

		private void ApplyConnectionHeaders(object client, string userAgent, string origin)
		{
			if (!string.IsNullOrWhiteSpace(origin) && _originProperty != null && _originProperty.CanWrite)
			{
				_originProperty.SetValue(client, origin);
			}

			if (string.IsNullOrWhiteSpace(userAgent) || _customHeadersProperty == null)
			{
				return;
			}

			var headers = _customHeadersProperty.GetValue(client) as NameValueCollection;
			if (headers == null)
			{
				if (!_customHeadersProperty.CanWrite)
				{
					return;
				}

				headers = new NameValueCollection();
				_customHeadersProperty.SetValue(client, headers);
			}

			headers["User-Agent"] = userAgent;
		}

		private void AttachEventHandlers(object client)
		{
			if (_openedEvent == null || _closedEvent == null || _errorEvent == null || _messageEvent == null)
			{
				throw new InvalidOperationException("WebSocket events are not initialized.");
			}

			_openedHandler = CreateEventHandler(_openedEvent, nameof(HandleClientOpened));
			_closedHandler = CreateEventHandler(_closedEvent, nameof(HandleClientClosed));
			_errorHandler = CreateEventHandler(_errorEvent, nameof(HandleClientError));
			_messageHandler = CreateEventHandler(_messageEvent, nameof(HandleClientMessage));

			_openedEvent.AddEventHandler(client, _openedHandler);
			_closedEvent.AddEventHandler(client, _closedHandler);
			_errorEvent.AddEventHandler(client, _errorHandler);
			_messageEvent.AddEventHandler(client, _messageHandler);
		}

		private Delegate CreateEventHandler(EventInfo eventInfo, string methodName)
		{
			var method = typeof(BilibiliWebSocketClient).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
			if (method == null || eventInfo.EventHandlerType == null)
			{
				throw new InvalidOperationException($"Cannot bind handler '{methodName}' for event '{eventInfo.Name}'.");
			}

			var handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, this, method, throwOnBindFailure: false);
			if (handler == null)
			{
				throw new InvalidOperationException($"Cannot create delegate for event '{eventInfo.Name}'.");
			}

			return handler;
		}

		private void DetachEventHandlers(object client)
		{
			try
			{
				if (_openedEvent != null && _openedHandler != null)
				{
					_openedEvent.RemoveEventHandler(client, _openedHandler);
				}
			}
			catch
			{
				// ignored
			}

			try
			{
				if (_closedEvent != null && _closedHandler != null)
				{
					_closedEvent.RemoveEventHandler(client, _closedHandler);
				}
			}
			catch
			{
				// ignored
			}

			try
			{
				if (_errorEvent != null && _errorHandler != null)
				{
					_errorEvent.RemoveEventHandler(client, _errorHandler);
				}
			}
			catch
			{
				// ignored
			}

			try
			{
				if (_messageEvent != null && _messageHandler != null)
				{
					_messageEvent.RemoveEventHandler(client, _messageHandler);
				}
			}
			catch
			{
				// ignored
			}
		}

		private static void InvokeOrThrow(MethodInfo methodInfo, object target, params object[] args)
		{
			try
			{
				methodInfo.Invoke(target, args);
			}
			catch (TargetInvocationException ex) when (ex.InnerException != null)
			{
				throw ex.InnerException;
			}
		}

		private void StartConnect(object client)
		{
			if (_connectAsyncMethod != null)
			{
				InvokeOrThrow(_connectAsyncMethod, client);
				return;
			}

			if (_connectMethod == null)
			{
				throw new InvalidOperationException("No websocket connect method available.");
			}

			var connectMethod = _connectMethod;
			_ = Task.Run(() =>
			{
				try
				{
					InvokeOrThrow(connectMethod, client);
				}
				catch (Exception ex)
				{
					Error?.Invoke(ex);
				}
			});
		}

		private bool IsClientConnectedUnsafe(object client)
		{
			if (TryReadStateName(client, out var stateName))
			{
				return string.Equals(stateName, "Open", StringComparison.Ordinal)
					|| string.Equals(stateName, "Connecting", StringComparison.Ordinal);
			}

			return TryReadIsAlive(client);
		}

		private bool IsClientOpenUnsafe(object client)
		{
			if (TryReadStateName(client, out var stateName))
			{
				return string.Equals(stateName, "Open", StringComparison.Ordinal);
			}

			return TryReadIsAlive(client);
		}

		private bool TryReadStateName(object client, out string stateName)
		{
			stateName = string.Empty;
			if (_readyStateProperty == null)
			{
				return false;
			}

			try
			{
				var state = _readyStateProperty.GetValue(client);
				if (state == null)
				{
					return false;
				}

				stateName = state.ToString() ?? string.Empty;
				return !string.IsNullOrWhiteSpace(stateName);
			}
			catch
			{
				return false;
			}
		}

		private bool TryReadIsAlive(object client)
		{
			if (_isAliveProperty == null)
			{
				return false;
			}

			try
			{
				return _isAliveProperty.GetValue(client) is bool isAlive && isAlive;
			}
			catch
			{
				return false;
			}
		}

		private static Exception? ExtractException(EventArgs eventArgs)
		{
			var argsType = eventArgs.GetType();
			var messageProperty = argsType.GetProperty("Message", BindingFlags.Instance | BindingFlags.Public);
			var message = messageProperty?.GetValue(eventArgs) as string;

			var exceptionProperty = argsType.GetProperty("Exception", BindingFlags.Instance | BindingFlags.Public);
			if (exceptionProperty != null && exceptionProperty.GetValue(eventArgs) is Exception ex)
			{
				if (!string.IsNullOrWhiteSpace(message) && !string.Equals(ex.Message, message, StringComparison.Ordinal))
				{
					return new Exception($"{ex.Message} | websocket-sharp={message}", ex);
				}

				return ex;
			}

			if (!string.IsNullOrWhiteSpace(message))
			{
				return new Exception($"websocket-sharp OnError: {message}");
			}

			return new Exception("websocket-sharp OnError without exception/message.");
		}

		private static bool TryExtractBinaryFrame(EventArgs eventArgs, out byte[] payload)
		{
			payload = Array.Empty<byte>();
			var argsType = eventArgs.GetType();
			var binaryProperties = _binaryFramePropertiesCache.GetOrAdd(argsType, type => new BinaryFrameProperties(type));
			if (!(binaryProperties.IsBinaryProperty?.GetValue(eventArgs) is bool isBinary) || !isBinary)
			{
				return false;
			}

			if (!(binaryProperties.RawDataProperty?.GetValue(eventArgs) is byte[] rawData) || rawData.Length == 0)
			{
				return false;
			}

			payload = rawData;
			return true;
		}

		private void ResetClientReflectionState()
		{
			_readyStateProperty = null;
			_isAliveProperty = null;
			_originProperty = null;
			_customHeadersProperty = null;
			_connectAsyncMethod = null;
			_connectMethod = null;
			_closeMethod = null;
			_sendBinaryMethod = null;
			_disposeMethod = null;
			_openedEvent = null;
			_closedEvent = null;
			_errorEvent = null;
			_messageEvent = null;
			_openedHandler = null;
			_closedHandler = null;
			_errorHandler = null;
			_messageHandler = null;
		}
	}
}
