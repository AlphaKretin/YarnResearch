using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.GameContent.Creative;
using Terraria.ID;
using Terraria.ModLoader;
using YarnResearch.Common.Configs;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.Net
{
	public enum YarnMessageType : byte
	{
		AutoResearchNotification,
		PartialResearch,
		ShimmerDiscovered,
		TeamCatchupRequest,
		TeamResearchList
	}

	// Every YARN packet is [message type][sender player index][payload length][payload], so the server can
	// relay one to the sender's teammates without knowing what any particular message contains - it reads
	// the payload as opaque bytes and re-emits it with the sender index it can actually trust.
	public static class YarnNetwork
	{
		// Item types per packet. A catch-up scan can announce thousands at once, which would overrun the
		// 64KB packet ceiling as a single message.
		private const int MaxTypesPerPacket = 1000;

		public static void HandlePacket(BinaryReader reader, int whoAmI)
		{
			var messageType = (YarnMessageType)reader.ReadByte();
			int sender = reader.ReadByte();
			int payloadLength = reader.ReadUInt16();
			byte[] payload = reader.ReadBytes(payloadLength);

			if (Main.netMode == NetmodeID.Server)
			{
				RelayToTeammates(messageType, whoAmI, payload);
				return;
			}

			using var payloadReader = new BinaryReader(new MemoryStream(payload));

			switch (messageType)
			{
				case YarnMessageType.AutoResearchNotification:
					ReceiveAutoResearchNotification(payloadReader, sender);
					break;
				case YarnMessageType.PartialResearch:
					ReceivePartialResearch(payloadReader);
					break;
				case YarnMessageType.ShimmerDiscovered:
					ReceiveShimmerDiscovered();
					break;
				case YarnMessageType.TeamCatchupRequest:
					ReceiveTeamCatchupRequest(sender);
					break;
				case YarnMessageType.TeamResearchList:
					ReceiveTeamResearchList(payloadReader);
					break;
			}
		}

		// Vanilla only shares research between players on the same team, so anyone else has no shared
		// research to be told about. Team 0 is "no team", which shares nothing.
		private static void RelayToTeammates(YarnMessageType messageType, int sender, byte[] payload)
		{
			int team = Main.player[sender].team;
			if (team == 0)
				return;

			for (int i = 0; i < Main.maxPlayers; i++)
			{
				if (i == sender || !Main.player[i].active || Main.player[i].team != team)
					continue;

				ModPacket packet = NewPacket(messageType, sender, payload.Length);
				packet.Write(payload);
				packet.Send(toClient: i);
			}
		}

		private static ModPacket NewPacket(YarnMessageType messageType, int sender, int payloadLength)
		{
			ModPacket packet = ModContent.GetInstance<YarnResearch>().GetPacket();
			packet.Write((byte)messageType);
			packet.Write((byte)sender);
			packet.Write((ushort)payloadLength);
			return packet;
		}

		// Mirrors one origin's chat notification to the sender's teammates. Called from the sender's own
		// notification flush, so automatic research inherits that player's ShowAutoResearchNotifications
		// preference; research done by hand is always sent and left to the receiver's setting.
		public static void SendAutoResearchNotification(int origin, IReadOnlyCollection<int> types)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || types.Count == 0)
				return;

			SendTypeList(YarnMessageType.AutoResearchNotification, (byte)origin, types);
		}

		// Sends types as [header byte][count][types] payloads, split across as many packets as needed.
		private static void SendTypeList(YarnMessageType messageType, byte header, IEnumerable<int> types)
		{
			var chunk = new List<int>(MaxTypesPerPacket);

			foreach (int type in types)
			{
				chunk.Add(type);

				if (chunk.Count == MaxTypesPerPacket)
				{
					SendTypeChunk(messageType, header, chunk);
					chunk.Clear();
				}
			}

			if (chunk.Count > 0)
				SendTypeChunk(messageType, header, chunk);
		}

		private static void SendTypeChunk(YarnMessageType messageType, byte header, List<int> types)
		{
			var payload = new MemoryStream();
			using (var writer = new BinaryWriter(payload))
			{
				writer.Write(header);
				writer.Write((ushort)types.Count);

				foreach (int type in types)
					writer.Write(type);
			}

			byte[] bytes = payload.ToArray();
			ModPacket packet = NewPacket(messageType, Main.myPlayer, bytes.Length);
			packet.Write(bytes);
			packet.Send();
		}

		private static List<int> ReadTypeList(BinaryReader reader)
		{
			int count = reader.ReadUInt16();

			var types = new List<int>(count);
			for (int i = 0; i < count; i++)
				types.Add(reader.ReadInt32());

			return types;
		}

		private static void ReceiveAutoResearchNotification(BinaryReader reader, int sender)
		{
			int origin = reader.ReadByte();
			ResearchCascadeSystem.AnnounceTeammateResearch(sender, origin, ReadTypeList(reader));
		}

		public static void SendTeamCatchupRequest()
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			// no payload - the server relays it to the sender's teammates, and the sender index says who asked
			ModPacket packet = NewPacket(YarnMessageType.TeamCatchupRequest, Main.myPlayer, 0);
			packet.Send();
		}

		// Answers with everything this player has fully researched. The reply is relayed to the whole team,
		// so it carries the requester's index and everyone else ignores it.
		private static void ReceiveTeamCatchupRequest(int requester)
		{
			ItemsSacrificedUnlocksTracker tracker = Main.LocalPlayerCreativeTracker.ItemSacrifices;
			var researched = new List<int>();

			tracker.ForEachItemWithResearchProgress(type =>
			{
				if (tracker.IsFullyResearched(type))
					researched.Add(type);
			});

			SendTypeList(YarnMessageType.TeamResearchList, (byte)requester, researched);
		}

		private static void ReceiveTeamResearchList(BinaryReader reader)
		{
			int requester = reader.ReadByte();
			List<int> types = ReadTypeList(reader);

			if (requester == Main.myPlayer)
				ResearchCascadeSystem.ResearchTeammateTypes(types);
		}

		public static void SendPartialResearch(int type)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (!config.SharePartialResearch)
				return;
			ItemsSacrificedUnlocksTracker tracker = Main.LocalPlayerCreativeTracker.ItemSacrifices;
			int count = tracker.GetSacrificeCount(type);

			var payload = new MemoryStream();
			using (var writer = new BinaryWriter(payload))
			{
				writer.Write(type);
				writer.Write(count);
			}

			byte[] bytes = payload.ToArray();
			ModPacket packet = NewPacket(YarnMessageType.PartialResearch, Main.myPlayer, bytes.Length);
			packet.Write(bytes);
			packet.Send();
		}

		private static void ReceivePartialResearch(BinaryReader reader)
		{
			int type = reader.ReadInt32();
			int newCount = reader.ReadInt32();

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

				ResearchCascadeSystem.ApplyingSharedResearch = true;
				try
				{
					Main.CreativeMenu.SacrificeItem(ref fodder, out _, spawnExcessItem: false, onlySacrificeIfItWouldFinishResearch: false);
				}
				finally
				{
					ResearchCascadeSystem.ApplyingSharedResearch = false;
				}
			}
		}

		public static void SendShimmerDiscovered()
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			// no payload, pure notification
			ModPacket packet = NewPacket(YarnMessageType.ShimmerDiscovered, Main.myPlayer, 0);
			packet.Send();
		}

		private static void ReceiveShimmerDiscovered()
		{
			ResearchCascadeSystem.MarkShimmerDiscovered();
		}
	}
}
