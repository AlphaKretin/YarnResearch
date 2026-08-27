using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria.GameContent;

namespace YarnResearch.Common.UI
{
	// Draws a tinted slot-background texture behind an item icon in the Journey Mode duplication grid,
	// marking it as carrying some mod state (an infinite buff, an armed prefix). Shared by every system
	// that marks a slot so they agree on geometry - PrefixPickerSystem's hover test widens the icon box by
	// the same padding factor, and would drift from the drawn tint if either owned its own copy.
	//
	// The pattern (drawing TextureAssets.InventoryBack* directly with an arbitrary tint, centered and
	// scaled by hand) follows AutoTrash's ItemSlot.cs, one of the mods the tModLoader wiki's
	// Open-Source-Mods page names as citable reference material.
	public static class SlotTint
	{
		// The background is drawn larger than the icon's own draw box so it reads as the tile behind the
		// icon rather than a same-size copy peeking out from the edges.
		public const float BackgroundPadding = 1.6f;

		// iconSize is the icon's own max draw box (ItemSlot.DrawItemIcon's sizeLimit * scale).
		public static void Draw(SpriteBatch spriteBatch, Vector2 center, float iconSize, Color tint)
		{
			Texture2D background = TextureAssets.InventoryBack9.Value;
			float scale = iconSize * BackgroundPadding / background.Width;
			var origin = new Vector2(background.Width, background.Height) / 2f;
			spriteBatch.Draw(background, center, null, tint, 0f, origin, scale, SpriteEffects.None, 0f);
		}
	}
}
