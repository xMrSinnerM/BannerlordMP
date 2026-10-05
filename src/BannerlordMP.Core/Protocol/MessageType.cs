namespace BannerlordMP.Core.Protocol
{
    public enum MessageType : byte
    {
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        PlayerList = 4,
        TimeRequest = 5,
        ActivityChanged = 6,
        TimeState = 7,
        WorldSnapshot = 8,
        PartyState = 9,
        BattleStarted = 10,
        BattleResult = 11,
        PartyDestroyed = 12,
        Chat = 13,
        AuthChallenge = 14,
        SlotList = 15,
        ClaimSlot = 16,
        CreateHero = 17,
        JoinAccepted = 18,
        SaveChunk = 19,
        ServerInfo = 20,
    }
}
