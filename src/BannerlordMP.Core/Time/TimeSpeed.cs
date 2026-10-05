namespace BannerlordMP.Core.Time
{
    /// <summary>Campaign clock speed shared by every player.</summary>
    public enum TimeSpeed : byte
    {
        Paused = 0,
        Play = 1,
        FastForward = 2,
    }

    /// <summary>What a player is currently doing, as far as the shared clock cares.</summary>
    public enum PlayerActivity : byte
    {
        /// <summary>On the campaign map, free to move.</summary>
        Map = 0,
        /// <summary>In a game menu or management screen (town menu, inventory, party screen...). Still follows the shared clock.</summary>
        Menu = 1,
        /// <summary>In a map conversation.</summary>
        Conversation = 2,
        /// <summary>In a 3D mission (field battle, siege, town/tavern scene...). Runs separately from the world.</summary>
        Mission = 3,
    }

    public enum TimeArbitrationMode : byte
    {
        /// <summary>Any attached player can pause, play or fast-forward for everyone; the latest request wins.</summary>
        LastRequestWins = 0,
        /// <summary>The world runs at the slowest speed requested by attached players; anyone pausing pauses everyone.</summary>
        Consensus = 1,
    }

    public static class PlayerActivityExtensions
    {
        /// <summary>
        /// A detached player is not following the world clock: their game is inside a mission (or conversation)
        /// while the world keeps running. They are fast-forwarded when they come back.
        /// </summary>
        public static bool IsDetached(this PlayerActivity activity, bool detachDuringConversations)
        {
            return activity == PlayerActivity.Mission
                || (detachDuringConversations && activity == PlayerActivity.Conversation);
        }
    }
}
