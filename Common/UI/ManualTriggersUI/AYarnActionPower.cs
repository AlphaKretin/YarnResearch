using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria.GameContent.Creative;
using Terraria.GameContent.UI.Elements;
using Terraria.Localization;
using Terraria.UI;

namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// Shared base for the mod's 5 action-button Creative Powers, mirroring vanilla's own
	// ASharedButtonPower -> StartDayImmediately/StartNoonImmediately/etc. shape. ASharedButtonPower's own
	// OnCreation/UsePower are internal to the game assembly, so a real subclass of it isn't possible from
	// a mod - this recreates the same one-base/many-leaves relationship as a class this mod fully owns
	// instead. Registering real ICreativePower instances (via CreativePowerManager.Register<T>) gets the
	// same first-class registration vanilla's own powers use.
	public abstract class AYarnActionPower : ICreativePower
	{
		public ushort PowerId { get; set; }
		public string ServerConfigName { get; set; }
		public PowerPermissionLevel CurrentPermissionLevel { get; set; }
		public PowerPermissionLevel DefaultPermissionLevel { get; set; }

		public GroupOptionButton<int> Button { get; private set; }
		public ItemIconButton IconElement { get; private set; }

		protected abstract Asset<Texture2D> Icon { get; }
		protected virtual Rectangle? IconFrame => null;

		// Vanilla's own power icons bake a drop shadow into the image itself - only needed here for an
		// icon (an item sprite, or the trash-slot icon) that doesn't already have one baked in.
		protected virtual bool DrawIconDropShadow => true;

		public abstract LocalizedText HoverText { get; }

		protected abstract void DoAction();

		public virtual bool GetIsUnlocked() => true;

		// No cross-client sync needed - every action here (scanning/sacrificing/clearing the local
		// player's own inventory) only ever runs on the clicking client.
		public void DeserializeNetMessage(BinaryReader reader, int whoAmI)
		{
		}

		public void ProvidePowerButtons(CreativePowerUIElementRequestInfo info, List<UIElement> elements)
		{
			Button = new GroupOptionButton<int>(
				option: PowerId,
				title: null,
				description: null,
				textColor: Color.White,
				iconTexturePath: null,
				textSize: 1f,
				titleAlignmentX: 0.5f,
				titleWidthReduction: 0f);

			Button.Width.Set(info.PreferredButtonWidth, 0f);
			Button.Height.Set(info.PreferredButtonHeight, 0f);

			// A real vanilla action button's _currentOption (0) does not self-match its own _myOption -
			// something explicitly resets it after construction, so do the same here via the real public
			// setter instead of leaving it self-matched (which reads as permanently selected).
			Button.SetCurrentOption(0);

			Button.OnLeftClick += (evt, listeningElement) => DoAction();

			// GroupOptionButton's own built-in icon rendering (SetIcon/SetIconFrame) isn't what real
			// vanilla buttons use - no real button across Time/Personal/Weather has a non-null _iconFrame,
			// and using it here caused translucency once the real override-opacity fields (below) were
			// copied on, since the button's own fade/opacity state bleeds into its built-in icon draw. A
			// separate icon child, same pattern as the category button's own icon, avoids that.
			IconElement = new ItemIconButton(Icon, hoverText: null, drawBackground: false, sourceRect: IconFrame, drawDropShadow: DrawIconDropShadow) {
				IgnoresMouseInteraction = true,
			};

			if (IconFrame is Rectangle frame) {
				// The real Open Research Menu button (same Infinite_Powers gear icon, same frame) doesn't
				// scale its icon to fill the button - it draws it at native size, flush against the
				// button's bottom-right corner (a live comparison found a 36x36 icon inset exactly 4px
				// from a 40x40 button's top-left, with zero margin on the right/bottom).
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

		// Builds Button without appending it to a visible strip yet - used for a power (like the shimmer
		// action) that starts locked and needs its button ready to insert later, once unlocked.
		public void EnsureButtonBuilt(CreativePowerUIElementRequestInfo info)
		{
			if (Button == null)
				ProvidePowerButtons(info, new List<UIElement>());
		}

		// Called once per game tick while the YARN strip is open - overridden by the destructive actions
		// to tick their confirm guard and retint the icon while armed.
		public virtual void PerTickUpdate()
		{
		}

		// Called when the strip closes (category switches away) - overridden to disarm a confirm guard
		// early rather than leaving it armed for the next time the strip opens.
		public virtual void OnClosed()
		{
		}
	}
}
