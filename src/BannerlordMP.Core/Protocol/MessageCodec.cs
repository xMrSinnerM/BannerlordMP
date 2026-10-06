using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BannerlordMP.Core.Protocol
{
    public static class MessageCodec
    {
        /// <summary>Bump whenever a message layout changes; host and clients must match exactly.</summary>
        public const ushort ProtocolVersion = 4;

        private static readonly Dictionary<MessageType, Func<INetMessage>> Factories = new Dictionary<MessageType, Func<INetMessage>>
        {
            { MessageType.Hello, () => new HelloMessage() },
            { MessageType.Welcome, () => new WelcomeMessage() },
            { MessageType.Reject, () => new RejectMessage() },
            { MessageType.PlayerList, () => new PlayerListMessage() },
            { MessageType.TimeRequest, () => new TimeRequestMessage() },
            { MessageType.ActivityChanged, () => new ActivityChangedMessage() },
            { MessageType.TimeState, () => new TimeStateMessage() },
            { MessageType.WorldSnapshot, () => new WorldSnapshotMessage() },
            { MessageType.PartyState, () => new PartyStateMessage() },
            { MessageType.BattleStarted, () => new BattleStartedMessage() },
            { MessageType.BattleResult, () => new BattleResultMessage() },
            { MessageType.PartyDestroyed, () => new PartyDestroyedMessage() },
            { MessageType.Chat, () => new ChatMessage() },
            { MessageType.AuthChallenge, () => new AuthChallengeMessage() },
            { MessageType.SlotList, () => new SlotListMessage() },
            { MessageType.ClaimSlot, () => new ClaimSlotMessage() },
            { MessageType.CreateHero, () => new CreateHeroMessage() },
            { MessageType.JoinAccepted, () => new JoinAcceptedMessage() },
            { MessageType.SaveChunk, () => new SaveChunkMessage() },
            { MessageType.ServerInfo, () => new ServerInfoMessage() },
            { MessageType.PartySpawned, () => new PartySpawnedMessage() },
            { MessageType.PartyRoster, () => new PartyRosterMessage() },
            { MessageType.WorldEvent, () => new WorldEventMessage() },
            { MessageType.EncounterRequest, () => new EncounterRequestMessage() },
            { MessageType.LedgerDelta, () => new LedgerDeltaMessage() },
            { MessageType.LedgerState, () => new LedgerStateMessage() },
            { MessageType.PartyInfoRequest, () => new PartyInfoRequestMessage() },
            { MessageType.DecisionVoteRequest, () => new DecisionVoteRequestMessage() },
            { MessageType.DecisionVote, () => new DecisionVoteMessage() },
        };

        public static byte[] Encode(INetMessage message)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write((byte)message.Type);
                message.Write(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static INetMessage Decode(byte[] data, int offset, int count)
        {
            using (var stream = new MemoryStream(data, offset, count, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                var type = (MessageType)reader.ReadByte();
                if (!Factories.TryGetValue(type, out var factory))
                    throw new InvalidDataException($"Unknown message type {(byte)type}.");
                var message = factory();
                message.Read(reader);
                return message;
            }
        }

        public static INetMessage Decode(byte[] data) => Decode(data, 0, data.Length);
    }
}
