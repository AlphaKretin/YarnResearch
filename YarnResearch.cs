using System.IO;
using Terraria.ModLoader;
using YarnResearch.Common.Net;

namespace YarnResearch
{
	// Please read https://github.com/tModLoader/tModLoader/wiki/Basic-tModLoader-Modding-Guide#mod-skeleton-contents for more information about the various files in a mod.
	public class YarnResearch : Mod
	{
		public override void HandlePacket(BinaryReader reader, int whoAmI) =>
			YarnNetwork.HandlePacket(reader, whoAmI);
	}
}
