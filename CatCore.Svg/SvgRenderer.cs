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
		private const string FontFallbackFamily = "Microsoft YaHei UI Semibold, Microsoft YaHei UI, Microsoft YaHei, sans-serif";
		private const string SourceFamily = "Microsoft YaHei UI Semibold";
		private static readonly string[] SemiboldToUiMapping = { "Microsoft YaHei UI Semibold", "Microsoft YaHei UI" };
		private static bool _fontMappingsInitialized;

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
				var pngBytes = RenderSvgToPng(normalizedTemplate, width, height);
				if (pngBytes.Length == 0)
				{
					return false;
				}

				File.WriteAllBytes(outputPath, pngBytes);
#if BADGE_DEBUG
				Console.WriteLine($"[BADGE_SVG] render done bytes={pngBytes.Length}");
#endif
				return true;
			}
			catch (Exception ex)
			{
				LogWarning($"Render failed: {ex.Message}");
				return false;
			}
		}

		public byte[] RenderSvgToPng(string svgTemplate, int width, int height)
		{
			try
			{
				using (var svgStream = new MemoryStream(Encoding.UTF8.GetBytes(svgTemplate)))
				using (var output = new MemoryStream())
				{
					var svgDocument = SvgDocument.Open<SvgDocument>(svgStream);
					svgDocument.Width = width;
					svgDocument.Height = height;
					using (var bitmap = svgDocument.Draw(width, height))
					{
						if (bitmap == null)
						{
							LogWarning("Render failed: svg draw returned null bitmap.");
							return Array.Empty<byte>();
						}

						bitmap.Save(output, ImageFormat.Png);
					}

					return output.ToArray();
				}
			}
			catch (Exception ex)
			{
				LogWarning($"Render failed: {ex.Message}");
				return Array.Empty<byte>();
			}
		}

		private static void InitializeFontMappings()
		{
			if (_fontMappingsInitialized)
			{
				return;
			}

			try
			{
				var localizedFamilyNames = (object)SvgFontManager.LocalizedFamilyNames;
				if (localizedFamilyNames is IDictionary<string, string> dictionary)
				{
					dictionary[SemiboldToUiMapping[0]] = SemiboldToUiMapping[1];
				}
				else if (localizedFamilyNames is ICollection<string[]> familyCollection)
				{
					var exists = familyCollection.Any(x => x.Length >= 2 && string.Equals(x[0], SemiboldToUiMapping[0], StringComparison.Ordinal) && string.Equals(x[1], SemiboldToUiMapping[1], StringComparison.Ordinal));
					if (!exists)
					{
						familyCollection.Add(new[] { SemiboldToUiMapping[0], SemiboldToUiMapping[1] });
					}
				}

				_fontMappingsInitialized = true;
#if BADGE_DEBUG
				Console.WriteLine("[BADGE_SVG] font init done: Microsoft YaHei UI Semibold -> Microsoft YaHei UI");
#endif
			}
			catch (Exception ex)
			{
				LogWarning($"Font init failed: {ex.Message}");
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

		private static void LogWarning(string message)
		{
			Console.WriteLine($"[Warning] [BADGE_SVG] {message}");
		}
	}
}
