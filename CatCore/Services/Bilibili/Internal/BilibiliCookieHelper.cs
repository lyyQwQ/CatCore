using System;
using System.Collections.Generic;
using System.Linq;

namespace CatCore.Services.Bilibili.Internal
{
	internal static class BilibiliCookieHelper
	{
		private static readonly string[] PYTHON_COOKIE_ORDER =
		{
			"SESSDATA",
			"buvid3",
			"b_nut",
			"bili_jct",
			"DedeUserID",
			"DedeUserID__ckMd5",
			"sid"
		};

		public static string StandardizeCookieOrder(string cookies)
		{
			if (string.IsNullOrWhiteSpace(cookies))
			{
				return string.Empty;
			}

			var parsed = ParseCookieString(cookies);
			var ordered = new List<string>(parsed.Count);

			foreach (var key in PYTHON_COOKIE_ORDER)
			{
				if (parsed.TryGetValue(key, out var value))
				{
					ordered.Add($"{key}={value}");
					parsed.Remove(key);
				}
			}

			foreach (var kvp in parsed)
			{
				if (kvp.Key.Equals("opus-goback", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				ordered.Add($"{kvp.Key}={kvp.Value}");
			}

			return string.Join("; ", ordered);
		}

		public static string GetCookieValue(string cookies, string name)
		{
			if (string.IsNullOrWhiteSpace(cookies) || string.IsNullOrWhiteSpace(name))
			{
				return string.Empty;
			}

			var parsed = ParseCookieString(cookies);
			return parsed.TryGetValue(name, out var value) ? value : string.Empty;
		}

		public static Dictionary<string, string> ParseCookieString(string cookies)
		{
			var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrWhiteSpace(cookies))
			{
				return parsed;
			}

			foreach (var rawPair in cookies.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var pair = rawPair.Trim();
				if (pair.Length == 0)
				{
					continue;
				}

				var equalsAt = pair.IndexOf('=');
				if (equalsAt <= 0 || equalsAt == pair.Length - 1)
				{
					continue;
				}

				var key = pair.Substring(0, equalsAt).Trim();
				var value = pair.Substring(equalsAt + 1).Trim();
				if (IsCookieAttributeKey(key))
				{
					continue;
				}

				parsed[key] = value;
			}

			return parsed;
		}

		private static bool IsCookieAttributeKey(string key)
		{
			return key.Equals("Path", StringComparison.OrdinalIgnoreCase)
				|| key.Equals("Domain", StringComparison.OrdinalIgnoreCase)
				|| key.Equals("Expires", StringComparison.OrdinalIgnoreCase)
				|| key.Equals("Max-Age", StringComparison.OrdinalIgnoreCase)
				|| key.Equals("Secure", StringComparison.OrdinalIgnoreCase)
				|| key.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase)
				|| key.Equals("SameSite", StringComparison.OrdinalIgnoreCase);
		}
	}
}
