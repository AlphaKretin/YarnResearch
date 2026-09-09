using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.Net
{
	public enum YarnMessageType : byte
	{
		AutoResearchNotification,
		PartialResearch,
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

		// Mirrors one origin's chat notification to the sender's teammates, so research YARN performed
		// automatically announces itself on both ends - matching what the sender just saw - while research
		// they did by hand stays silent, exactly as vanilla leaves it. Called from the sender's own
		// notification flush, so it inherits that player's ShowAutoResearchNotifications preference.
		public static void SendAutoResearchNotification(int origin, IReadOnlyCollection<int> types)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient || types.Count == 0)
				return;

			var chunk = new List<int>(MaxTypesPerPacket);

			foreach (int type in types)
			{
				chunk.Add(type);

				if (chunk.Count == MaxTypesPerPacket)
				{
					SendNotificationChunk(origin, chunk);
					chunk.Clear();
				}
			}

			if (chunk.Count > 0)
				SendNotificationChunk(origin, chunk);
		}

		private static void SendNotificationChunk(int origin, List<int> types)
		{
			var payload = new MemoryStream();
			using (var writer = new BinaryWriter(payload))
			{
				writer.Write((byte)origin);
				writer.Write((ushort)types.Count);

				foreach (int type in types)
					writer.Write(type);
			}

			byte[] bytes = payload.ToArray();
			ModPacket packet = NewPacket(YarnMessageType.AutoResearchNotification, Main.myPlayer, bytes.Length);
			packet.Write(bytes);
			packet.Send();
		}

		private static void ReceiveAutoResearchNotification(BinaryReader reader, int sender)
		{
			int origin = reader.ReadByte();
			int count = reader.ReadUInt16();

			var types = new List<int>(count);
			for (int i = 0; i < count; i++)
				types.Add(reader.ReadInt32());

			ResearchCascadeSystem.AnnounceTeammateResearch(sender, origin, types);
		}

		public static void SendPartialResearch(int type, int count)
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

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

		private static void ReceivePartialResearch(BinaryReader reader, int sender)
		{
			int type = reader.ReadInt32();
			int count = reader.ReadInt32();

			MultiplayerSyncSystem.ReceivePartialResearch(type, count);
		}

	}
}
