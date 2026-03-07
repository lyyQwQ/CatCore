using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using Svg;

namespace CatCore.Svg
{
	public sealed class SvgRenderer
	{
		private sealed class FontCandidate
		{
			public string FontPath { get; set; } = string.Empty;
			public string[] FamilyNames { get; set; } = Array.Empty<string>();
		}

		private const string FontFallbackFamily = "Microsoft YaHei UI Semibold, Microsoft YaHei UI, Microsoft YaHei, 微软雅黑";
		private const string SourceFamily = "Microsoft YaHei UI Semibold";
		private static readonly string[] SemiboldToUiMapping = { "Microsoft YaHei UI Semibold", "Microsoft YaHei UI" };
		private static readonly object FontLock = new object();
		private static bool _fontMappingsInitialized;
		private static FontCandidate _selectedFont = new FontCandidate();

		public bool TryRenderToPng(string svgTemplate, string outputPath, int width, int height)
		{
#if BADGE_DEBUG
			Console.WriteLine($"[BADGE_SVG] render entry width={width} height={height} output={outputPath}");
#endif
			if (string.IsNullOrWhiteSpace(svgTemplate) || string.IsNullOrWhiteSpace(outputPath) || width <= 0 || height <= 0)
			{
				LogWarning("Render failed: invalid input arguments.");
				return false;
			}

			try
			{
				InitializeFontMappings();
				var normalizedTemplate = EnsureFallbackFontFamily(svgTemplate);
				var directory = Path.GetDirectoryName(outputPath);
				if (!string.IsNullOrWhiteSpace(directory))
				{
					Directory.CreateDirectory(directory);
				}

				using (var svgStream = new MemoryStream(Encoding.UTF8.GetBytes(normalizedTemplate)))
				using (var bitmap = new Bitmap(width, height))
				using (var graphics = Graphics.FromImage(bitmap))
				{
					var svgDocument = SvgDocument.Open<SvgDocument>(svgStream);
					svgDocument.Width = width;
					svgDocument.Height = height;
					graphics.Clear(Color.Transparent);

					var renderer = global::Svg.SvgRenderer.FromGraphics(graphics);
					svgDocument.Draw(renderer);
					bitmap.Save(outputPath, ImageFormat.Png);
				}

#if BADGE_DEBUG
				Console.WriteLine($"[BADGE_SVG] render done output={outputPath}");
#endif
				return true;
			}
			catch (Exception ex)
			{
				LogWarning($"Render failed: {ex.GetType().Name}: {ex.Message}");
				return false;
			}
		}

		private static void InitializeFontMappings()
		{
			if (_fontMappingsInitialized)
			{
				return;
			}

			lock (FontLock)
			{
				if (_fontMappingsInitialized)
				{
					return;
				}

				try
				{
					_selectedFont = ResolvePreferredFont();

					if (!string.IsNullOrWhiteSpace(_selectedFont.FontPath) && File.Exists(_selectedFont.FontPath))
					{
						if (!SvgFontManager.PrivateFontPathList.Contains(_selectedFont.FontPath))
						{
							SvgFontManager.PrivateFontPathList.Add(_selectedFont.FontPath);
						}

						AppendFamilyNames(_selectedFont.FamilyNames);
					}

					AppendFamilyNames(new[] { SemiboldToUiMapping[0], SemiboldToUiMapping[1] });

					_fontMappingsInitialized = true;
#if BADGE_DEBUG
					Console.WriteLine($"[BADGE_SVG] font init done path={_selectedFont.FontPath}");
#endif
				}
				catch (Exception ex)
				{
					LogWarning($"Font init failed: {ex.Message}");
				}
			}
		}

		private static string EnsureFallbackFontFamily(string svgTemplate)
		{
			if (svgTemplate.IndexOf(FontFallbackFamily, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return svgTemplate;
			}

			if (svgTemplate.IndexOf(SourceFamily, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return svgTemplate.Replace(SourceFamily, FontFallbackFamily);
			}

			const string svgTagStart = "<svg";
			var svgTagIndex = svgTemplate.IndexOf(svgTagStart, StringComparison.OrdinalIgnoreCase);
			if (svgTagIndex < 0)
			{
				return svgTemplate;
			}

			var insertIndex = svgTemplate.IndexOf('>', svgTagIndex);
			if (insertIndex < 0)
			{
				return svgTemplate;
			}

			return svgTemplate.Insert(insertIndex, $" style=\"font-family: {FontFallbackFamily};\"");
		}

		private static void AppendFamilyNames(string[] familyNames)
		{
			if (familyNames == null || familyNames.Length == 0)
			{
				return;
			}

			var localizedFamilyNames = (object)SvgFontManager.LocalizedFamilyNames;
			if (localizedFamilyNames is IDictionary<string, string> dictionary)
			{
				for (var i = 0; i < familyNames.Length - 1; i++)
				{
					dictionary[familyNames[i]] = familyNames[i + 1];
				}

				return;
			}

			if (localizedFamilyNames is ICollection<string[]> familyCollection)
			{
				var exists = familyCollection.Any(x => x.SequenceEqual(familyNames));
				if (!exists)
				{
					familyCollection.Add(familyNames);
				}
			}
		}

		private static FontCandidate ResolvePreferredFont()
		{
			var windowsFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
			var localFonts = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Microsoft",
				"Windows",
				"Fonts");

			var candidates = new[]
			{
				new FontCandidate
				{
					FontPath = Path.Combine(windowsFonts, "msyhbd.ttc"),
					FamilyNames = new[]
					{
						"Microsoft YaHei UI Semibold",
						"Microsoft YaHei UI Bold",
						"Microsoft YaHei UI",
						"Microsoft YaHei",
						"MicrosoftYaHeiUISemibold",
						"微软雅黑 Semibold",
						"微软雅黑 Bold",
						"微软雅黑"
					}
				},
				new FontCandidate
				{
					FontPath = Path.Combine(windowsFonts, "msyh.ttc"),
					FamilyNames = new[]
					{
						"Microsoft YaHei UI",
						"Microsoft YaHei",
						"微软雅黑",
						"Microsoft YaHei UI Semibold",
						"MicrosoftYaHeiUISemibold",
						"微软雅黑 Semibold"
					}
				},
				new FontCandidate
				{
					FontPath = Path.Combine(windowsFonts, "msyhl.ttc"),
					FamilyNames = new[]
					{
						"Microsoft YaHei UI Light",
						"Microsoft YaHei UI",
						"Microsoft YaHei",
						"微软雅黑 Light",
						"微软雅黑",
						"Microsoft YaHei UI Semibold"
					}
				},
				new FontCandidate
				{
					FontPath = Path.Combine(localFonts, "msyhbd.ttc"),
					FamilyNames = new[]
					{
						"Microsoft YaHei UI Semibold",
						"Microsoft YaHei UI Bold",
						"Microsoft YaHei UI",
						"Microsoft YaHei",
						"微软雅黑 Semibold",
						"微软雅黑 Bold",
						"微软雅黑"
					}
				},
				new FontCandidate
				{
					FontPath = Path.Combine(localFonts, "msyh.ttc"),
					FamilyNames = new[]
					{
						"Microsoft YaHei UI",
						"Microsoft YaHei",
						"微软雅黑",
						"Microsoft YaHei UI Semibold",
						"微软雅黑 Semibold"
					}
				}
			};

			foreach (var candidate in candidates)
			{
				if (File.Exists(candidate.FontPath))
				{
					return candidate;
				}
			}

			return new FontCandidate();
		}

		private static void LogWarning(string message)
		{
			Console.WriteLine($"[Warning] [BADGE_SVG] {message}");
		}
	}
}
