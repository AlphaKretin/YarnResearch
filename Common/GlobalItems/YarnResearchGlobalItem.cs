using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using YarnResearch.Common.Configs;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.GlobalItems
{
	public class YarnResearchGlobalItem : GlobalItem
	{
		public static LocalizedText ResearchCrateContentsHintText { get; private set; }

		public override void SetStaticDefaults()
		{
			ResearchCrateContentsHintText = Mod.GetLocalization($"{nameof(YarnResearchGlobalItem)}.ResearchCrateContentsHint");
		}

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

		public override void ModifyTooltips(Item item, List<TooltipLine> tooltips)
		{
			if (!ItemID.Sets.OpenableBag[item.type] || !ResearchCascadeSystem.IsResearched(item.type))
				return;

			if (!ResearchCascadeSystem.HasUnresearchedCrateContents(item.type))
				return;

			List<string> keys = ResearchCascadeSystem.ResearchCrateContentsKeybind.GetAssignedKeys();
			if (keys.Count == 0)
				return;

			tooltips.Add(new TooltipLine(Mod, "ResearchCrateContentsHint", ResearchCrateContentsHintText.Format(keys[0])) {
				Color = Color.Pink
			});
		}
	}
}
