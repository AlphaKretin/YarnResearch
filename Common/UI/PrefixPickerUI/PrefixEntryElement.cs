using Microsoft.Xna.Framework.Graphics;
using System;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.ModLoader.UI;
using Terraria.UI;
using YarnResearch.Common.Players;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.UI.PrefixPickerUI
{
	// One row in the prefix picker popup - left-click sets the persistent per-category default,
	// right-click immediately duplicates this item type with this prefix.
	public class PrefixEntryElement : UIPanel
	{
		private readonly PrefixCandidate _candidate;
		private readonly int _itemType;
		private readonly Action _onSelected;

		public PrefixEntryElement(PrefixCandidate candidate, int itemType, Action onSelected)
		{
			_candidate = candidate;
			_itemType = itemType;
			_onSelected = onSelected;

			Width.Set(0f, 1f);
			Height.Set(36f, 0f);
			SetPadding(6f);

			var text = new UIText(candidate.DisplayName) {
				VAlign = 0.5f
			};
			Append(text);

			// UICommon's own standard menu-item hover treatment (background/border color swap + the
			// vanilla menu-tick sound) rather than a hand-rolled highlight - same helper vanilla's own
			// UI (mod browser, mod config lists, etc.) uses for exactly this.
			this.WithFadedMouseOver();
		}

		// Previews this row's exact prefixed result via the tooltip, same mechanism the duplication grid's
		// own hover-preview uses (PrefixPickerSystem.ApplyHoverPreview). Must be done from DrawSelf:
		// IsMouseHovering is only valid by Draw time, not during this element's own Update.
		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);

			if (IsMouseHovering)
				PrefixPickerSystem.SetHoverPreview(_itemType, _candidate.PrefixId);
		}

		public override void LeftClick(UIMouseEvent evt)
		{
			base.LeftClick(evt);
			Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>().SetDefaultPrefix(PrefixGroup.OfType(_itemType), _candidate.PrefixId);
			_onSelected();
		}

		public override void RightClick(UIMouseEvent evt)
		{
			base.RightClick(evt);
			PrefixPickerSystem.DuplicateWithPrefix(_itemType, _candidate.PrefixId);
			_onSelected();
		}
	}
}
