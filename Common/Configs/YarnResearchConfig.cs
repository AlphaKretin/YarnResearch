using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace YarnResearch.Common.Configs
{
	public class YarnResearchConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ClientSide;

		[Header("Features")]
		[DefaultValue(true)]
		public bool AutoResearchHeldItems;

		[DefaultValue(true)]
		public bool AutoResearchCraftable;

		[DefaultValue(true)]
		public bool InfiniteResearchedConsumables;

		[DefaultValue(true)]
		public bool AutoResearchMiscCascades;

		[DefaultValue(true)]
		public bool AutoResearchShimmerOutputs;

		[DefaultValue(true)]
		public bool AutoResearchCrateContents;

		[DefaultValue(true)]
		public bool AutoResearchShopStock;

		[DefaultValue(true)]
		public bool InfiniteResearchedBuffs;

		[Header("Options")]
		[DefaultValue(true)]
		public bool ShowAutoResearchNotifications;
	}
}
