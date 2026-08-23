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

		[Header("Options")]
		[DefaultValue(true)]
		public bool IncludeBankAndSafeInScan;

		[DefaultValue(true)]
		public bool ShowAutoResearchNotifications;
	}
}
