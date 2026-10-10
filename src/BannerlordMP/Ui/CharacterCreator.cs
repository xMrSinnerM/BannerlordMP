using System;
using System.Diagnostics;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Game;
using BannerlordMP.Net;
using TaleWorlds.MountAndBlade;

namespace BannerlordMP.Ui
{
    /// <summary>
    /// A new player's hero, made with the game's own single-player character creator: start a throwaway
    /// sandbox campaign on this PC, let the player go through every creation screen, copy the finished hero
    /// as soon as the map opens, end that campaign without saving, and hand the hero back to the join menu.
    /// The server then builds the same hero in its world.
    /// </summary>
    internal static class CharacterCreator
    {
        /// <summary>Seconds on the map before copying the hero, so whatever the game sets up on its first frames is in.</summary>
        private const double SettleSeconds = 1.0;
        /// <summary>The map normally opens right after the creation-over event; this covers a game that skips the event.</summary>
        private const double NoEventSeconds = 5.0;
        /// <summary>Starting a new game leaves the main menu within a moment; if it never does, give up.</summary>
        private const double StartTimeoutSeconds = 60.0;
        /// <summary>How long the main menu must stay up before it counts (loading can flash through it).</summary>
        private const double MenuSettleSeconds = 1.0;

        private enum Stage
        {
            None,
            Starting,
            Creating,
            Ending,
        }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static Stage _stage;
        private static ConnectTarget _target;
        private static string _serverPassword;
        private static bool _creationOver;
        private static double _stageStarted;
        private static double _onMapSince = -1;
        private static double _atMenuSince = -1;
        private static double _menuShownSince = -1;
        private static HeroSheet _sheet;

        /// <summary>True from the moment creation starts until the throwaway campaign is closed again.</summary>
        public static bool Active => _stage != Stage.None;

        /// <summary>Starts the creator for a hero on <paramref name="target"/>. False if the game's new-game button was not found.</summary>
        public static bool Begin(ConnectTarget target, string serverPassword)
        {
            // Nothing may treat the throwaway campaign as a world to host or join.
            HostMenu.PendingHost = null;
            JoinMenu.PendingResume = null;
            _target = target;
            _serverPassword = serverPassword;
            _sheet = null;
            _creationOver = false;
            _onMapSince = -1;
            SetStage(Stage.Starting);
            Log.Info($"Character creator: starting a local sandbox for a hero on {target}");
            if (GameBridge.StartNewSandbox())
                return true;
            Reset();
            return false;
        }

        /// <summary>Campaign event: the last creation screen was confirmed.</summary>
        public static void OnCreationOver()
        {
            if (_stage == Stage.Creating || _stage == Stage.Starting)
                _creationOver = true;
        }

        public static void Tick()
        {
            if (_stage == Stage.None)
                return;
            var now = Clock.Elapsed.TotalSeconds;
            var inGame = TaleWorlds.Core.Game.Current != null;
            if (GameBridge.AtMainMenu && !inGame)
            {
                if (_atMenuSince < 0)
                    _atMenuSince = now;
            }
            else
            {
                _atMenuSince = -1;
            }
            var backAtMenu = _atMenuSince >= 0 && now - _atMenuSince >= MenuSettleSeconds;
            // After ending the game on purpose, the menu being up is enough (in case the game object lingers).
            _menuShownSince = GameBridge.AtMainMenu ? (_menuShownSince < 0 ? now : _menuShownSince) : -1;
            var menuShown = _menuShownSince >= 0 && now - _menuShownSince >= MenuSettleSeconds;

            switch (_stage)
            {
                case Stage.Starting:
                    if (inGame || !GameBridge.AtMainMenu)
                        SetStage(Stage.Creating);
                    else if (now - _stageStarted > StartTimeoutSeconds)
                        Cancelled("The character creator did not start.");
                    break;

                case Stage.Creating:
                    if (backAtMenu)
                    {
                        Cancelled(null);
                        break;
                    }
                    if (!GameBridge.OnCampaignMap)
                    {
                        _onMapSince = -1;
                        break;
                    }
                    if (_onMapSince < 0)
                        _onMapSince = now;
                    var waited = now - _onMapSince;
                    if (waited < SettleSeconds || (!_creationOver && waited < NoEventSeconds))
                        break;
                    try
                    {
                        _sheet = HeroSheetBridge.CaptureMainHero();
                        Log.Info($"Character creator: captured {_sheet.Name} ({_sheet.CultureId}, age {_sheet.Age:0}, {_sheet.Skills.Count} skills, {_sheet.Troops.Count} troop types)");
                    }
                    catch (Exception e)
                    {
                        Log.Error("Could not copy the created character", e);
                        _sheet = null;
                    }
                    Log.Notify(_sheet != null ? $"{_sheet.Name} is ready. Returning to the server..." : "Could not copy your character.");
                    SetStage(Stage.Ending);
                    try
                    {
                        MBGameManager.EndGame();
                    }
                    catch (Exception e)
                    {
                        // Still handled once the player exits to the main menu by hand.
                        Log.Error("Could not close the character creator's campaign", e);
                        Log.Notify("Exit to the main menu (do not save) to continue joining.");
                    }
                    break;

                case Stage.Ending:
                    if (!menuShown)
                        break;
                    var sheet = _sheet;
                    var target = _target;
                    var password = _serverPassword;
                    Reset();
                    if (sheet == null)
                        Dialogs.Message("Character creator", "Your character could not be copied (see BannerlordMP.log). Join again and use Quick create instead.");
                    else
                        JoinMenu.OnHeroMade(sheet, target, password);
                    break;
            }
        }

        private static void Cancelled(string reason)
        {
            Log.Info("Character creator: cancelled" + (reason != null ? " (" + reason + ")" : ""));
            Reset();
            if (reason != null)
                Dialogs.Message("Character creator", reason);
        }

        private static void SetStage(Stage stage)
        {
            _stage = stage;
            _stageStarted = Clock.Elapsed.TotalSeconds;
        }

        private static void Reset()
        {
            _stage = Stage.None;
            _target = null;
            _serverPassword = null;
            _sheet = null;
            _creationOver = false;
            _onMapSince = -1;
            _atMenuSince = -1;
            _menuShownSince = -1;
        }
    }
}
