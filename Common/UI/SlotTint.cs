using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;

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
	public class SlotTint : ModSystem
	{
		// The background is drawn larger than the icon's own draw box so it reads as the tile behind the
		// icon rather than a same-size copy peeking out from the edges.
		public const float BackgroundPadding = 1.6f;

		private static Texture2D _neutralBackground;

		public override void Unload()
		{
			_neutralBackground?.Dispose();
			_neutralBackground = null;
		}

		// iconSize is the icon's own max draw box (ItemSlot.DrawItemIcon's sizeLimit * scale).
		public static void Draw(SpriteBatch spriteBatch, Vector2 center, float iconSize, Color tint)
		{
			Texture2D background = NeutralBackground();
			float scale = iconSize * BackgroundPadding / background.Width;
			var origin = new Vector2(background.Width, background.Height) / 2f;
			spriteBatch.Draw(background, center, null, tint, 0f, origin, scale, SpriteEffects.None, 0f);
		}

		// A greyscale copy of the slot background. SpriteBatch tints by multiplying, so drawing vanilla's
		// own blue slot texture filters every tint through that blue - yellow comes out a muddy green, and
		// no colour reads as the one that was asked for. Flattening the source to luminance keeps the
		// slot's shading and shape while leaving the tint free to supply the hue on its own.
		private static Texture2D NeutralBackground()
		{
			if (_neutralBackground != null)
				return _neutralBackground;

			Texture2D source = TextureAssets.InventoryBack9.Value;
			var pixels = new Color[source.Width * source.Height];
			source.GetData(pixels);

			for (int i = 0; i < pixels.Length; i++)
			{
				Color pixel = pixels[i];

				// Rec. 601 luma. The source is premultiplied, so weighting its channels keeps the result at
				// or under the pixel's own alpha and premultiplied for the tint to multiply into.
				byte luma = (byte)((pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000);
				pixels[i] = new Color(luma, luma, luma, pixel.A);
			}

			_neutralBackground = new Texture2D(Main.graphics.GraphicsDevice, source.Width, source.Height);
			_neutralBackground.SetData(pixels);
			return _neutralBackground;
		}
	}
}
