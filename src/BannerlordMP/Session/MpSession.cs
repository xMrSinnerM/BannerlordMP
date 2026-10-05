using System;
using System.Collections.Generic;
using System.Diagnostics;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Time;
using BannerlordMP.Game;
using BannerlordMP.Net;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.InputSystem;

namespace BannerlordMP.Session
{
    /// <summary>A running multiplayer session, either hosting (<see cref="HostSession"/>) or joined (<see cref="ClientSession"/>).</summary>
    internal abstract class MpSession : IDisposable
    {
        public const int HostPlayerId = 0;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _lastRequestFrame = -1;
        private int _frame;
        private bool _stopRequested;

        protected MpSession(MpConfig config)
        {
            Config = config;
        }

        public static MpSession Current { get; private set; }

        public static bool Active => Current != null;

        public abstract bool IsHost { get; }

        protected MpConfig Config { get; }
        /// <summary>Set by the subclass constructor before anything else uses it.</summary>
        protected ITransport Net { get; set; }
        protected double RealSeconds => _clock.Elapsed.TotalSeconds;
        protected PartyLookup Parties { get; } = new PartyLookup();

        /// <summary>Everyone in the session, host included, keyed by player id.</summary>
        protected Dictionary<int, PlayerInfo> Players { get; } = new Dictionary<int, PlayerInfo>();

        public static void Start(MpSession session)
        {
            Stop();
            Current = session;
        }

        public static void Stop()
        {
            var session = Current;
            Current = null;
            if (session == null)
                return;
            try
            {
                session.Dispose();
            }
            catch (Exception e)
            {
                Log.Error("Error while closing session", e);
            }
        }

        public void Tick(float dt)
        {
            _frame++;
            Net?.Poll();
            if (_stopRequested)
            {
                // Deferred so the transport is never torn down from inside its own event callbacks.
                if (Current == this)
                    Stop();
                return;
            }
            if (Current != this || !GameBridge.CampaignRunning)
                return;
            PollPauseKey();
            OnTick(dt);
        }

        /// <summary>Called by the time control patch when the local player presses pause / play / fast-forward.</summary>
        public void RequestSpeed(TimeSpeed speed)
        {
            _lastRequestFrame = _frame;
            OnSpeedRequested(speed);
        }

        public bool IsPlayerParty(MobileParty party)
        {
            if (party == null)
                return false;
            foreach (var player in Players.Values)
            {
                if (player.PartyId == party.StringId)
                    return true;
            }
            return false;
        }

        /// <summary>Decides whether the local game may start an encounter between two parties.</summary>
        public abstract bool AllowEncounter(MobileParty attacker, MobileParty defender);

        public abstract void OnLocalBattleEnded(MapEvent mapEvent);

        public abstract void OnLocalPartyDestroyed(MobileParty party);

        /// <summary>A political or ownership change happened in the local campaign.</summary>
        public virtual void OnLocalWorldEvent(Core.Protocol.WorldEventKind kind, string a, string b)
        {
        }

        public virtual void OnLocalPartyCreated(MobileParty party)
        {
        }

        public abstract void SendChat(string text);

        public abstract IEnumerable<string> Describe();

        protected abstract void OnTick(float dt);

        protected abstract void OnSpeedRequested(TimeSpeed speed);

        /// <summary>Ends the session after the current network poll.</summary>
        protected void RequestStop() => _stopRequested = true;

        protected string NameOf(int playerId) => Players.TryGetValue(playerId, out var p) ? p.Name : "player " + playerId;

        public virtual void Dispose()
        {
            Net?.Dispose();
            GameBridge.RestoreDefaultSpeedUp();
        }

        private void PollPauseKey()
        {
            // The pause key may bypass Campaign.SetTimeSpeed, so read it directly. If the game did route it
            // through SetTimeSpeed this frame, the request was already made.
            if (_lastRequestFrame == _frame || !Input.IsKeyPressed(InputKey.Space) || !GameBridge.IsOnMapWithoutMenu())
                return;
            RequestSpeed(CurrentSharedSpeed() == TimeSpeed.Paused ? TimeSpeed.Play : TimeSpeed.Paused);
        }

        protected abstract TimeSpeed CurrentSharedSpeed();
    }
}
