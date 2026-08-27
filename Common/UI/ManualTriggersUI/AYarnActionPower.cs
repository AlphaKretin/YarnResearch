using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Creative;
using Terraria.GameContent.UI.Elements;
using Terraria.Localization;
using Terraria.UI;

namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// Shared base for the mod's Creative Powers, mirroring vanilla's own ASharedButtonPower ->
	// StartDayImmediately/etc. shape. ASharedButtonPower's own OnCreation/UsePower are internal to the game
	// assembly, so a real subclass of it isn't possible from a mod - this recreates the same
	// one-base/many-leaves relationship as a class this mod fully owns instead. Registering real
	// ICreativePower instances (via CreativePowerManager.Register<T>) gets the same first-class registration
	// vanilla's own powers use.
	//
	// Non-generic so the strip can treat every power uniformly regardless of its button's option type - a
	// one-shot action button and a toggle button use different GroupOptionButton<T> instantiations (see
	// AYarnPower<TOption> and AYarnTogglePower).
	public abstract class AYarnPower : ICreativePower
	{
		public ushort PowerId { get; set; }
		public string ServerConfigName { get; set; }
		public PowerPermissionLevel CurrentPermissionLevel { get; set; }
		public PowerPermissionLevel DefaultPermissionLevel { get; set; }

		public ItemIconButton IconElement { get; protected set; }

		protected abstract Asset<Texture2D> Icon { get; }
		protected virtual Rectangle? IconFrame => null;

		// Vanilla's own power icons bake a drop shadow into the image itself - only needed here for an
		// icon (an item sprite, or the trash-slot icon) that doesn't already have one baked in.
		protected virtual bool DrawIconDropShadow => true;

		public abstract LocalizedText HoverText { get; }

		protected abstract void DoAction();

		public virtual bool GetIsUnlocked() => true;

		// The built button, as a plain UIElement - all the strip needs it for is layout, hover detection,
		// and appending/removing.
		public abstract UIElement ButtonElement { get; }

		public abstract void ProvidePowerButtons(CreativePowerUIElementRequestInfo info, List<UIElement> elements);

		// An item's own inventory sprite, for a power whose icon is just that item.
		protected static Asset<Texture2D> GetItemIcon(int itemType)
		{
			Main.instance.LoadItem(itemType);
			return TextureAssets.Item[itemType];
		}

		// No cross-client sync needed - every action here (scanning/sacrificing/clearing the local
		// player's own inventory, or toggling free crafting) only ever runs on the clicking client.
		public void DeserializeNetMessage(BinaryReader reader, int whoAmI)
		{
		}

		// Builds the button without appending it to a visible strip yet - used for a power (like the shimmer
		// action) that starts locked and needs its button ready to insert later, once unlocked.
		public void EnsureButtonBuilt(CreativePowerUIElementRequestInfo info)
		{
			if (ButtonElement == null)
				ProvidePowerButtons(info, new List<UIElement>());
		}

		// Called once per game tick while the YARN strip is open - overridden by the destructive actions
		// to tick their confirm guard and retint the icon while armed, and by a toggle to keep its button's
		// picked state in sync.
		public virtual void PerTickUpdate()
		{
		}

		// Called when the strip closes (category switches away) - overridden to disarm a confirm guard
		// early rather than leaving it armed for the next time the strip opens.
		public virtual void OnClosed()
		{
		}
	}

	// Builds the actual GroupOptionButton. TOption is whatever the matching real vanilla button uses: int
	// for a one-shot action button, bool for a toggle.
	public abstract class AYarnPower<TOption> : AYarnPower
	{
		public GroupOptionButton<TOption> Button { get; private set; }

		public override UIElement ButtonElement => Button;

		// The value this button represents. It renders as picked whenever the button's current option
		// matches this.
		protected abstract TOption MyOption { get; }

		// The current-option value that reads as "not picked", stamped on at construction.
		protected abstract TOption UnpickedOption { get; }

		public override void ProvidePowerButtons(CreativePowerUIElementRequestInfo info, List<UIElement> elements)
		{
			Button = new GroupOptionButton<TOption>(
				option: MyOption,
				title: null,
				description: null,
				textColor: Color.White,
				iconTexturePath: null,
				textSize: 1f,
				titleAlignmentX: 0.5f,
				titleWidthReduction: 0f);

			Button.Width.Set(info.PreferredButtonWidth, 0f);
			Button.Height.Set(info.PreferredButtonHeight, 0f);

			// A fresh GroupOptionButton self-matches its current option to its own value, which reads as
			// permanently selected. Real vanilla buttons don't, so stamp the unpicked value on instead.
			Button.SetCurrentOption(UnpickedOption);

			Button.OnLeftClick += (evt, listeningElement) => DoAction();

			// A separate icon child rather than GroupOptionButton's built-in SetIcon/SetIconFrame: no real
			// vanilla button uses those, and doing so here made the icon translucent once the real
			// override-opacity fields were copied on, since the button's own fade state bleeds into its
			// built-in icon draw.
			IconElement = new ItemIconButton(Icon, sourceRect: IconFrame, drawDropShadow: DrawIconDropShadow) {
				IgnoresMouseInteraction = true,
			};

			if (IconFrame is Rectangle frame) {
				// The real Open Research Menu button (same Infinite_Powers gear icon, same frame) draws its
				// icon at native size flush against the button's bottom-right corner rather than scaling it
				// to fill: a 36x36 icon inset exactly 4px from a 40x40 button's top-left.
				IconElement.Left.Set(info.PreferredButtonWidth - frame.Width, 0f);
				IconElement.Top.Set(info.PreferredButtonHeight - frame.Height, 0f);
				IconElement.Width.Set(frame.Width, 0f);
				IconElement.Height.Set(frame.Height, 0f);
			}
			else {
				IconElement.Width.Set(0f, 1f);
				IconElement.Height.Set(0f, 1f);
			}

			Button.Append(IconElement);

			elements.Add(Button);
		}
	}

	// A one-shot action button, matching the real GroupOptionButton<int> in vanilla's Time strip: its option
	// is its own power id, and it never stays picked.
	public abstract class AYarnActionPower : AYarnPower<int>
	{
		protected override int MyOption => PowerId;
		protected override int UnpickedOption => 0;
	}
}
