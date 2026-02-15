using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CatCore.Models.Shared;
using CatCore.Svg;
using Serilog;

namespace CatCore.Models.Bilibili
{
	public sealed class BilibiliChatBadge : IChatBadge
	{
		private const int BadgeHeight = 44;
		private const int RenderScale = 3;
		private const int BadgeCacheCapacity = 300;
		private const string GuardResourcePrefix = "CatCore.Resources.Statics.Images.";

		private static readonly Regex ChineseCharRegex = new Regex("^[\u4e00-\u9fa5]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex FullWidthEnglishRegex = new Regex("[WM]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex TwoThirdWidthEnglishRegex = new Regex("[ABCDGHKNOPQRUVXYZbdghkmnopqw023456789]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex HalfWidthEnglishRegex = new Regex("[EFJLSTacesuvxyz]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex QuarterWidthEnglishRegex = new Regex("[Ifijlrt1]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly BadgeUriLruCache BadgeCache = new BadgeUriLruCache(BadgeCacheCapacity);

		private const string SVG_FRAME = @"<?xml version=""1.0"" encoding=""utf-8""?><svg version=""1.1"" id=""Badge"" xmlns=""http://www.w3.org/2000/svg"" xmlns:xlink=""http://www.w3.org/1999/xlink"" viewBox=""0 0 %ImageWidth% 44""><defs><linearGradient id=""a"" gradientTransform=""rotate(45)""><stop offset=""0"" stop-color=""%LinearGradientColorB%""/><stop offset=""1"" stop-color=""%LinearGradientColorA%""/></linearGradient></defs><rect x=""%OFFSET_X%"" y=""%OFFSET_Y%"" rx=""4"" ry=""4"" width=""%WIDTH_1%"" height=""28"" fill=""url(#a)"" stroke=""%BorderColor%"" stroke-width=""2"" paint-order=""stroke fill""/><text x=""%OFFSET_BADGE_NAME_X%"" y=""%OFFSET_BADGE_NAME_Y%"" font-family=""Microsoft YaHei UI Semibold, Microsoft YaHei UI, Microsoft YaHei, sans-serif"" font-size=""22"" fill=""#fff"">%CONTENT%</text><rect x=""%OFFSET_Level_0%"" y=""%OFFSET_Y%"" rx=""4"" ry=""4"" width=""32"" height=""28"" fill=""#fff""/><text x=""%OFFSET_Level_1%"" y=""%OFFSET_BADGE_NAME_Y%"" text-anchor=""middle"" font-family=""Microsoft YaHei UI Semibold, Microsoft YaHei UI, Microsoft YaHei, sans-serif"" font-size=""24"" fill=""%LinearGradientColorA%"">%LEVEL%</text>%GuardImage%</svg>";

		public string Id { get; private set; } = string.Empty;
		public string Name { get; set; } = string.Empty;
		public string Uri { get; private set; } = string.Empty;
		public string Color { get; set; } = string.Empty;
		public string BorderColor { get; private set; } = "#FFFFFF";
		public string LinearGradientColorA { get; private set; } = "#000000";
		public string LinearGradientColorB { get; private set; } = "#FFFFFF";
		public int Level { get; set; }
		public int Guard { get; set; }

		static BilibiliChatBadge()
		{
		#if BADGE_DEBUG
			Log.Information("[BADGE_CACHE] INIT capacity={Capacity}", BadgeCacheCapacity);
		#endif
		}

		public async Task genImage()
		{
			var stopwatch = Stopwatch.StartNew();
			var safeName = Name ?? string.Empty;
			var cacheKey = BuildCacheKey(safeName, Level, Guard);

			if (BadgeCache.TryGet(cacheKey, out var cachedUri))
			{
				Uri = cachedUri;
				Id = cacheKey;
		#if BADGE_DEBUG
				Log.Information("[BADGE_CACHE] HIT key={Key} uri={Uri}", cacheKey, cachedUri);
		#endif
				return;
			}

		#if BADGE_DEBUG
			Log.Information("[BADGE_CACHE] MISS key={Key}", cacheKey);
		#endif

			var offset = Guard == 0 ? new[] { 2, 8 } : new[] { 22, 8 };
			var offsetBadgeName = Guard == 0 ? new[] { 6, 30 } : new[] { 44, 30 };
			var offsetLevel = Guard == 0 ? new[] { 8, 24 } : new[] { 46, 62 };
			var width = Guard == 0 ? new[] { 14, 18 } : new[] { -6, 18 };

			try
			{
				var nameLength = await getNameLengthAsync().ConfigureAwait(false);
				var imageWidth = offsetLevel[1] + width[1] + nameLength;

				var svgBuilder = new StringBuilder(SVG_FRAME);
				svgBuilder.Replace("%ImageWidth%", imageWidth.ToString(CultureInfo.InvariantCulture));
				svgBuilder.Replace("%OFFSET_X%", offset[0].ToString(CultureInfo.InvariantCulture));
				svgBuilder.Replace("%OFFSET_Y%", offset[1].ToString(CultureInfo.InvariantCulture));
				svgBuilder.Replace("%OFFSET_BADGE_NAME_X%", offsetBadgeName[0].ToString(CultureInfo.InvariantCulture));
				svgBuilder.Replace("%OFFSET_BADGE_NAME_Y%", offsetBadgeName[1].ToString(CultureInfo.InvariantCulture));
				svgBuilder.Replace("%OFFSET_Level_0%", (offsetLevel[0] + nameLength).ToString(CultureInfo.InvariantCulture));
				svgBuilder.Replace("%OFFSET_Level_1%", (offsetLevel[1] + nameLength).ToString(CultureInfo.InvariantCulture));
				svgBuilder.Replace("%WIDTH_1%", (offsetLevel[1] + width[0] + nameLength).ToString(CultureInfo.InvariantCulture));
				svgBuilder.Replace("%BorderColor%", BorderColor);
				svgBuilder.Replace("%LinearGradientColorA%", LinearGradientColorA);
				svgBuilder.Replace("%LinearGradientColorB%", LinearGradientColorB);
				svgBuilder.Replace("%CONTENT%", safeName);
				svgBuilder.Replace("%LEVEL%", Level.ToString(CultureInfo.InvariantCulture));

				var guardImageBase64 = GetGuardImageBase64(Guard);
				var guardImage = Guard == 0 || string.IsNullOrWhiteSpace(guardImageBase64)
					? string.Empty
					: $"<image x=\"0\" y=\"0\" width=\"44\" height=\"44\" xlink:href=\"{guardImageBase64}\"/>";
				svgBuilder.Replace("%GuardImage%", guardImage);

				var badgeId = convertToValidFilename(cacheKey);
				if (string.IsNullOrWhiteSpace(badgeId))
				{
					badgeId = BuildBadgeId(safeName, Level, Guard);
				}

				var outputPath = Path.Combine(GetBadgeDirectoryPath(), badgeId + ".png");
				Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? GetBadgeDirectoryPath());

				var renderWidth = imageWidth * RenderScale;
				var renderHeight = BadgeHeight * RenderScale;

			#if BADGE_DEBUG
				Log.Information("[BADGE_SVG] genImage key={Key} medal={Medal} lv={Level} guard={Guard} colors={Start}/{End}/{Border}", badgeId, safeName, Level, Guard, LinearGradientColorA, LinearGradientColorB, BorderColor);
			#endif

				var renderer = new SvgRenderer();
				var rendered = false;
				for (var attempt = 0; attempt < 5 && !rendered; attempt++)
				{
					rendered = renderer.TryRenderToPng(svgBuilder.ToString(), outputPath, renderWidth, renderHeight);
				}

				if (!rendered)
				{
					throw new InvalidOperationException("Unable to render badge PNG from SVG template.");
				}

				Uri = new Uri(outputPath).AbsoluteUri;
				Id = cacheKey;
				BadgeCache.Store(cacheKey, Uri, out var evictedKey, out var _);

			#if BADGE_DEBUG
				Log.Information("[BADGE_CACHE] STORE key={Key} uri={Uri}", cacheKey, Uri);
				if (!string.IsNullOrWhiteSpace(evictedKey))
				{
					Log.Information("[BADGE_CACHE] EVICT key={Key}", evictedKey);
				}
			#endif

			#if BADGE_DEBUG
				Log.Information("[BADGE_SVG] done key={Key} uri={Uri} elapsed={ElapsedMs}ms", badgeId, Uri, stopwatch.ElapsedMilliseconds);
			#endif
			}
			catch (Exception ex)
			{
				Log.Warning("[BADGE_SVG] FAIL key={Key} ex={ExceptionType}: {Message}", BuildBadgeId(safeName, Level, Guard), ex.GetType().Name, ex.Message);
			}
		}

		private string BuildCacheKey(string medalName, int level, int guardLevel)
		{
			return string.Concat(
				LinearGradientColorA,
				"_",
				LinearGradientColorB,
				"_",
				BorderColor,
				"_",
				medalName ?? string.Empty,
				"_",
				level.ToString(CultureInfo.InvariantCulture),
				"_",
				guardLevel.ToString(CultureInfo.InvariantCulture));
		}

		private async Task<int> getNameLengthAsync()
		{
			var badgeName = Name ?? string.Empty;
			var weightedLength = await Task.Run(() =>
			{
				var count = 0f;
				for (var i = 0; i < badgeName.Length; i++)
				{
					var character = badgeName.Substring(i, 1);
					if (ChineseCharRegex.IsMatch(character) || FullWidthEnglishRegex.IsMatch(character))
					{
						count += 1f;
					}
					else if (TwoThirdWidthEnglishRegex.IsMatch(character))
					{
						count += 2f / 3f;
					}
					else if (HalfWidthEnglishRegex.IsMatch(character))
					{
						count += 0.5f;
					}
					else if (QuarterWidthEnglishRegex.IsMatch(character))
					{
						count += 0.25f;
					}
					else
					{
						count += 1f;
					}
				}

				return count;
			}).ConfigureAwait(false);

			var width = (int)Math.Ceiling(22 * weightedLength);
		#if BADGE_DEBUG
			Log.Information("[BADGE_SVG] nameWidth medal={Medal} chars={Chars} width={Width}", badgeName, badgeName.Length, width);
		#endif
			return width;
		}

		public void setMedalColorByLevel(int level, int guardLevel = 0)
		{
			switch (level)
			{
				case 1:
				case 2:
				case 3:
				case 4:
					LinearGradientColorA = "#5c968e";
					LinearGradientColorB = "#5c968e";
					BorderColor = "#5c968e";
					break;
				case 5:
				case 6:
				case 7:
				case 8:
					LinearGradientColorA = "#5d7b9e";
					LinearGradientColorB = "#5d7b9e";
					BorderColor = "#5d7b9e";
					break;
				case 9:
				case 10:
				case 11:
				case 12:
					LinearGradientColorA = "#8d7ca6";
					LinearGradientColorB = "#8d7ca6";
					BorderColor = "#8d7ca6";
					break;
				case 13:
				case 14:
				case 15:
				case 16:
					LinearGradientColorA = "#be6686";
					LinearGradientColorB = "#be6686";
					BorderColor = "#be6686";
					break;
				case 17:
				case 18:
				case 19:
				case 20:
					LinearGradientColorA = "#c79d24";
					LinearGradientColorB = "#c79d24";
					BorderColor = "#c79d24";
					break;
				case 21:
				case 22:
				case 23:
				case 24:
					LinearGradientColorA = "#529d92";
					LinearGradientColorB = "#1a544b";
					BorderColor = "#1a544b";
					break;
				case 25:
				case 26:
				case 27:
				case 28:
					LinearGradientColorA = "#6888f1";
					LinearGradientColorB = "#06154c";
					BorderColor = "#06154c";
					break;
				case 29:
				case 30:
				case 31:
				case 32:
					LinearGradientColorA = "#9d9bff";
					LinearGradientColorB = "#06154c";
					BorderColor = "#06154c";
					break;
				case 33:
				case 34:
				case 35:
				case 36:
					LinearGradientColorA = "#e986bb";
					LinearGradientColorB = "#7a0423";
					BorderColor = "#7a0423";
					break;
				case 37:
				case 38:
				case 39:
				case 40:
					LinearGradientColorA = "#ffa869";
					LinearGradientColorB = "#fe7645";
					BorderColor = "#fe7645";
					break;
				default:
					LinearGradientColorA = "#000000";
					LinearGradientColorB = "#FFFFFF";
					BorderColor = "#FFFFFF";
					break;
			}

			switch (guardLevel)
			{
				case 1:
				case 2:
					BorderColor = "#ffe854";
					break;
				case 3:
					BorderColor = "#67e8ff";
					break;
			}

		#if BADGE_DEBUG
			Log.Information("[BADGE_SVG] colorMap inputLv={Level} guard={Guard} start={Start} end={End} border={Border}", level, guardLevel, LinearGradientColorA, LinearGradientColorB, BorderColor);
		#endif
		}

		private static string BuildBadgeId(string name, int level, int guard)
		{
			var guardSuffix = guard == 3
				? "_舰长"
				: (guard == 2
					? "_提督"
					: (guard == 1
						? "_总督"
						: string.Empty));

			var rawBadgeId = (name ?? string.Empty) + "_" + level.ToString(CultureInfo.InvariantCulture) + guardSuffix;
			var validBadgeId = convertToValidFilename(rawBadgeId);
			return string.IsNullOrWhiteSpace(validBadgeId) ? "badge" : validBadgeId;
		}

		private static string GetBadgeDirectoryPath()
		{
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".catcore", "cache", "Badges");
		}

		private static string GetGuardImageBase64(int guardLevel)
		{
			if (guardLevel <= 0)
			{
				return string.Empty;
			}

			var preferredFile = guardLevel switch
			{
				1 => "BilibiliLiveGuard1_full.png",
				2 => "BilibiliLiveGuard2_full.png",
				3 => "BilibiliLiveGuard3_full.png",
				_ => string.Empty
			};

			var fallbackFile = guardLevel switch
			{
				1 => "BilibiliLiveGuard1.png",
				2 => "BilibiliLiveGuard2.png",
				3 => "BilibiliLiveGuard3.png",
				_ => string.Empty
			};

			var base64 = Base64fromResourceImg(preferredFile);
			if (string.IsNullOrWhiteSpace(base64))
			{
				base64 = Base64fromResourceImg(fallbackFile);
			}

			return AddBase64DataType(base64);
		}

		private static string Base64fromResourceImg(string imageFile)
		{
			if (string.IsNullOrWhiteSpace(imageFile))
			{
				return string.Empty;
			}

			var resourceName = GuardResourcePrefix + imageFile;
			using (var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
			{
				if (resourceStream == null)
				{
					return string.Empty;
				}

				using (var memoryStream = new MemoryStream())
				{
					resourceStream.CopyTo(memoryStream);
					return Convert.ToBase64String(memoryStream.ToArray());
				}
			}
		}

		private static string AddBase64DataType(string base64)
		{
			if (string.IsNullOrWhiteSpace(base64))
			{
				return string.Empty;
			}

			return base64.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
				? base64
				: "data:image/png;base64," + base64;
		}

		private static string convertToValidFilename(string text)
		{
			var sanitized = text ?? string.Empty;
			var invalidCharacters = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
			for (var i = 0; i < invalidCharacters.Length; i++)
			{
				sanitized = sanitized.Replace(invalidCharacters[i].ToString(), string.Empty);
			}

			return sanitized;
		}

		private sealed class BadgeUriLruCache
		{
			private readonly int _capacity;
			private readonly Dictionary<string, LinkedListNode<BadgeCacheEntry>> _index;
			private readonly LinkedList<BadgeCacheEntry> _lru;
			private readonly object _lock = new object();

			public BadgeUriLruCache(int capacity)
			{
				_capacity = capacity;
				_index = new Dictionary<string, LinkedListNode<BadgeCacheEntry>>(StringComparer.Ordinal);
				_lru = new LinkedList<BadgeCacheEntry>();
			}

			public bool TryGet(string key, out string uri)
			{
				lock (_lock)
				{
					if (_index.TryGetValue(key, out var node))
					{
						_lru.Remove(node);
						_lru.AddFirst(node);
						uri = node.Value.Uri;
						return true;
					}
				}

				uri = string.Empty;
				return false;
			}

			public void Store(string key, string uri, out string evictedKey, out string evictedUri)
			{
				evictedKey = string.Empty;
				evictedUri = string.Empty;

				lock (_lock)
				{
					if (_index.TryGetValue(key, out var existingNode))
					{
						existingNode.Value = new BadgeCacheEntry(key, uri);
						_lru.Remove(existingNode);
						_lru.AddFirst(existingNode);
						return;
					}

					var newNode = new LinkedListNode<BadgeCacheEntry>(new BadgeCacheEntry(key, uri));
					_lru.AddFirst(newNode);
					_index[key] = newNode;

					if (_index.Count <= _capacity)
					{
						return;
					}

					var tail = _lru.Last;
					if (tail == null)
					{
						return;
					}

					_lru.RemoveLast();
					_index.Remove(tail.Value.Key);
					evictedKey = tail.Value.Key;
					evictedUri = tail.Value.Uri;
				}
			}

			private readonly struct BadgeCacheEntry
			{
				public BadgeCacheEntry(string key, string uri)
				{
					Key = key;
					Uri = uri;
				}

				public string Key { get; }
				public string Uri { get; }
			}
		}
	}
}
