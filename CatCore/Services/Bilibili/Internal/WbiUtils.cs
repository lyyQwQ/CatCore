using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CatCore.Services.Bilibili.Internal
{
	internal static class WbiUtils
	{
		private static readonly int[] MIXIN_KEY_ENC_TAB =
		{
			46, 47, 18, 2, 53, 8, 23, 32, 15, 50,
			10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
			33, 9, 42, 19, 29, 28, 14, 39, 12, 38,
			41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
			26, 17, 0, 1, 60, 51, 30, 4, 22, 25,
			54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
			20, 34, 44, 52
		};

		private static readonly TimeSpan UPDATE_INTERVAL = TimeSpan.FromMinutes(30);
		private static readonly TimeSpan FAILURE_COOLDOWN = TimeSpan.FromMinutes(10);
		private static readonly SemaphoreSlim ENSURE_GATE = new(1, 1);
		private static readonly HttpClient SHARED_CLIENT = CreateHttpClient();

		private static string _imgKey = string.Empty;
		private static string _subKey = string.Empty;
		private static DateTime _lastUpdateTimeUtc = DateTime.MinValue;
		private static int _consecutiveFailures;
		private static DateTime _cooldownUntilUtc = DateTime.MinValue;

		public static void ResetFailureState()
		{
			_consecutiveFailures = 0;
			_cooldownUntilUtc = DateTime.MinValue;
		}

		public static void ClearCache()
		{
			_imgKey = string.Empty;
			_subKey = string.Empty;
			_lastUpdateTimeUtc = DateTime.MinValue;
		}

		public static async Task<Dictionary<string, string>> SignParametersAsync(Dictionary<string, string> parameters, string cookies, CancellationToken cancellationToken = default)
		{
			if (parameters == null)
			{
				throw new ArgumentNullException(nameof(parameters));
			}

			if (!await EnsureWbiKeysAsync(cookies, cancellationToken).ConfigureAwait(false))
			{
				throw new InvalidOperationException("WBI keys unavailable");
			}

			var signedParams = new Dictionary<string, string>(parameters, StringComparer.Ordinal)
			{
				["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
			};

			if (!signedParams.ContainsKey("web_location"))
			{
				signedParams["web_location"] = "444.8";
			}

			var mixinKey = GetMixinKey();
			if (mixinKey.Length == 0)
			{
				throw new InvalidOperationException("WBI mixin key unavailable");
			}

			var sortedPairs = signedParams
				.Where(kv => !string.Equals(kv.Key, "w_rid", StringComparison.Ordinal))
				.OrderBy(kv => kv.Key, StringComparer.Ordinal)
				.ToArray();

			var queryBuilder = new StringBuilder();
			for (var i = 0; i < sortedPairs.Length; i++)
			{
				if (i > 0)
				{
					queryBuilder.Append('&');
				}

				queryBuilder.Append(sortedPairs[i].Key);
				queryBuilder.Append('=');
				queryBuilder.Append(UrlEncode(sortedPairs[i].Value));
			}

			var signInput = queryBuilder.ToString() + mixinKey;
			using var md5 = MD5.Create();
			var wRid = ToHex(md5.ComputeHash(Encoding.UTF8.GetBytes(signInput)));
			signedParams["w_rid"] = wRid;
			return signedParams;
		}

		private static async Task<bool> EnsureWbiKeysAsync(string cookies, CancellationToken cancellationToken)
		{
			var nowUtc = DateTime.UtcNow;
			if (HasFreshKeys(nowUtc))
			{
				return true;
			}

			if (nowUtc < _cooldownUntilUtc)
			{
				return HasAnyKeys();
			}

			await ENSURE_GATE.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				nowUtc = DateTime.UtcNow;
				if (HasFreshKeys(nowUtc))
				{
					return true;
				}

				if (nowUtc < _cooldownUntilUtc)
				{
					return HasAnyKeys();
				}

				var updated = await TryUpdateKeysFromNavAsync(cookies, true, cancellationToken).ConfigureAwait(false);
				if (updated)
				{
					_consecutiveFailures = 0;
					_cooldownUntilUtc = DateTime.MinValue;
					return true;
				}

				_consecutiveFailures++;
				if (_consecutiveFailures >= 5)
				{
					_consecutiveFailures = 0;
					_cooldownUntilUtc = nowUtc + FAILURE_COOLDOWN;
				}

				return HasAnyKeys();
			}
			finally
			{
				ENSURE_GATE.Release();
			}
		}

		private static async Task<bool> TryUpdateKeysFromNavAsync(string cookies, bool allowGuestFallback, CancellationToken cancellationToken)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
			request.Headers.TryAddWithoutValidation("User-Agent", BilibiliAuthHttpClient.BilibiliUserAgent);
			request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
			request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
			request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
			request.Headers.TryAddWithoutValidation("Connection", "close");

			var standardizedCookies = BilibiliCookieHelper.StandardizeCookieOrder(cookies);
			if (!string.IsNullOrWhiteSpace(standardizedCookies))
			{
				request.Headers.TryAddWithoutValidation("Cookie", standardizedCookies);
			}

			using var response = await SHARED_CLIENT.SendAsync(request, cancellationToken).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			if (!TryReadNavResult(body, out var code, out var imgKey, out var subKey))
			{
				return false;
			}

			if (code == 0 && imgKey.Length > 0 && subKey.Length > 0)
			{
				_imgKey = imgKey;
				_subKey = subKey;
				_lastUpdateTimeUtc = DateTime.UtcNow;
				return true;
			}

			if (code == -101 && allowGuestFallback && !string.IsNullOrWhiteSpace(cookies))
			{
				return await TryUpdateKeysFromNavAsync(string.Empty, false, cancellationToken).ConfigureAwait(false);
			}

			return false;
		}

		private static bool TryReadNavResult(string body, out int code, out string imgKey, out string subKey)
		{
			code = -1;
			imgKey = string.Empty;
			subKey = string.Empty;

			try
			{
				using var document = JsonDocument.Parse(body);
				var root = document.RootElement;
				if (!root.TryGetProperty("code", out var codeNode))
				{
					return false;
				}

				code = codeNode.ValueKind == JsonValueKind.Number ? codeNode.GetInt32() : int.Parse(codeNode.GetRawText(), CultureInfo.InvariantCulture);
				if (code != 0)
				{
					return true;
				}

				if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
				{
					return false;
				}

				if (!dataNode.TryGetProperty("wbi_img", out var wbiNode) || wbiNode.ValueKind != JsonValueKind.Object)
				{
					return false;
				}

				var imgUrl = wbiNode.TryGetProperty("img_url", out var imgUrlNode) ? imgUrlNode.GetString() ?? string.Empty : string.Empty;
				var subUrl = wbiNode.TryGetProperty("sub_url", out var subUrlNode) ? subUrlNode.GetString() ?? string.Empty : string.Empty;

				imgKey = ExtractFileNameWithoutExtension(imgUrl);
				subKey = ExtractFileNameWithoutExtension(subUrl);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static string GetMixinKey()
		{
			if (!HasAnyKeys())
			{
				return string.Empty;
			}

			var source = _imgKey + _subKey;
			if (source.Length == 0)
			{
				return string.Empty;
			}

			var builder = new StringBuilder(64);
			foreach (var index in MIXIN_KEY_ENC_TAB)
			{
				if (index < source.Length)
				{
					builder.Append(source[index]);
				}
			}

			var mixed = builder.ToString();
			return mixed.Length > 32 ? mixed.Substring(0, 32) : mixed;
		}

		private static bool HasAnyKeys()
		{
			return !string.IsNullOrEmpty(_imgKey) && !string.IsNullOrEmpty(_subKey);
		}

		private static bool HasFreshKeys(DateTime nowUtc)
		{
			return HasAnyKeys() && nowUtc - _lastUpdateTimeUtc < UPDATE_INTERVAL;
		}

		private static HttpClient CreateHttpClient()
		{
			var handler = new HttpClientHandler
			{
				UseCookies = false
#if !RELEASE
				,
				Proxy = Helpers.SharedProxyProvider.PROXY
#endif
			};

			var client = new HttpClient(handler)
			{
				Timeout = TimeSpan.FromSeconds(10)
			};
			return client;
		}

		private static string ExtractFileNameWithoutExtension(string url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return string.Empty;
			}

			var slashIndex = url.LastIndexOf('/');
			var fileName = slashIndex >= 0 ? url.Substring(slashIndex + 1) : url;
			var dotIndex = fileName.LastIndexOf('.');
			return dotIndex > 0 ? fileName.Substring(0, dotIndex) : fileName;
		}

		private static string UrlEncode(string value)
		{
			if (value == null)
			{
				return string.Empty;
			}

			return Uri.EscapeDataString(value);
		}

		private static string ToHex(byte[] bytes)
		{
			var builder = new StringBuilder(bytes.Length * 2);
			foreach (var b in bytes)
			{
				builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
			}

			return builder.ToString();
		}
	}
}
