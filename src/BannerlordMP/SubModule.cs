using System;
using System.Linq;
using BannerlordMP.Game;
using BannerlordMP.Net;
using BannerlordMP.Session;
using BannerlordMP.Steam;
using BannerlordMP.Ui;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace BannerlordMP
{
    public sealed class SubModule : MBSubModuleBase
    {
        private Harmony _harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            AppDomain.CurrentDomain.UnhandledException += (sender, args) => Log.Error("Unhandled exception (game may crash)", args.ExceptionObject as Exception);
            try
            {
                _harmony = new Harmony("bannerlordmp.campaign");
                _harmony.PatchAll(typeof(SubModule).Assembly);
            }
            catch (Exception e)
            {
                Log.Error("Failed to apply patches", e);
            }
            Log.Info($"BannerlordMP {typeof(SubModule).Assembly.GetName().Version} starting; patched methods: {string.Join(", ", _harmony?.GetPatchedMethods().Select(m => m.DeclaringType?.Name + "." + m.Name) ?? new string[0])}");

            Module.CurrentModule.AddInitialStateOption(new InitialStateOption("BannerlordMP_Host", new TextObject("Host Co-op Campaign"), 3,
                HostMenu.Open, () => (false, null), null, null));
            Module.CurrentModule.AddInitialStateOption(new InitialStateOption("BannerlordMP_Join", new TextObject("Join Co-op Campaign"), 4,
                JoinMenu.Open, () => (false, null), null, null));
            Log.Info("BannerlordMP loaded.");
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            try
            {
                SteamService.Initialize();
                JoinMenu.Initialize();
            }
            catch (Exception e)
            {
                Log.Error("Steam integration failed to start", e);
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
            try
            {
                JoinMenu.Tick();
                StartPendingSession();
            }
            catch (Exception e)
            {
                Log.Error("Menu tick failed", e);
            }

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

        /// <summary>Hosting or joining from the main menu loads a world first; the session starts once it is on the map.</summary>
        private static void StartPendingSession()
        {
            if (!GameBridge.OnCampaignMap)
                return;

            if (HostMenu.PendingHost != null)
            {
                var config = HostMenu.PendingHost;
                HostMenu.PendingHost = null;
                try
                {
                    MpSession.Start(new HostSession(config));
                    if (SteamService.Available && config.SteamVisibility != LobbyVisibility.Off)
                        Dialogs.Confirm("Server started", $"'{config.ServerName}' is up. Invite Steam friends now? (Later: mp.invite in the console.)",
                            "Invite", "Later", () => SteamService.OpenInviteDialog());
                }
                catch (Exception e)
                {
                    MpSession.Stop();
                    Log.Error("Could not start hosting", e);
                    Dialogs.Message("Could not host", e.Message);
                }
            }
            else if (JoinMenu.PendingResume != null)
            {
                var resume = JoinMenu.PendingResume;
                JoinMenu.PendingResume = null;
                Log.Info($"Join: world loaded, reconnecting to {resume.Target} as {resume.HeroId}");
                try
                {
                    MpSession.Start(new ClientSession(MpConfig.Load(), resume.Target, resume.Token, resume.HeroId));
                }
                catch (Exception e)
                {
                    MpSession.Stop();
                    Log.Error("Could not rejoin after loading", e);
                    Dialogs.Message("Could not join", e.Message);
                }
            }
        }
    }
}
