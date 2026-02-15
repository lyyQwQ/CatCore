using CatCore.Models.Bilibili;
using CatCore.Models.Shared;
using CatCore.Services.Bilibili.Interfaces;
using CatCore.Services.Interfaces;
using Serilog;

namespace CatCore.Services.Bilibili
{
	internal sealed class BilibiliServiceManager : KittenPlatformServiceManagerBase<IBilibiliService, BilibiliChannel, BilibiliMessage>
	{
		public BilibiliServiceManager(ILogger logger, IBilibiliService bilibiliService, IKittenPlatformActiveStateManager activeStateManager)
			: base(logger, bilibiliService, activeStateManager, PlatformType.Bilibili)
		{
		}
	}
}
