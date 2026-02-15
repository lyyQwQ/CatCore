using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CatCore.Services.Bilibili.Internal
{
	internal static class BilibiliAuthHttpClient
	{
		public const string BilibiliUserAgent =
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

		private static readonly HttpClient SHARED_CLIENT = CreateHttpClient();

		public static async Task<(bool Ok, string Body)> GetAsync(string url, string cookies = "", CancellationToken cancellationToken = default)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			ApplyCommonHeaders(request, cookies);

			using var response = await SHARED_CLIENT.SendAsync(request, cancellationToken).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			return (response.StatusCode == HttpStatusCode.OK, body);
		}

		public static async Task<(bool Ok, string Body)> PostAsync(string url, string body, string cookies = "", CancellationToken cancellationToken = default)
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, url);
			ApplyCommonHeaders(request, cookies);
			request.Content = new StringContent(body ?? string.Empty);
			request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

			using var response = await SHARED_CLIENT.SendAsync(request, cancellationToken).ConfigureAwait(false);
			var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			return (response.StatusCode == HttpStatusCode.OK, responseBody);
		}

		private static void ApplyCommonHeaders(HttpRequestMessage request, string cookies)
		{
			request.Version = new Version(1, 1);
			request.Headers.TryAddWithoutValidation("User-Agent", BilibiliUserAgent);
			request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
			request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
			request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
			request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
			request.Headers.TryAddWithoutValidation("Connection", "close");

			var standardizedCookies = BilibiliCookieHelper.StandardizeCookieOrder(cookies);
			if (!string.IsNullOrWhiteSpace(standardizedCookies))
			{
				request.Headers.TryAddWithoutValidation("Cookie", standardizedCookies);
			}
		}

		private static HttpClient CreateHttpClient()
		{
			var handler = new HttpClientHandler
			{
				UseCookies = false,
				AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
#if !RELEASE
				,
				Proxy = Helpers.SharedProxyProvider.PROXY
#endif
			};

			var client = new HttpClient(handler)
			{
				Timeout = TimeSpan.FromSeconds(12)
			};
			return client;
		}
	}
}
