using System.Collections.ObjectModel;

namespace CatCore.Models.Shared
{
	public interface IChatUserWithBadges : IChatUser
	{
		ReadOnlyCollection<IChatBadge> Badges { get; }
	}
}
