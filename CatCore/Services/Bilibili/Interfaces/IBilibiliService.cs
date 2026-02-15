using CatCore.Models.Bilibili;
using CatCore.Services.Interfaces;

namespace CatCore.Services.Bilibili.Interfaces
{
	public interface IBilibiliService : IPlatformService<IBilibiliService, BilibiliChannel, BilibiliMessage>
	{
	}
}
