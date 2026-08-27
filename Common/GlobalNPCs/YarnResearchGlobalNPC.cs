using Terraria;
using Terraria.ModLoader;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.GlobalNPCs
{
	public class YarnResearchGlobalNPC : GlobalNPC
	{
		public override void ModifyActiveShop(NPC npc, string shopName, Item[] items)
		{
			if (NPCShopDatabase.TryGetNPCShop(shopName, out AbstractNPCShop shop))
				ResearchCascadeSystem.ProcessShopEntries(shop);
		}
	}
}
