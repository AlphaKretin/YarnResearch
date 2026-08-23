using Terraria;
using Terraria.ModLoader;
using YarnResearch.Common.Configs;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.GlobalItems
{
	public class YarnResearchGlobalItem : GlobalItem
	{
		public override bool ConsumeItem(Item item, Player player)
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (!config.InfiniteResearchedConsumables)
				return true;

			return !ResearchCascadeSystem.IsResearched(item.type);
		}

		public override void OnResearched(Item item, bool fullyResearched)
		{
			if (fullyResearched)
				ResearchCascadeSystem.HandleResearched(item.type);
		}
	}
}
