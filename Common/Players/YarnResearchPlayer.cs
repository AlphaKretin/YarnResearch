using System.Collections.Generic;
using Terraria;
using Terraria.GameContent.Creative;
using Terraria.ModLoader;
using YarnResearch.Common.Configs;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.Players
{
	public class YarnResearchPlayer : ModPlayer
	{
		private readonly Dictionary<Item[], (int[] Types, int[] Stacks)> _snapshots = new();

		public override void PostUpdate()
		{
			if (Player.whoAmI != Main.myPlayer)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (!config.AutoResearchHeldItems)
				return;

			ResearchCascadeSystem.BeginBatch();
			try {
				ScanAndDiff(Player.inventory);

				if (config.IncludeBankAndSafeInScan) {
					ScanAndDiff(Player.bank.item);
					ScanAndDiff(Player.bank2.item);
					ScanAndDiff(Player.bank3.item);
				}
			}
			finally {
				ResearchCascadeSystem.EndBatch();
			}
		}

		private void ScanAndDiff(Item[] items)
		{
			if (!_snapshots.TryGetValue(items, out var snapshot) || snapshot.Types.Length != items.Length) {
				snapshot = (new int[items.Length], new int[items.Length]);
				_snapshots[items] = snapshot;
			}

			for (int i = 0; i < items.Length; i++) {
				Item item = items[i];
				if (snapshot.Types[i] == item.type && snapshot.Stacks[i] == item.stack)
					continue;

				snapshot.Types[i] = item.type;
				snapshot.Stacks[i] = item.stack;

				CheckItem(item);
			}
		}

		private static void CheckItem(Item item)
		{
			if (item.IsAir || item.ResearchUnlockCount <= 0)
				return;

			if (ResearchCascadeSystem.IsResearched(item.type))
				return;

			int? remaining = CreativeUI.GetSacrificesRemaining(item.type);
			if (remaining.HasValue && item.stack >= remaining.Value) {
				ResearchCascadeSystem.RegisterHeldOrigin(item.type);
				CreativeUI.ResearchItem(item.type);
				ResearchCascadeSystem.ClearHeldOrigin(item.type);
			}
		}
	}
}
