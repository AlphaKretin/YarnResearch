using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using YarnResearch.Common.Configs;
using YarnResearch.Common.Net;
using YarnResearch.Common.Players;
using YarnResearch.Common.Systems;
using YarnResearch.Common.UI;
using YarnResearch.Common.UI.PrefixPickerUI;

namespace YarnResearch.Common.GlobalItems
{
	public class YarnResearchGlobalItem : GlobalItem
	{
		public static LocalizedText ResearchCrateContentsHintText { get; private set; }
		public static LocalizedText ToggleInfiniteBuffHintText { get; private set; }
		public static LocalizedText OpenPrefixPickerHintText { get; private set; }
		public static LocalizedText PrefixPickerPopupSetDefaultHintText { get; private set; }
		public static LocalizedText PrefixPickerPopupDuplicateHintText { get; private set; }

		public override void SetStaticDefaults()
		{
			ResearchCrateContentsHintText = Mod.GetLocalization($"{nameof(YarnResearchGlobalItem)}.ResearchCrateContentsHint");
			ToggleInfiniteBuffHintText = Mod.GetLocalization($"{nameof(YarnResearchGlobalItem)}.ToggleInfiniteBuffHint");
			OpenPrefixPickerHintText = Mod.GetLocalization($"{nameof(YarnResearchGlobalItem)}.OpenPrefixPickerHint");
			PrefixPickerPopupSetDefaultHintText = Mod.GetLocalization($"{nameof(YarnResearchGlobalItem)}.PrefixPickerPopupSetDefaultHint");
			PrefixPickerPopupDuplicateHintText = Mod.GetLocalization($"{nameof(YarnResearchGlobalItem)}.PrefixPickerPopupDuplicateHint");
		}

		// Journey duplication doesn't roll a prefix through ChoosePrefix - the duplicate is a straight
		// Clone() of the source item - so this is the only hook point that can override it.
		public override void OnCreated(Item item, ItemCreationContext context)
		{
			if (context is not JourneyDuplicationItemCreationContext)
			{
				// call the base trigger to ensure normal behaviour like prefixes on craft work
				base.OnCreated(item, context);
				return;
			}

			// Preserve armed/default prefixes across a Goblin Tinkerer death rather than clearing them -
			// just don't apply them while he's gone, matching vanilla's own reroll gating.
			if (!NPC.AnyNPCs(NPCID.GoblinTinkerer))
				return;

			YarnResearchPlayer player = Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>();

			// A consumed one-shot is honoured whatever it is and always wins - including "no modifier", which
			// has to stop here rather than fall through to the group default the player was overriding.
			if (player.TryConsumeOneShotPrefix(item.type, out int oneShotPrefix))
			{
				if (oneShotPrefix != PrefixTrial.NoPrefixId && item.CanRollPrefix(oneShotPrefix))
					item.Prefix(oneShotPrefix);

				return;
			}

			if (player.TryGetDefaultPrefix(item, out int defaultPrefix))
				item.Prefix(defaultPrefix);
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
			{
				ResearchCascadeSystem.HandleResearched(item.type);
				InfiniteBuffSystem.HandleItemResearched(item);
			}
			else
			{
				YarnNetwork.SendPartialResearch(item.type);
			}
		}

		public override void ModifyTooltips(Item item, List<TooltipLine> tooltips)
		{
			AddCrateContentsHint(item, tooltips);
			AddInfiniteBuffHint(item, tooltips);
			AddOpenPrefixPickerHint(item, tooltips);
			AddPrefixPickerPopupControlsHint(item, tooltips);
		}

		private void AddCrateContentsHint(Item item, List<TooltipLine> tooltips)
		{
			if (!ItemID.Sets.OpenableBag[item.type] || !ResearchCascadeSystem.IsResearched(item.type))
				return;

			if (!ResearchCascadeSystem.HasUnresearchedCrateContents(item.type))
				return;

			List<string> keys = ResearchCascadeSystem.ResearchCrateContentsKeybind.GetAssignedKeys();
			if (keys.Count == 0)
				return;

			tooltips.Add(new TooltipLine(Mod, "ResearchCrateContentsHint", ResearchCrateContentsHintText.Format(keys[0]))
			{
				Color = YarnColors.TooltipHint
			});
		}

		// Scoped to the duplication grid for the same reason as AddOpenPrefixPickerHint below - the hotkey
		// this advertises only acts on a hovered duplication-grid item.
		private void AddInfiniteBuffHint(Item item, List<TooltipLine> tooltips)
		{
			if (!DuplicationHoverSystem.IsHoveringSlot || !InfiniteBuffSystem.IsToggleable(item))
				return;

			List<string> keys = InfiniteBuffSystem.ToggleInfiniteBuffKeybind.GetAssignedKeys();
			if (keys.Count == 0)
				return;

			tooltips.Add(new TooltipLine(Mod, "ToggleInfiniteBuffHint", ToggleInfiniteBuffHintText.Format(keys[0]))
			{
				Color = YarnColors.TooltipHint
			});
		}

		// Only within the duplication grid itself, not any hovered item while the Journey power menu
		// happens to be open elsewhere (e.g. the player's ordinary inventory) - see PrefixPickerSystem.
		private void AddOpenPrefixPickerHint(Item item, List<TooltipLine> tooltips)
		{
			if (!DuplicationHoverSystem.IsHoveringSlot || !NPC.AnyNPCs(NPCID.GoblinTinkerer))
				return;

			List<string> keys = PrefixPickerSystem.OpenPrefixPickerKeybind.GetAssignedKeys();
			if (keys.Count == 0 || PrefixCandidate.GetCandidates(item).Count == 0)
				return;

			tooltips.Add(new TooltipLine(Mod, "OpenPrefixPickerHint", OpenPrefixPickerHintText.Format(keys[0]))
			{
				Color = YarnColors.TooltipHint
			});
		}

		// Only on the synthetic preview item shown while hovering a row in the prefix picker popup itself -
		// see PrefixPickerSystem.IsPreviewingPopupRow.
		private void AddPrefixPickerPopupControlsHint(Item item, List<TooltipLine> tooltips)
		{
			if (!PrefixPickerSystem.IsPreviewingPopupRow)
				return;

			tooltips.Add(new TooltipLine(Mod, "PrefixPickerPopupSetDefaultHint", PrefixPickerPopupSetDefaultHintText.Value)
			{
				Color = YarnColors.TooltipHint
			});
			tooltips.Add(new TooltipLine(Mod, "PrefixPickerPopupDuplicateHint", PrefixPickerPopupDuplicateHintText.Value)
			{
				Color = YarnColors.TooltipHint
			});
		}
	}
}
