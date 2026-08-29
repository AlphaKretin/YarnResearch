using Microsoft.Xna.Framework;
using Terraria.Audio;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using YarnResearch.Common.Players;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.UI.ManualTriggersUI
{
	public class HeldItemsActionPower : AYarnActionPower
	{
		private static readonly LocalizedText HoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(HeldItemsActionPower)}.HoverText");

		protected override YarnIcon Icon => YarnIcons.HeldItemsScan;
		public override LocalizedText HoverText => HoverTextValue;

		protected override void DoAction()
		{
			YarnResearchPlayer.ManualScan();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}
	}

	public class CascadeActionPower : AYarnActionPower
	{
		private static readonly LocalizedText HoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(CascadeActionPower)}.HoverText");

		protected override YarnIcon Icon => YarnIcons.CraftableScan;
		public override LocalizedText HoverText => HoverTextValue;

		protected override void DoAction()
		{
			ResearchCascadeSystem.ManualCascadeScan();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}
	}

	public class MiscCascadeActionPower : AYarnActionPower
	{
		private static readonly LocalizedText HoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(MiscCascadeActionPower)}.HoverText");

		protected override YarnIcon Icon => YarnIcons.MiscScan;
		public override LocalizedText HoverText => HoverTextValue;

		protected override void DoAction()
		{
			ResearchCascadeSystem.ManualMiscCascadeScan();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}
	}

	public class ShimmerActionPower : AYarnActionPower
	{
		private static readonly LocalizedText HoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(ShimmerActionPower)}.HoverText");

		protected override YarnIcon Icon => YarnIcons.ShimmerScan;
		public override LocalizedText HoverText => HoverTextValue;

		public override bool GetIsUnlocked() => ResearchCascadeSystem.ShimmerDiscovered;

		protected override void DoAction()
		{
			ResearchCascadeSystem.RunShimmerCatchupScan();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}
	}

	// The one power here that toggles rather than acting once - see AYarnTogglePower.
	public class FreeCraftingTogglePower : AYarnTogglePower
	{
		private static readonly LocalizedText HoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(FreeCraftingTogglePower)}.HoverText");
		private static readonly LocalizedText ActiveHoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(FreeCraftingTogglePower)}.ActiveHoverText");

		protected override YarnIcon Icon => YarnIcons.FreeCrafting;
		public override LocalizedText HoverText => FreeCraftingSystem.Enabled ? ActiveHoverTextValue : HoverTextValue;

		protected override bool IsOn => FreeCraftingSystem.Enabled;

		protected override void DoAction()
		{
			FreeCraftingSystem.Toggle();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}
	}

	// Destructive/irreversible, so the first click only arms a short confirm window (see ConfirmGuard) -
	// the actual action only fires on a second click within that window.
	public class SacrificeActionPower : AYarnActionPower
	{
		private static readonly LocalizedText HoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(SacrificeActionPower)}.HoverText");
		private static readonly LocalizedText ArmedHoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(SacrificeActionPower)}.ArmedHoverText");

		private readonly ConfirmGuard _guard = new();

		protected override YarnIcon Icon => YarnIcons.ConsumeUnresearched;

		public override LocalizedText HoverText => _guard.Armed ? ArmedHoverTextValue : HoverTextValue;

		protected override void DoAction()
		{
			if (_guard.Click()) {
				if (YarnResearchPlayer.BulkSacrificeUnresearched())
					SoundEngine.PlaySound(SoundID.Research);
			}
			else {
				SoundEngine.PlaySound(SoundID.MenuTick);
			}
		}

		public override void PerTickUpdate()
		{
			_guard.Update();
			if (IconElement != null)
				IconElement.IconTint = _guard.Armed ? YarnColors.ArmedActionIcon : Color.White;
		}

		public override void OnClosed() => _guard.Disarm();
	}

	// Destructive/irreversible, so the first click only arms a short confirm window (see ConfirmGuard) -
	// the actual action only fires on a second click within that window.
	public class ClearActionPower : AYarnActionPower
	{
		private static readonly LocalizedText HoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(ClearActionPower)}.HoverText");
		private static readonly LocalizedText ArmedHoverTextValue = ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(ClearActionPower)}.ArmedHoverText");

		private readonly ConfirmGuard _guard = new();

		// No named SoundID exists for the inventory trash-slot sound - built directly from the underlying
		// asset path instead, matching the two extracted variants Custom/trash_item_0 and _1.
		private static readonly SoundStyle TrashSound = new("Terraria/Sounds/Custom/trash_item_", 0, 2) { LimitsArePerVariant = true };

		protected override YarnIcon Icon => YarnIcons.ClearResearched;

		public override LocalizedText HoverText => _guard.Armed ? ArmedHoverTextValue : HoverTextValue;

		protected override void DoAction()
		{
			if (_guard.Click()) {
				if (YarnResearchPlayer.BulkClearResearched())
					SoundEngine.PlaySound(TrashSound);
			}
			else {
				SoundEngine.PlaySound(SoundID.MenuTick);
			}
		}

		public override void PerTickUpdate()
		{
			_guard.Update();
			if (IconElement != null)
				IconElement.IconTint = _guard.Armed ? YarnColors.ArmedActionIcon : Color.White;
		}

		public override void OnClosed() => _guard.Disarm();
	}
}
