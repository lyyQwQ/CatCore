using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CatCore.Models.Config;
using CatCore.Models.Api.Requests;
using CatCore.Models.Api.Responses;
using CatCore.Services.Interfaces;
using CatCore.Services.Overlay;
using CatCore.Services.Twitch.Interfaces;
using Serilog;

namespace CatCore.Services
{
	internal sealed class KittenApiService : IKittenApiService, IDisposable
	{
		private readonly ILogger _logger;
		private readonly IKittenSettingsService _settingsService;
		private readonly ITwitchAuthService _twitchAuthService;
		private readonly ITwitchChannelManagementService _twitchChannelManagementService;
		private readonly ITwitchHelixApiService _helixApiService;
		private readonly IOverlayWebSocketService _overlayWebSocketService;
		private readonly Version _libraryVersion;
		private static readonly HttpClient BilibiliHttpClient = new HttpClient();
		private static readonly Dictionary<string, string> StaticContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			[".html"] = "text/html",
			[".css"] = "text/css",
			[".js"] = "application/javascript",
			[".json"] = "application/json",
			[".png"] = "image/png",
			[".jpg"] = "image/jpeg",
			[".jpeg"] = "image/jpeg",
			[".bmp"] = "image/bmp",
			[".gif"] = "image/gif",
			[".webp"] = "image/webp",
			[".svg"] = "image/svg+xml",
			[".woff2"] = "font/woff2"
		};
		private readonly object _bilibiliQrStateLock = new object();
		private string? _bilibiliQrKey;
		private string? _bilibiliQrUrl;
		private string _bilibiliQrStatus = "Idle";

		private HttpListener? _listener;
		private CancellationTokenSource? _listenerCancellationTokenSource;
		private string? _webSitePage;
		private string? _overlayPage;

		public KittenApiService(ILogger logger, IKittenSettingsService settingsService, ITwitchAuthService twitchAuthService, ITwitchChannelManagementService twitchChannelManagementService,
			ITwitchHelixApiService helixApiService, IOverlayWebSocketService overlayWebSocketService, Version libraryVersion)
		{
			_logger = logger;
			_settingsService = settingsService;
			_twitchAuthService = twitchAuthService;
			_twitchChannelManagementService = twitchChannelManagementService;
			_helixApiService = helixApiService;
			_overlayWebSocketService = overlayWebSocketService;
			_libraryVersion = libraryVersion;
		}

		public async Task Initialize()
		{
			if (_webSitePage == null)
			{
				using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream($"{nameof(CatCore)}.Resources.index.html")!);
				var pageBuilder = new StringBuilder(await reader.ReadToEndAsync().ConfigureAwait(false));
				pageBuilder.Replace("{libVersion}", _libraryVersion.ToString(3));
				_webSitePage = pageBuilder.ToString();
			}

			if (_overlayPage == null)
			{
				using var overlayReader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream($"{nameof(CatCore)}.Resources.overlay.html")!);
				_overlayPage = await overlayReader.ReadToEndAsync().ConfigureAwait(false);
			}

			_logger.Information("Purring up internal webserver");

			_listener = new HttpListener {Prefixes = {ConstantsBase.InternalApiServerUri}};

			try
			{
				_listener.Start();
				_listenerCancellationTokenSource = new CancellationTokenSource();
				_overlayWebSocketService.Start(new Uri(ConstantsBase.InternalApiServerUri));

				_ = Task.Run(async () =>
				{
					while (_listenerCancellationTokenSource != null && !_listenerCancellationTokenSource.IsCancellationRequested)
					{
						try
						{
							var context = await _listener.GetContextAsync().ConfigureAwait(false);
							await HandleContext(context).ConfigureAwait(false);
						}
						catch (HttpListenerException) when (_listenerCancellationTokenSource != null && _listenerCancellationTokenSource.IsCancellationRequested)
						{
							break;
						}
						catch (ObjectDisposedException) when (_listenerCancellationTokenSource != null && _listenerCancellationTokenSource.IsCancellationRequested)
						{
							break;
						}
						catch (Exception e)
						{
							_logger.Error(e, "An error occured while trying to handle an incoming request");
						}
					}

					// ReSharper disable once FunctionNeverReturns
				});

				_logger.Information("Internal webserver has been purred up");
			}
			catch (Exception e)
			{
				_overlayWebSocketService.Stop();
				_logger.Error(e, "The portal webpage is most likely not available because an error occurred while trying to purr up the internal webserver");
			}
		}

		public void Dispose()
		{
			try
			{
				_listenerCancellationTokenSource?.Cancel();
			}
			catch
			{
				// ignored
			}

			try
			{
				_listener?.Close();
			}
			catch
			{
				// ignored
			}

			_listener = null;

			if (_listenerCancellationTokenSource != null)
			{
				_listenerCancellationTokenSource.Dispose();
				_listenerCancellationTokenSource = null;
			}

			_overlayWebSocketService.Stop();
		}

		private async Task HandleContext(HttpListenerContext ctx)
		{
			var request = ctx.Request;
			using var response = ctx.Response;
			try
			{
				var requestHandled = false;

#if DEBUG
				_logger.Debug("New incoming request {Method} {RequestUrl}", request.HttpMethod, request.Url.AbsoluteUri);
#endif

				if (request.Url.AbsolutePath.StartsWith("/api"))
				{
					requestHandled = await HandleApiRequest(request, response).ConfigureAwait(false);
				}
				else if (request.Url.AbsolutePath == "/" && request.HttpMethod == "GET" && request.Url.Query.StartsWith("?url=", StringComparison.OrdinalIgnoreCase))
				{
					requestHandled = await HandleImageProxyRequest(request, response).ConfigureAwait(false);
				}
				else if (request.Url.AbsolutePath == "/" && request.HttpMethod == "GET")
				{
					var data = Encoding.UTF8.GetBytes(_webSitePage!);
					response.ContentEncoding = Encoding.UTF8;
					response.ContentLength64 = data.Length;
					response.ContentType = "text/html";
					await response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);

					requestHandled = true;
				}
				else if ((request.Url.AbsolutePath == "/overlay" || request.Url.AbsolutePath == "/overlay/") && request.HttpMethod == "GET")
				{
					var bilibiliConfig = _settingsService.Config.BilibiliConfig;
					var overlayConfig = JsonSerializer.Serialize(BuildOverlayConfigData(bilibiliConfig));
					var overlayPage = _overlayPage!.Replace("var config_data = {};", $"var config_data = {overlayConfig};");
					var data = Encoding.UTF8.GetBytes(overlayPage);
					response.ContentEncoding = Encoding.UTF8;
					response.ContentLength64 = data.Length;
					response.ContentType = "text/html";
					await response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);

					requestHandled = true;
				}
				else if (request.Url.AbsolutePath.Equals("/overlay/custom.js", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
				{
					response.ContentType = "application/javascript";
					response.ContentLength64 = 0;
					requestHandled = true;
				}
				else if (request.Url.AbsolutePath.Equals("/overlay/custom.css", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
				{
					response.ContentType = "text/css";
					response.ContentLength64 = 0;
					requestHandled = true;
				}
				else if (request.Url.AbsolutePath.StartsWith("/Statics/", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
				{
					requestHandled = await HandleStaticRequest(request, response).ConfigureAwait(false);
				}

				if (!requestHandled)
				{
					_logger.Warning("{Method} {RequestUrl} went unhandled", request.HttpMethod, request.Url.AbsoluteUri);

					response.StatusCode = 404;
				}
#if DEBUG
				else
				{
					_logger.Debug("Successfully handled {Method} {RequestUrl}", request.HttpMethod, request.Url.AbsoluteUri);
				}
#endif
			}
			catch (Exception e)
			{
				_logger.Error(e, "Something went wrong while trying to handle an incoming request");
				response.StatusCode = 500;
			}
		}

		private Task<bool> HandleApiRequest(HttpListenerRequest request, HttpListenerResponse response)
		{
			return request.Url.Segments.ElementAtOrDefault(2) switch
			{
				"twitch/" => HandleTwitchApiRequests(request, response),
				"bilibili/" => HandleBilibiliApiRequests(request, response),
				"global/" => HandleGlobalApiRequest(request, response),
				_ => Task.FromResult(false)
			};
		}

		private async Task<bool> HandleStaticRequest(HttpListenerRequest request, HttpListenerResponse response)
		{
			var resourceName = GetResourceNameFromPath(request.Url.AbsolutePath);
			using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
			if (resourceStream == null)
			{
				response.StatusCode = 404;
				return true;
			}

			response.ContentType = GetContentType(request.Url.AbsolutePath);
			response.ContentLength64 = resourceStream.Length;
			await resourceStream.CopyToAsync(response.OutputStream).ConfigureAwait(false);
			return true;
		}

		private static string GetResourceNameFromPath(string absolutePath)
		{
			return $"{nameof(CatCore)}.Resources.{absolutePath.TrimStart('/').Replace('/', '.')}";
		}

		private static string GetContentType(string absolutePath)
		{
			var extension = Path.GetExtension(absolutePath);
			if (!string.IsNullOrWhiteSpace(extension) && StaticContentTypes.TryGetValue(extension, out var contentType))
			{
				return contentType;
			}

			return "application/octet-stream";
		}

		private static Dictionary<string, object> BuildOverlayConfigData(BilibiliConfig bilibiliConfig)
		{
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				["overlay_tts_enable"] = bilibiliConfig.OverlayTtsEnable,
				["overlay_tts_voice_package"] = bilibiliConfig.OverlayTtsVoicePackage,
				["overlay_tts_voice_speed"] = bilibiliConfig.OverlayTtsVoiceSpeed,
				["overlay_tts_voice_pitch"] = bilibiliConfig.OverlayTtsVoicePitch,
				["overlay_show_init_welcome"] = bilibiliConfig.OverlayShowInitWelcome,
				["overlay_show_username"] = bilibiliConfig.OverlayShowUsername,
				["overlay_show_gift_in_sc"] = bilibiliConfig.OverlayShowGiftInSc,
				["overlay_show_guard_in_sc"] = bilibiliConfig.OverlayShowGuardInSc
			};
		}

		private static bool TryExtractProxyTargetUrl(HttpListenerRequest request, out string targetUrl)
		{
			targetUrl = string.Empty;
			if (request.Url == null)
			{
				return false;
			}

			const string prefix = "?url=";
			if (!request.Url.Query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			targetUrl = WebUtility.UrlDecode(request.Url.Query.Substring(prefix.Length));
			return !string.IsNullOrWhiteSpace(targetUrl);
		}

		private async Task<bool> HandleImageProxyRequest(HttpListenerRequest request, HttpListenerResponse response)
		{
			if (!TryExtractProxyTargetUrl(request, out var targetUrl) ||
				!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri) ||
				(targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
			{
				response.StatusCode = 400;
				return true;
			}

			try
			{
				using var upstreamResponse = await BilibiliHttpClient.GetAsync(targetUri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
				if (!upstreamResponse.IsSuccessStatusCode)
				{
					response.StatusCode = 502;
					return true;
				}

				response.ContentType = upstreamResponse.Content.Headers.ContentType?.MediaType ?? GetContentType(targetUri.AbsolutePath);
				if (upstreamResponse.Content.Headers.ContentLength.HasValue)
				{
					response.ContentLength64 = upstreamResponse.Content.Headers.ContentLength.Value;
				}

				using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
				await upstreamStream.CopyToAsync(response.OutputStream).ConfigureAwait(false);
				return true;
			}
			catch (Exception e)
			{
				_logger.Warning(e, "Image proxy request failed for {TargetUrl}", targetUrl);
				response.StatusCode = 502;
				return true;
			}
		}

		// ReSharper disable once CognitiveComplexity
		private async Task<bool> HandleTwitchApiRequests(HttpListenerRequest request, HttpListenerResponse response)
		{
			switch (request.Url.Segments.ElementAtOrDefault(3))
			{
				case "login" when request.HttpMethod == "GET":
					response.Redirect(_twitchAuthService.AuthorizationUrl($"{request.Url.GetLeftPart(UriPartial.Authority)}/api/twitch/authcode_callback"));
					return true;
				case "authcode_callback" when request.HttpMethod == "GET":
					string? code = null;
					foreach (var parameterPair in request.Url.Query.Substring(1).Split(new[] {'&'}, StringSplitOptions.RemoveEmptyEntries))
					{
						var kvp = parameterPair.Split('=');
						if (kvp[0] == "code")
						{
							code = kvp[1];
							break;
						}
					}

					if (code != null)
					{
						await _twitchAuthService.GetTokensByAuthorizationCode(code, request.Url.GetLeftPart(UriPartial.Path)).ConfigureAwait(false);
					}

					response.Redirect(request.Url.GetLeftPart(UriPartial.Authority));

					return true;
				case "logout" when request.HttpMethod == "GET":
					await _twitchAuthService.RevokeTokens().ConfigureAwait(false);

					response.Redirect(request.Url.GetLeftPart(UriPartial.Authority));

					return true;
				case "state" when request.HttpMethod == "GET":
					response.ContentEncoding = Encoding.UTF8;
					response.ContentType = "application/json";

					var loggedInUserInfo = await _twitchAuthService.FetchLoggedInUserInfoWithRefresh().ConfigureAwait(false);
					var userInfos = loggedInUserInfo != null
						? await _twitchChannelManagementService.GetAllChannelsEnriched().ConfigureAwait(false)
						: null;

					await JsonSerializer
						.SerializeAsync(response.OutputStream, new TwitchStateResponseDto(_twitchAuthService.TokenIsValid, loggedInUserInfo, userInfos, _settingsService.Config.TwitchConfig))
						.ConfigureAwait(false);

					return true;
				case "state" when request.HttpMethod == "POST":
					var twitchStateRequestDto = await JsonSerializer.DeserializeAsync<TwitchStateRequestDto>(request.InputStream).ConfigureAwait(false);
					using (_settingsService.ChangeTransaction())
					{
						var twitchConfig = _settingsService.Config.TwitchConfig;
						twitchConfig.Enabled = twitchStateRequestDto.Enabled;
						if (_twitchAuthService.TokenIsValid)
						{
							_twitchChannelManagementService.UpdateChannels(twitchStateRequestDto.SelfEnabled, twitchStateRequestDto.AdditionalChannelsData);
						}

						twitchConfig.ParseBttvEmotes = twitchStateRequestDto.ParseBttvEmotes;
						twitchConfig.ParseFfzEmotes = twitchStateRequestDto.ParseFfzEmotes;
						twitchConfig.ParseTwitchEmotes = twitchStateRequestDto.ParseTwitchEmotes;
						twitchConfig.ParseCheermotes = twitchStateRequestDto.ParseCheermotes;
					}

					return true;
				case "channels" when request.HttpMethod == "GET":
					var query = request.QueryString["query"];
					var directChannelNameSearch = await _helixApiService.FetchUserInfo(loginNames: new []{query});
					var searchQueryChannels = await _helixApiService.SearchChannels(query).ConfigureAwait(false);

					var channelQueryData = new List<TwitchChannelQueryData>();
					if (directChannelNameSearch != null && directChannelNameSearch.Value.Data.Any())
					{
						channelQueryData.Add(new TwitchChannelQueryData(directChannelNameSearch.Value.Data.First()));
					}

					if (searchQueryChannels != null)
					{
						channelQueryData.AddRange(searchQueryChannels.Value.Data
							.Select(channelData => new TwitchChannelQueryData(channelData))
							.Except(channelQueryData)
							.OrderBy(x => x.DisplayName.Length)
							.ThenBy(x => x.DisplayName));
					}

					response.ContentEncoding = Encoding.UTF8;
					response.ContentType = "application/json";
					await JsonSerializer.SerializeAsync(response.OutputStream, channelQueryData).ConfigureAwait(false);

					return true;
				default:
					return false;
			}
		}

		private async Task<bool> HandleGlobalApiRequest(HttpListenerRequest request, HttpListenerResponse response)
		{
			switch (request.Url.Segments.ElementAtOrDefault(3))
			{
				case "state" when request.HttpMethod == "GET":
					response.ContentEncoding = Encoding.UTF8;
					response.ContentType = "application/json";
					await JsonSerializer.SerializeAsync(response.OutputStream, new GlobalStateResponseDto(_settingsService.Config.GlobalConfig)).ConfigureAwait(false);

					return true;
				case "state" when request.HttpMethod == "POST":
					var globalStateRequestDto = await JsonSerializer.DeserializeAsync<GlobalStateRequestDto>(request.InputStream).ConfigureAwait(false);
					using (_settingsService.ChangeTransaction())
					{
						var globalConfig = _settingsService.Config.GlobalConfig;
						globalConfig.LaunchInternalApiOnStartup = globalStateRequestDto.LaunchInternalApiOnStartup;
						globalConfig.LaunchWebPortalOnStartup = globalStateRequestDto.LaunchWebPortalOnStartup;
						globalConfig.HandleEmojis = globalStateRequestDto.ParseEmojis;
					}

					return true;
				default:
					return false;
			}
		}

		private async Task<bool> HandleBilibiliApiRequests(HttpListenerRequest request, HttpListenerResponse response)
		{
			switch (request.Url.Segments.ElementAtOrDefault(3))
			{
				case "qr_request" when request.HttpMethod == "GET":
					response.ContentEncoding = Encoding.UTF8;
					response.ContentType = "application/json";

					var (url, status) = await RequestBilibiliQrLogin().ConfigureAwait(false);
					if (string.IsNullOrWhiteSpace(url))
					{
						lock (_bilibiliQrStateLock)
						{
							url = _bilibiliQrUrl ?? string.Empty;
							status = _bilibiliQrStatus;
						}
					}

					await JsonSerializer.SerializeAsync(response.OutputStream, new {url, status}).ConfigureAwait(false);

					return true;
				case "qr_status" when request.HttpMethod == "GET":
					response.ContentEncoding = Encoding.UTF8;
					response.ContentType = "application/json";

					var (pollStatus, cookies) = await PollBilibiliQrLoginStatus().ConfigureAwait(false);
					await JsonSerializer.SerializeAsync(response.OutputStream, new {status = pollStatus, cookies}).ConfigureAwait(false);

					return true;
				case "state" when request.HttpMethod == "GET":
					response.ContentEncoding = Encoding.UTF8;
					response.ContentType = "application/json";
					await JsonSerializer.SerializeAsync(response.OutputStream, _settingsService.Config.BilibiliConfig).ConfigureAwait(false);

					return true;
				case "state" when request.HttpMethod == "POST":
					var bilibiliStateRequest = await JsonSerializer.DeserializeAsync<BilibiliConfig>(request.InputStream).ConfigureAwait(false);
					if (bilibiliStateRequest == null)
					{
						response.StatusCode = 400;
						return true;
					}

					using (_settingsService.ChangeTransaction())
					{
						var bilibiliConfig = _settingsService.Config.BilibiliConfig;
						bilibiliConfig.Enabled = bilibiliStateRequest.Enabled;
						bilibiliConfig.RoomId = bilibiliStateRequest.RoomId;
						bilibiliConfig.Cookies = bilibiliStateRequest.Cookies ?? string.Empty;
						bilibiliConfig.OverlayTtsEnable = bilibiliStateRequest.OverlayTtsEnable;
						bilibiliConfig.OverlayTtsVoicePackage = bilibiliStateRequest.OverlayTtsVoicePackage ?? string.Empty;
						bilibiliConfig.OverlayTtsVoiceSpeed = bilibiliStateRequest.OverlayTtsVoiceSpeed;
						bilibiliConfig.OverlayTtsVoicePitch = bilibiliStateRequest.OverlayTtsVoicePitch;
						bilibiliConfig.OverlayShowInitWelcome = bilibiliStateRequest.OverlayShowInitWelcome;
						bilibiliConfig.OverlayShowUsername = bilibiliStateRequest.OverlayShowUsername;
						bilibiliConfig.OverlayShowGiftInSc = bilibiliStateRequest.OverlayShowGiftInSc;
						bilibiliConfig.OverlayShowGuardInSc = bilibiliStateRequest.OverlayShowGuardInSc;
					}

					return true;
				default:
					return false;
			}
		}

		private async Task<(string Url, string Status)> RequestBilibiliQrLogin()
		{
			try
			{
				using var response = await BilibiliHttpClient
					.GetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate")
					.ConfigureAwait(false);
				response.EnsureSuccessStatusCode();

				using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
				using var jsonDocument = await JsonDocument.ParseAsync(contentStream).ConfigureAwait(false);

				var root = jsonDocument.RootElement;
				var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
				if (code != 0 || !root.TryGetProperty("data", out var dataElement))
				{
					const string failedStatus = "Failed to request QR login.";
					lock (_bilibiliQrStateLock)
					{
						_bilibiliQrStatus = failedStatus;
					}

					return (string.Empty, failedStatus);
				}

				var qrLoginUrl = dataElement.TryGetProperty("url", out var qrUrlElement)
					? qrUrlElement.GetString() ?? string.Empty
					: string.Empty;
				var qrCodeKey = dataElement.TryGetProperty("qrcode_key", out var qrKeyElement)
					? qrKeyElement.GetString() ?? string.Empty
					: string.Empty;

				if (string.IsNullOrWhiteSpace(qrLoginUrl) || string.IsNullOrWhiteSpace(qrCodeKey))
				{
					const string failedStatus = "Failed to request QR login.";
					lock (_bilibiliQrStateLock)
					{
						_bilibiliQrKey = null;
						_bilibiliQrUrl = null;
						_bilibiliQrStatus = failedStatus;
					}

					return (string.Empty, failedStatus);
				}

				const string pendingStatus = "Please scan and confirm the QR login.";
				lock (_bilibiliQrStateLock)
				{
					_bilibiliQrKey = qrCodeKey;
					_bilibiliQrUrl = qrLoginUrl;
					_bilibiliQrStatus = pendingStatus;
				}

				return (qrLoginUrl, pendingStatus);
			}
			catch (Exception e)
			{
				_logger.Warning(e, "Failed to request Bilibili QR login");
				const string failedStatus = "Failed to request QR login.";
				lock (_bilibiliQrStateLock)
				{
					_bilibiliQrKey = null;
					_bilibiliQrUrl = null;
					_bilibiliQrStatus = failedStatus;
				}

				return (string.Empty, failedStatus);
			}
		}

		private async Task<(string Status, string Cookies)> PollBilibiliQrLoginStatus()
		{
			string? qrCodeKey;
			lock (_bilibiliQrStateLock)
			{
				qrCodeKey = _bilibiliQrKey;
			}

			if (string.IsNullOrWhiteSpace(qrCodeKey))
			{
				lock (_bilibiliQrStateLock)
				{
					return (string.IsNullOrWhiteSpace(_bilibiliQrStatus) ? "No active QR request." : _bilibiliQrStatus, string.Empty);
				}
			}

			try
			{
				using var response = await BilibiliHttpClient
					.GetAsync($"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={WebUtility.UrlEncode(qrCodeKey)}")
					.ConfigureAwait(false);
				response.EnsureSuccessStatusCode();

				using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
				using var jsonDocument = await JsonDocument.ParseAsync(contentStream).ConfigureAwait(false);

				var root = jsonDocument.RootElement;
				var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
				if (code != 0 || !root.TryGetProperty("data", out var dataElement))
				{
					const string failedStatus = "QR polling failed.";
					lock (_bilibiliQrStateLock)
					{
						_bilibiliQrStatus = failedStatus;
					}

					return (failedStatus, string.Empty);
				}

				var pollCode = dataElement.TryGetProperty("code", out var pollCodeElement) ? pollCodeElement.GetInt32() : -1;
				switch (pollCode)
				{
					case 86101:
						return UpdateBilibiliQrStatus("Waiting for QR scan.", string.Empty);
					case 86090:
						return UpdateBilibiliQrStatus("Scanned. Confirm login in Bilibili app.", string.Empty);
					case 86038:
						lock (_bilibiliQrStateLock)
						{
							_bilibiliQrKey = null;
							_bilibiliQrUrl = null;
						}

						return UpdateBilibiliQrStatus("QR expired. Request a new one.", string.Empty);
					case 0:
						var redirectUrl = dataElement.TryGetProperty("url", out var redirectUrlElement)
							? redirectUrlElement.GetString() ?? string.Empty
							: string.Empty;
						var cookies = BuildBilibiliCookieString(redirectUrl);

						if (!string.IsNullOrWhiteSpace(cookies))
						{
							using (_settingsService.ChangeTransaction())
							{
								_settingsService.Config.BilibiliConfig.Cookies = cookies;
							}
						}

						lock (_bilibiliQrStateLock)
						{
							_bilibiliQrKey = null;
							_bilibiliQrUrl = null;
						}

						return UpdateBilibiliQrStatus("QR login succeeded.", cookies);
					default:
						return UpdateBilibiliQrStatus($"Unhandled QR status code: {pollCode}", string.Empty);
				}
			}
			catch (Exception e)
			{
				_logger.Warning(e, "Failed to poll Bilibili QR login status");
				return UpdateBilibiliQrStatus("QR polling failed.", string.Empty);
			}
		}

		private (string Status, string Cookies) UpdateBilibiliQrStatus(string status, string cookies)
		{
			lock (_bilibiliQrStateLock)
			{
				_bilibiliQrStatus = status;
			}

			return (status, cookies);
		}

		private static string BuildBilibiliCookieString(string callbackUrl)
		{
			if (string.IsNullOrWhiteSpace(callbackUrl) || !Uri.TryCreate(callbackUrl, UriKind.Absolute, out var parsedUri))
			{
				return string.Empty;
			}

			var query = parsedUri.Query;
			if (string.IsNullOrWhiteSpace(query))
			{
				return string.Empty;
			}

			var cookieKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"SESSDATA",
				"bili_jct",
				"DedeUserID",
				"DedeUserID__ckMd5",
				"sid",
				"buvid3"
			};

			var cookieParts = new List<string>();
			foreach (var parameterPair in query.TrimStart('?').Split(new[] {'&'}, StringSplitOptions.RemoveEmptyEntries))
			{
				var separatorIndex = parameterPair.IndexOf('=');
				if (separatorIndex < 1)
				{
					continue;
				}

				var key = WebUtility.UrlDecode(parameterPair.Substring(0, separatorIndex));
				if (!cookieKeys.Contains(key))
				{
					continue;
				}

				var value = WebUtility.UrlDecode(parameterPair.Substring(separatorIndex + 1));
				if (string.IsNullOrWhiteSpace(value))
				{
					continue;
				}

				cookieParts.Add($"{key}={value}");
			}

			return string.Join("; ", cookieParts.Distinct(StringComparer.OrdinalIgnoreCase));
		}
	}
}
