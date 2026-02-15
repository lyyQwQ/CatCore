using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using CatCore.Models.Bilibili;
using CatCore.Models.Config;
using CatCore.Services;
using CatCore.Services.Interfaces;
using FluentAssertions;
using Serilog;
using Xunit;

namespace CatCoreTests
{
	public sealed class BilibiliDanmuSemanticFallbackTests
	{
		[Fact]
		public void ResolveDanmuIsBroadcaster_OwnerUidMissing_DoesNotFallbackToOwnerName()
		{
			var service = CreateBilibiliServiceForDanmuTest();
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""data"": {},
			  ""info"": [
			    [],
			    ""同名测试"",
			    [""12345"", ""同名用户""],
			    [0, """", ""同名用户""]
			  ]
			}");

			var method = GetBilibiliServiceMethod("ResolveDanmuIsBroadcaster");
			var result = method.Invoke(service, new object[] { payload, payload.GetProperty("info"), "12345" });

			result.Should().BeOfType<bool>();
			((bool)result!).Should().BeFalse();
		}

		[Fact]
		public void TryBuildChatMessage_Info3Missing_DataFansMedalProvidesDisplayPrefix()
		{
			var service = CreateBilibiliServiceForDanmuTest();
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""cmd"": ""DANMU_MSG"",
			  ""msg_id"": ""10006"",
			  ""data"": {
			    ""fans_medal"": {
			      ""medal_name"": ""应援团"",
			      ""medal_level"": 12
			    }
			  },
			  ""info"": [
			    [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
			    ""弹幕内容"",
			    [""12345"", ""测试用户"", 0, 0, 0, 0, 0, ""FFFFFF""],
			    [0, """", 0, 0, 0, 0, 0, ""#FFFFFF"", ""#FFD26E"", ""#1F2D42""]
			  ]
			}");

			var method = GetBilibiliServiceMethod("TryBuildChatMessage");
			var channel = new BilibiliChannel("1", "1");
			var result = method.Invoke(service, new object[] { payload, payload.GetRawText(), "DANMU_MSG", channel });

			result.Should().BeOfType<BilibiliMessage>();
			var message = (BilibiliMessage)result!;
			message.Sender.DisplayName.Should().Contain("应援团 12");
			message.Sender.DisplayName.Should().EndWith("测试用户");
		}

		[Fact]
		public void TryBuildChatMessage_Info3Present_DoesNotUseDataFansMedalFallbackOrMedalTextPrefix()
		{
			var service = CreateBilibiliServiceForDanmuTest();
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""cmd"": ""DANMU_MSG"",
			  ""msg_id"": ""10007"",
			  ""data"": {
			    ""fans_medal"": {
			      ""medal_name"": ""数据侧粉丝牌"",
			      ""medal_level"": 99
			    }
			  },
			  ""info"": [
			    [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
			    ""弹幕内容"",
			    [""12345"", ""测试用户"", 0, 0, 0, 0, 0, ""FFFFFF""],
			    [3, ""信息侧粉丝牌"", 0, 0, 0, 0, 0, ""#FFFFFF"", ""#FFD26E"", ""#1F2D42""]
			  ]
			}");

			var method = GetBilibiliServiceMethod("TryBuildChatMessage");
			var channel = new BilibiliChannel("1", "1");
			var result = method.Invoke(service, new object[] { payload, payload.GetRawText(), "DANMU_MSG", channel });

			result.Should().BeOfType<BilibiliMessage>();
			var message = (BilibiliMessage)result!;
			message.Sender.DisplayName.Should().Be("测试用户");
			message.Sender.DisplayName.Should().NotContain("信息侧粉丝牌 3");
			message.Sender.DisplayName.Should().NotContain("数据侧粉丝牌 99");
		}

		[Fact]
		public void TryBuildChatMessage_ShowBadgeFalse_DoesNotApplyMedalPrefixAndKeepsUserName()
		{
			var service = CreateBilibiliServiceForDanmuTest(showBadge: false);
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""cmd"": ""DANMU_MSG"",
			  ""msg_id"": ""10008"",
			  ""data"": {
			    ""fans_medal"": {
			      ""medal_name"": ""数据侧粉丝牌"",
			      ""medal_level"": 99
			    }
			  },
			  ""info"": [
			    [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
			    ""弹幕内容"",
			    [""12345"", ""测试用户"", 0, 0, 0, 0, 0, ""FFFFFF""],
			    [3, ""信息侧粉丝牌"", 0, 0, 0, 0, 0, ""#FFFFFF"", ""#FFD26E"", ""#1F2D42""]
			  ]
			}");

			var method = GetBilibiliServiceMethod("TryBuildChatMessage");
			var channel = new BilibiliChannel("1", "1");
			var result = method.Invoke(service, new object[] { payload, payload.GetRawText(), "DANMU_MSG", channel });

			result.Should().BeOfType<BilibiliMessage>();
			var message = (BilibiliMessage)result!;
			message.Sender.DisplayName.Should().Be("测试用户");
			message.Sender.DisplayName.Should().NotContain("信息侧粉丝牌 3");
		}

		[Fact]
		public void TryBuildChatMessage_HonorLevelWithoutIcon_DoesNotUseHonorTextPrefixAndFallsBackToInternalHonorImageMetadata()
		{
			var service = CreateBilibiliServiceForDanmuTest();
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""cmd"": ""DANMU_MSG"",
			  ""msg_id"": ""10001"",
			  ""data"": {},
			  ""info"": [
			    [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
			    ""弹幕内容"",
			    [""12345"", ""测试用户"", 0, 0, 0, 0, 0, ""FFFFFF""],
			    [0, """", ""房间主播""],
			    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			    [5]
			  ]
			}");

			var method = GetBilibiliServiceMethod("TryBuildChatMessage");
			var channel = new BilibiliChannel("1", "1");
			var result = method.Invoke(service, new object[] { payload, payload.GetRawText(), "DANMU_MSG", channel });

			result.Should().BeOfType<BilibiliMessage>();
			var message = (BilibiliMessage)result!;
			message.Sender.DisplayName.Should().Be("测试用户");
			message.Sender.DisplayName.Should().NotContain("[荣誉Lv5]");
			message.Metadata.Should().NotBeNull();

			var metadata = message.Metadata!;
			metadata.Should().ContainKey("bili.richimage.0.kind");
			metadata["bili.richimage.0.kind"].Should().Be("tag");
			metadata.Should().ContainKey("bili.richimage.0.url");
			metadata["bili.richimage.0.url"].Should().Contain("BilibiliLiveHonorLevel.png");
		}

		[Fact]
		public void TryBuildChatMessage_BadgeImagePresent_DoesNotKeepFansMedalTextPrefix()
		{
			var service = CreateBilibiliServiceForDanmuTest();
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""cmd"": ""DANMU_MSG"",
			  ""msg_id"": ""10002"",
			  ""data"": {
			    ""badge"": {
			      ""icon"": ""https://i0.hdslb.com/bfs/test/badge.png""
			    }
			  },
			  ""info"": [
			    [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
			    ""弹幕内容"",
			    [""12345"", ""测试用户"", 0, 0, 0, 0, 0, ""FFFFFF""],
			    [3, ""舰长团"", 0, 0, 0, 0, 0, ""#FFFFFF"", ""#FFD26E"", ""#1F2D42""]
			  ]
			}");

			var method = GetBilibiliServiceMethod("TryBuildChatMessage");
			var channel = new BilibiliChannel("1", "1");
			var result = method.Invoke(service, new object[] { payload, payload.GetRawText(), "DANMU_MSG", channel });

			result.Should().BeOfType<BilibiliMessage>();
			var message = (BilibiliMessage)result!;
			message.Sender.DisplayName.Should().Be("测试用户");
			message.Sender.DisplayName.Should().NotContain("舰长团 3");
			message.Metadata.Should().NotBeNull();
			message.Metadata!.Values.Should().Contain("badge");
		}

		[Fact]
		public void TryBuildChatMessage_BadgeInfoIconWithGuardLevel_UsesTagAndKeepsGuardFallbackBadgeWithoutHonorTextPrefix()
		{
			var service = CreateBilibiliServiceForDanmuTest();
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""cmd"": ""DANMU_MSG"",
			  ""msg_id"": ""10005"",
			  ""data"": {
			    ""badge_info"": {
			      ""icon"": ""https://i0.hdslb.com/bfs/test/badge-info-tag.png""
			    }
			  },
			  ""info"": [
			    [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
			    ""弹幕内容"",
			    [""12345"", ""测试用户"", 0, 0, 0, 0, 0, ""FFFFFF""],
			    [0, """", 0, 0, 0, 0, 0, ""#FFFFFF"", ""#FFD26E"", ""#1F2D42""],
			    0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0,
			    [5]
			  ]
			}");

			var method = GetBilibiliServiceMethod("TryBuildChatMessage");
			var channel = new BilibiliChannel("1", "1");
			var result = method.Invoke(service, new object[] { payload, payload.GetRawText(), "DANMU_MSG", channel });

			result.Should().BeOfType<BilibiliMessage>();
			var message = (BilibiliMessage)result!;
			message.Sender.DisplayName.Should().NotContain("[荣誉Lv5]");
			message.Metadata.Should().NotBeNull();

			var metadata = message.Metadata!;
			metadata.Values.Should().Contain("https://i0.hdslb.com/bfs/test/badge-info-tag.png");

			var hasTagKind = false;
			var hasBadgeKind = false;
			foreach (var kv in metadata)
			{
				if (!kv.Key.EndsWith(".kind", StringComparison.Ordinal))
				{
					continue;
				}

				if (string.Equals(kv.Value, "tag", StringComparison.Ordinal))
				{
					hasTagKind = true;
				}

				if (string.Equals(kv.Value, "badge", StringComparison.Ordinal))
				{
					hasBadgeKind = true;
				}
			}

			hasTagKind.Should().BeTrue();
			hasBadgeKind.Should().BeTrue();
		}

		[Fact]
		public void TryBuildChatMessage_DanmuDataHasMultipleBadgeSources_MetadataContainsMultipleBadges()
		{
			var service = CreateBilibiliServiceForDanmuTest();
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""cmd"": ""DANMU_MSG"",
			  ""msg_id"": ""10004"",
			  ""data"": {
			    ""fans_medal"": {
			      ""medal_icon"": ""https://i0.hdslb.com/bfs/test/fans-medal-badge.png""
			    },
			    ""medal"": {
			      ""guard_icon"": ""https://i0.hdslb.com/bfs/test/guard-badge.png""
			    }
			  },
			  ""info"": [
			    [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
			    ""弹幕内容"",
			    [""12345"", ""测试用户"", 0, 0, 0, 0, 0, ""FFFFFF""]
			  ]
			}");

			var method = GetBilibiliServiceMethod("TryBuildChatMessage");
			var channel = new BilibiliChannel("1", "1");
			var result = method.Invoke(service, new object[] { payload, payload.GetRawText(), "DANMU_MSG", channel });

			result.Should().BeOfType<BilibiliMessage>();
			var message = (BilibiliMessage)result!;
			message.Metadata.Should().NotBeNull();

			var metadata = message.Metadata!;
			var badgeKindCount = 0;
			foreach (var kv in metadata)
			{
				if (kv.Key.EndsWith(".kind", StringComparison.Ordinal)
					&& string.Equals(kv.Value, "badge", StringComparison.Ordinal))
				{
					badgeKindCount++;
				}
			}

			badgeKindCount.Should().BeGreaterOrEqualTo(2);
			metadata.Values.Should().Contain("https://i0.hdslb.com/bfs/test/fans-medal-badge.png");
			metadata.Values.Should().Contain("https://i0.hdslb.com/bfs/test/guard-badge.png");
		}

		[Fact]
		public void TryBuildChatMessage_DanmuDataHasMultipleTagSources_MetadataPreservesMoreThanTwoRichImages()
		{
			var service = CreateBilibiliServiceForDanmuTest();
			var payload = JsonSerializer.Deserialize<JsonElement>(@"{
			  ""cmd"": ""DANMU_MSG"",
			  ""msg_id"": ""10003"",
			  ""data"": {
			    ""uface"": ""https://i0.hdslb.com/bfs/test/avatar.png"",
			    ""badge_info"": {
			      ""icon"": ""https://i0.hdslb.com/bfs/test/badge.png""
			    },
			    ""icon"": {
			      ""prefix"": {
			        ""resource"": [
			          ""https://i0.hdslb.com/bfs/test/tag-prefix-1.png"",
			          ""https://i0.hdslb.com/bfs/test/tag-prefix-2.png""
			        ]
			      }
			    },
			    ""extra"": {
			      ""icon"": {
			        ""prefix"": {
			          ""resource"": [
			            ""https://i0.hdslb.com/bfs/test/tag-extra-1.png""
			          ]
			        }
			      }
			    },
			    ""title_info"": {
			      ""icon"": ""https://i0.hdslb.com/bfs/test/title-tag.png""
			    }
			  },
			  ""info"": [
			    [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
			    ""弹幕内容"",
			    [""12345"", ""测试用户"", 0, 0, 0, 0, 0, ""FFFFFF""]
			  ]
			}");

			var method = GetBilibiliServiceMethod("TryBuildChatMessage");
			var channel = new BilibiliChannel("1", "1");
			var result = method.Invoke(service, new object[] { payload, payload.GetRawText(), "DANMU_MSG", channel });

			result.Should().BeOfType<BilibiliMessage>();
			var message = (BilibiliMessage)result!;
			message.Metadata.Should().NotBeNull();

			var metadata = message.Metadata!;
			metadata.Should().ContainKey("bili.richimage.count");
			int.TryParse(metadata["bili.richimage.count"], out var richImageCount).Should().BeTrue();
			richImageCount.Should().BeGreaterThan(2);

			var tagKindCount = 0;
			foreach (var kv in metadata)
			{
				if (kv.Key.EndsWith(".kind", StringComparison.Ordinal)
					&& string.Equals(kv.Value, "tag", StringComparison.Ordinal))
				{
					tagKindCount++;
				}
			}

			tagKindCount.Should().BeGreaterOrEqualTo(2);
		}

		private static Type GetBilibiliServiceType()
		{
			var catCoreAssembly = typeof(KittenApiService).Assembly;
			var bilibiliServiceType = catCoreAssembly.GetType("CatCore.Services.Bilibili.BilibiliService");
			bilibiliServiceType.Should().NotBeNull();
			return bilibiliServiceType!;
		}

		private static MethodInfo GetBilibiliServiceMethod(string name)
		{
			var method = GetBilibiliServiceType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
			method.Should().NotBeNull();
			return method!;
		}

		private static object CreateBilibiliServiceForDanmuTest(bool? showBadge = null)
		{
			var bilibiliServiceType = GetBilibiliServiceType();
			var service = FormatterServices.GetUninitializedObject(bilibiliServiceType);

			SetPrivateField(bilibiliServiceType, service, "_logger", new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger());
			SetPrivateField(bilibiliServiceType, service, "_danmuWealthIconCacheLock", new object());
			SetPrivateField(bilibiliServiceType, service, "_danmuWealthLevelIconUrls", new Dictionary<int, string>());
			if (showBadge.HasValue)
			{
				SetPrivateField(bilibiliServiceType, service, "_settingsService", new TestKittenSettingsService(showBadge.Value));
			}

			return service;
		}

		private sealed class TestKittenSettingsService : IKittenSettingsService
		{
			public ConfigRoot Config { get; }

			public event Action<IKittenSettingsService, ConfigRoot>? OnConfigChanged;

			public TestKittenSettingsService(bool showBadge)
			{
				Config = new ConfigRoot();
				Config.BilibiliConfig.ShowBadge = showBadge;
			}

			public void Initialize()
			{
			}

			public void Load()
			{
			}

			public void Store()
			{
				OnConfigChanged?.Invoke(this, Config);
			}

			public IDisposable ChangeTransaction()
			{
				return NoopDisposable.Instance;
			}

			private sealed class NoopDisposable : IDisposable
			{
				public static readonly NoopDisposable Instance = new();

				public void Dispose()
				{
				}
			}
		}

		private static void SetPrivateField(Type type, object instance, string fieldName, object? value)
		{
			var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
			field.Should().NotBeNull();
			field!.SetValue(instance, value);
		}
	}
}
