// two important notes:
// vanilla already syncs research upon completion if you're on the same team at that moment
// and the notifications for cascades are handled in the cascade system
// this system covers the gaps: sharing partial research, catching up lagging research

using Terraria.ModLoader;
using Terraria;
using Terraria.GameContent.Creative;
using YarnResearch.Common.Net;
using YarnResearch.Common.Configs;

namespace YarnResearch.Common.Systems
{
    public class MultiplayerSyncSystem : ModSystem
    {
        public static void SharePartialResearch(Item item)
        {
            var config = ModContent.GetInstance<YarnResearchConfig>();
            if (!config.SharePartialResearch)
                return;
            ItemsSacrificedUnlocksTracker tracker = Main.LocalPlayerCreativeTracker.ItemSacrifices;
            int count = tracker.GetSacrificeCount(item.type);
            YarnNetwork.SendPartialResearch(item.type, count);
        }

        public static void ReceivePartialResearch(int type, int newCount)
        {
            var config = ModContent.GetInstance<YarnResearchConfig>();
            ItemsSacrificedUnlocksTracker tracker = Main.LocalPlayerCreativeTracker.ItemSacrifices;
            int count = tracker.GetSacrificeCount(type);
            if (newCount > count && config.SharePartialResearch)
            {
                // sac the same amount of the same item to catch up
                int sacCount = newCount - count;
                var fodder = new Item();
                fodder.SetDefaults(type);
                fodder.stack = sacCount;
                Main.CreativeMenu.SacrificeItem(ref fodder, out _, spawnExcessItem: false, onlySacrificeIfItWouldFinishResearch: false);
            }
        }
    }
}