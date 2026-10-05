using System;
using BannerlordMP.Game;
using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordMP
{
    public sealed class SubModule : MBSubModuleBase
    {
        private Harmony _harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            try
            {
                _harmony = new Harmony("bannerlordmp.campaign");
                _harmony.PatchAll(typeof(SubModule).Assembly);
                Log.Info("BannerlordMP loaded.");
            }
            catch (Exception e)
            {
                Log.Error("Failed to apply patches", e);
            }
        }

        protected override void OnSubModuleUnloaded()
        {
            MpSession.Stop();
            _harmony?.UnpatchAll(_harmony.Id);
            base.OnSubModuleUnloaded();
        }

        protected override void OnGameStart(TaleWorlds.Core.Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (game.GameType is Campaign && gameStarterObject is CampaignGameStarter starter)
                starter.AddBehavior(new MpCampaignBehavior());
        }

        public override void OnGameEnd(TaleWorlds.Core.Game game)
        {
            MpSession.Stop();
            base.OnGameEnd(game);
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            var session = MpSession.Current;
            if (session == null)
                return;
            try
            {
                session.Tick(dt);
            }
            catch (Exception e)
            {
                // A bug in the mod should end the session, not the game.
                Log.Error("Session tick failed; leaving session", e);
                Log.Notify("Multiplayer error, session closed. See BannerlordMP.log.");
                MpSession.Stop();
            }
        }
    }
}
