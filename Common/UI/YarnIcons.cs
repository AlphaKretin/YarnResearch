using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace YarnResearch.Common.UI
{
	// One icon for the mod's custom UI, as either an item's own inventory sprite or a path into the game's
	// image assets. The two are requested differently, so the entry records which kind it is and resolves
	// itself on demand - both sources need the game's content to be loaded, so an entry can't hold a texture
	// outright.
	public readonly struct YarnIcon
	{
		private readonly int _itemType;
		private readonly string _texturePath;

		// One frame out of a multi-icon spritesheet, or null to draw the whole texture. Only an explicitly
		// given frame counts here - it also marks an icon as wanting native-size placement rather than
		// filling its button.
		public Rectangle? Frame { get; }

		// What to actually draw from the texture. An item with no explicit frame defers to the rectangle
		// vanilla itself draws that item with, which slices an animated item's sheet down to a single frame
		// instead of drawing the whole sheet.
		public Rectangle SourceRectangle =>
			Frame ?? (_itemType >= 0 ? Item.GetDrawHitbox(_itemType, null) : Texture.Value.Bounds);

		// How large an icon draws relative to its sprite's native size, before the fit-to-button clamp.
		// Item sprites vary far more in size than the UI textures do, so they get their own default factor,
		// and an individual icon can override either to sit deliberately larger or smaller than its peers.
		public float DrawScale => _scale ?? (_itemType >= 0 ? ItemIconScale : TextureIconScale);

		private const float ItemIconScale = 0.9f;
		private const float TextureIconScale = 1f;

		// Draws a second, offset dark copy underneath. Vanilla's own power icons bake a drop shadow into
		// the image itself, so this is only wanted for an icon that doesn't already have one.
		public bool DrawDropShadow { get; }

		private readonly float? _scale;

		private YarnIcon(int itemType, string texturePath, Rectangle? frame, float? scale, bool drawDropShadow)
		{
			_scale = scale;
			_itemType = itemType;
			_texturePath = texturePath;
			Frame = frame;
			DrawDropShadow = drawDropShadow;
		}

		public static YarnIcon FromItem(int itemType, float? scale = null, bool drawDropShadow = true) =>
			new(itemType, null, null, scale, drawDropShadow);

		public static YarnIcon FromTexture(string texturePath, Rectangle? frame = null, float? scale = null, bool drawDropShadow = true) =>
			new(-1, texturePath, frame, scale, drawDropShadow);

		public Asset<Texture2D> Texture
		{
			get
			{
				if (_texturePath != null)
					return ModContent.Request<Texture2D>(_texturePath, AssetRequestMode.ImmediateLoad);

				// An item's sprite is only loaded on demand, so an item never seen in this session would
				// otherwise resolve to a blank texture.
				Main.instance.LoadItem(_itemType);
				return TextureAssets.Item[_itemType];
			}
		}
	}

	// Every icon the mod picks for itself, in one place so they can be compared and swapped together.
	public static class YarnIcons
	{
		public static readonly YarnIcon YarnCategory = YarnIcon.FromItem(ItemID.UnluckyYarn, scale: 1.2f);

		public static readonly YarnIcon HeldItemsScan = YarnIcon.FromItem(ItemID.Binoculars);
		public static readonly YarnIcon CraftableScan = YarnIcon.FromItem(ItemID.WorkBench);
		public static readonly YarnIcon ShimmerScan = YarnIcon.FromItem(ItemID.BottomlessShimmerBucket);
		public static readonly YarnIcon MiscScan = YarnIcon.FromItem(ItemID.GoodieBag);
		public static readonly YarnIcon FreeCrafting = YarnIcon.FromItem(ItemID.HandOfCreation);

		// The vanilla powers menu's own research-gear icon: frame index 1 of a 21-frame, 36x36-per-frame
		// spritesheet, with a drop shadow already baked in.
		public static readonly YarnIcon ConsumeUnresearched =
			YarnIcon.FromTexture("Terraria/Images/UI/Creative/Infinite_Powers", new Rectangle(36, 0, 36, 36), drawDropShadow: false);

		// The inventory trash-slot icon, at Content/Images/Trash.xnb.
		public static readonly YarnIcon ClearResearched = YarnIcon.FromTexture("Terraria/Images/Trash");
	}
}
