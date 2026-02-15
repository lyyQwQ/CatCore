using System;
using System.Reflection;
using CatCore.Services;
using FluentAssertions;
using Xunit;

namespace CatCoreTests
{
	public sealed class BilibiliServiceImageUrlNormalizationTests
	{
		[Fact]
		public void NormalizeBilibiliImageUrl_LocalhostHttpUrl_KeepsHttpScheme()
		{
			const string input = "http://localhost:8338/Statics/Images/BilibiliLiveBroadcaster.png";

			var result = InvokeNormalizeBilibiliImageUrl(input);

			result.Should().Be(input);
		}

		[Fact]
		public void NormalizeBilibiliImageUrl_LoopbackIpv4HttpUrl_KeepsHttpScheme()
		{
			const string input = "http://127.0.0.1:8338/Statics/Images/BilibiliLiveBroadcaster.png";

			var result = InvokeNormalizeBilibiliImageUrl(input);

			result.Should().Be(input);
		}

		[Fact]
		public void NormalizeBilibiliImageUrl_ExternalHttpUrl_UpgradesToHttps()
		{
			const string input = "http://i0.hdslb.com/bfs/face/avatar.png";
			const string expected = "https://i0.hdslb.com/bfs/face/avatar.png";

			var result = InvokeNormalizeBilibiliImageUrl(input);

			result.Should().Be(expected);
		}

		private static string InvokeNormalizeBilibiliImageUrl(string url)
		{
			var catCoreAssembly = typeof(KittenApiService).Assembly;
			var bilibiliServiceType = catCoreAssembly.GetType("CatCore.Services.Bilibili.BilibiliService");
			bilibiliServiceType.Should().NotBeNull();

			var method = bilibiliServiceType!.GetMethod("NormalizeBilibiliImageUrl", BindingFlags.NonPublic | BindingFlags.Static);
			method.Should().NotBeNull();

			var result = method!.Invoke(null, new object[] { url });
			result.Should().BeOfType<string>();
			return (string)result!;
		}
	}
}
