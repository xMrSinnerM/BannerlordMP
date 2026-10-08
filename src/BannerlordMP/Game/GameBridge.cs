using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Time;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;
using SandBox;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BannerlordMP.Game
{
    /// <summary>
    /// Every direct use of the TaleWorlds campaign API goes through here, so that game-version breakage is
    /// concentrated in one file.
    /// </summary>
    internal static class GameBridge
    {
        private static float? _defaultSpeedUpMultiplier;

        /// <summary>True while the mod itself is changing campaign time; patches let those calls through.</summary>
        public static bool ApplyingTime { get; private set; }


        public static bool CampaignRunning => Campaign.Current != null;

        public static double NowHours => CampaignTime.Now.ToHours;

        public static string CampaignId => Campaign.Current?.UniqueGameId ?? string.Empty;

        public static MobileParty MainParty => MobileParty.MainParty;

        public static TimeSpeed GetLocalSpeed()
        {
            switch (Campaign.Current.TimeControlMode)
            {
                case CampaignTimeControlMode.Stop:
                case CampaignTimeControlMode.FastForwardStop:
                    return TimeSpeed.Paused;
                case CampaignTimeControlMode.StoppableFastForward:
                case CampaignTimeControlMode.UnstoppableFastForward:
                case CampaignTimeControlMode.UnstoppableFastForwardForPartyWaitTime:
                    return TimeSpeed.FastForward;
                default:
                    return TimeSpeed.Play;
            }
        }

        /// <summary>The time speed number the game's UI uses (0 pause, 1 play, 2 fast forward).</summary>
        public static TimeSpeed FromGameSpeed(int speed)
        {
            return speed <= 0 ? TimeSpeed.Paused : speed == 1 ? TimeSpeed.Play : TimeSpeed.FastForward;
        }

        public static TimeSpeed FromGameMode(CampaignTimeControlMode mode)
        {
            switch (mode)
            {
                case CampaignTimeControlMode.Stop:
                case CampaignTimeControlMode.FastForwardStop:
                    return TimeSpeed.Paused;
                case CampaignTimeControlMode.StoppablePlay:
                case CampaignTimeControlMode.UnstoppablePlay:
                    return TimeSpeed.Play;
                default:
                    return TimeSpeed.FastForward;
            }
        }

        /// <param name="speedUpMultiplier">Fast-forward multiplier, or 0 for the game's default.</param>
        /// <remarks>
        /// Always uses the game's "unstoppable" modes. The normal (stoppable) ones stop the clock whenever the
        /// local player's party is idle, which is single-player behaviour: in a shared world one idle party
        /// (or a dedicated server's parked one) must not pause everyone.
        /// </remarks>
        public static void SetLocalTime(TimeSpeed speed, float speedUpMultiplier)
        {
            const bool unstoppable = true;
            var campaign = Campaign.Current;
            if (campaign == null)
                return;

            if (_defaultSpeedUpMultiplier == null)
                _defaultSpeedUpMultiplier = campaign.SpeedUpMultiplier;

            CampaignTimeControlMode mode;
            switch (speed)
            {
                case TimeSpeed.Play:
                    mode = unstoppable ? CampaignTimeControlMode.UnstoppablePlay : CampaignTimeControlMode.StoppablePlay;
                    break;
                case TimeSpeed.FastForward:
                    mode = unstoppable ? CampaignTimeControlMode.UnstoppableFastForward : CampaignTimeControlMode.StoppableFastForward;
                    break;
                default:
                    mode = CampaignTimeControlMode.Stop;
                    break;
            }

            var multiplier = speedUpMultiplier > 0 ? speedUpMultiplier : _defaultSpeedUpMultiplier.Value;
            if (campaign.TimeControlMode == mode && Math.Abs(campaign.SpeedUpMultiplier - multiplier) < 0.001f)
                return;

            ApplyingTime = true;
            try
            {
                campaign.SpeedUpMultiplier = multiplier;
                campaign.TimeControlMode = mode;
            }
            finally
            {
                ApplyingTime = false;
            }
        }

        public static void RestoreDefaultSpeedUp()
        {
            if (Campaign.Current != null && _defaultSpeedUpMultiplier != null)
                Campaign.Current.SpeedUpMultiplier = _defaultSpeedUpMultiplier.Value;
            _defaultSpeedUpMultiplier = null;
        }

        public static PlayerActivity DetectActivity()
        {
            if (Mission.Current != null)
                return PlayerActivity.Mission;

            var state = GameStateManager.Current?.ActiveState;
            if (state is MapState mapState)
            {
                if (mapState.MapConversationActive)
                    return PlayerActivity.Conversation;
                return mapState.AtMenu ? PlayerActivity.Menu : PlayerActivity.Map;
            }

            // Inventory, party screen, encyclopedia, etc. are game states pushed on top of the map.
            return PlayerActivity.Menu;
        }

        public static bool IsOnMapWithoutMenu()
        {
            return Mission.Current == null
                && GameStateManager.Current?.ActiveState is MapState mapState
                && !mapState.AtMenu
                && !mapState.MapConversationActive;
        }

        public static MobileParty FindParty(string stringId)
        {
            if (string.IsNullOrEmpty(stringId) || Campaign.Current == null)
                return null;
            return Campaign.Current.CampaignObjectManager.Find<MobileParty>(stringId)
                ?? MobileParty.All.FirstOrDefault(p => p.StringId == stringId);
        }

        public static Hero FindHero(string stringId)
        {
            if (string.IsNullOrEmpty(stringId) || Campaign.Current == null)
                return null;
            return Campaign.Current.CampaignObjectManager.Find<Hero>(stringId);
        }

        /// <summary>
        /// Closes whatever the loaded world's main party was doing: an open town or encounter menu, a player
        /// encounter, being inside a settlement. A downloaded world is a save of the server, so this is the
        /// server's hero; its menu would otherwise stay open after we switch heroes, and "Leave town" then acts on
        /// a party that was never in that town (crash).
        /// </summary>
        public static void CloseMainPartyActivity()
        {
            var main = MobileParty.MainParty;
            try
            {
                if (PlayerEncounter.Current != null)
                {
                    Log.Info("Finishing the loaded world's player encounter");
                    PlayerEncounter.Finish(true);
                }
                if (main?.CurrentSettlement != null)
                {
                    Log.Info($"Main party {main.StringId} leaves {main.CurrentSettlement.StringId}");
                    LeaveSettlementAction.ApplyForParty(main);
                }
                for (var guard = 0; Campaign.Current?.CurrentMenuContext != null && guard < 10; guard++)
                {
                    Log.Info("Closing game menu " + Campaign.Current.CurrentMenuContext.GameMenu?.StringId);
                    GameMenu.ExitToLast();
                }
            }
            catch (Exception e)
            {
                Log.Error("Could not close the loaded world's menus", e);
            }
        }

        public static void TakeControlOf(Hero hero)
        {
            CloseMainPartyActivity();
            if (Hero.MainHero != hero)
                ChangePlayerCharacterAction.Apply(hero);

            // ChangePlayerCharacterAction is made for heirs of the same clan, so it leaves "the player's clan"
            // (Campaign.PlayerDefaultFaction, behind Clan.PlayerClan) on the old clan. Our hero leads its own clan;
            // the clan and kingdom screens crash when the main hero is not in the player clan.
            var clan = hero.Clan;
            if (clan != null && Clan.PlayerClan != clan)
            {
                var setter = AccessTools.PropertySetter(typeof(Campaign), "PlayerDefaultFaction");
                if (setter == null)
                {
                    Log.Error("Campaign.PlayerDefaultFaction setter not found; clan screens may crash");
                    return;
                }
                var previous = Clan.PlayerClan?.StringId;
                setter.Invoke(Campaign.Current, new object[] { clan });
                Log.Info($"Player clan changed from {previous} to {Clan.PlayerClan?.StringId}");
            }
        }

        /// <summary>
        /// Stops a party from moving or being engaged by AI. Used for other players' parties (they are moved by
        /// the network) and for parties stuck in a battle another player is fighting.
        /// </summary>
        /// <param name="ignoredByOthers">
        /// True to also make AI parties ignore it (offline players, parties mid-battle elsewhere). An online
        /// player's party stays a valid target, so AI on the host can still hunt them.
        /// </param>
        public static void Freeze(MobileParty party, bool ignoredByOthers = true)
        {
            if (party == null || party == MobileParty.MainParty)
                return;
            party.Ai.DisableAi();
            party.SetMoveModeHold();
            party.IgnoreByOtherPartiesTill(ignoredByOthers ? CampaignTime.YearsFromNow(100) : CampaignTime.Now);
        }

        /// <summary>Dedicated host: keep the server's own party still and out of every encounter.</summary>
        public static void ParkMainParty()
        {
            // Out of any town and menu first: the world sent to players is saved from here, and they would load it
            // with the server hero's menu open.
            CloseMainPartyActivity();
            var main = MobileParty.MainParty;
            if (main == null)
                return;
            main.SetMoveModeHold();
            main.IgnoreByOtherPartiesTill(CampaignTime.YearsFromNow(100));
        }

        /// <summary>
        /// Client, after taking control of its hero: the server keeps offline players' parties frozen, and the
        /// downloaded save still has ours that way. Make it a normal, attackable, controllable party again.
        /// </summary>
        public static void ReleaseOwnParty()
        {
            var main = MobileParty.MainParty;
            if (main == null)
                return;
            main.IgnoreByOtherPartiesTill(CampaignTime.Now);
            // As single player sets up the player's party: AI machinery on (movement runs through it), but it
            // never makes decisions of its own. Fully enabled AI let the party act by itself, e.g. when leaving town.
            main.Ai.EnableAi();
            main.Ai.SetDoNotMakeNewDecisions(true);
            if (main.CurrentSettlement == null && main.MapEvent == null)
                main.SetMoveModeHold();
            Log.Info($"Own party released: in settlement {main.CurrentSettlement?.StringId ?? "none"}, AI disabled {main.Ai.IsDisabled}, " +
                     $"no own decisions {main.Ai.DoNotMakeNewDecisions}, behavior {main.DefaultBehavior}");
        }

        /// <summary>Dedicated host: undo any move order the game gave the parked party (map clicks, menus).</summary>
        public static void KeepMainPartyParked()
        {
            var main = MobileParty.MainParty;
            if (main != null && main.CurrentSettlement == null && main.DefaultBehavior != AiBehavior.Hold)
                main.SetMoveModeHold();
        }

        public static void UnparkMainParty()
        {
            MobileParty.MainParty?.IgnoreByOtherPartiesTill(CampaignTime.Now);
        }

        public static void Unfreeze(MobileParty party, bool enableAi)
        {
            if (party == null || party == MobileParty.MainParty)
                return;
            party.IgnoreByOtherPartiesTill(CampaignTime.Now);
            if (enableAi)
                party.Ai.EnableAi();
        }

        public static PartyPosition GetPosition(MobileParty party)
        {
            var position = party.Position;
            return new PartyPosition(party.StringId, position.X, position.Y, position.IsOnLand);
        }

        public static void SetPosition(MobileParty party, float x, float y, bool isOnLand)
        {
            // Parties inside a settlement, attached to an army or in a battle are positioned by the game itself;
            // moving them by hand can leave it in an inconsistent state.
            if (party == null || !party.IsActive || party.CurrentSettlement != null || party.AttachedTo != null || party.MapEvent != null)
                return;
            var current = party.Position;
            if (Math.Abs(current.X - x) < 0.001f && Math.Abs(current.Y - y) < 0.001f)
                return;
            party.Position = new CampaignVec2(new Vec2(x, y), isOnLand);
        }

        public static List<TroopCount> CaptureRoster(TroopRoster roster)
        {
            var result = new List<TroopCount>();
            foreach (var element in roster.GetTroopRoster())
            {
                if (element.Character != null)
                    result.Add(new TroopCount(element.Character.StringId, element.Number, element.WoundedNumber));
            }
            return result;
        }

        /// <summary>
        /// Makes <paramref name="roster"/> match <paramref name="target"/> for regular troops. Heroes are left alone:
        /// hero death, capture and release need their own actions and are not synchronized yet.
        /// </summary>
        public static void ApplyRoster(TroopRoster roster, List<TroopCount> target)
        {
            var wanted = new Dictionary<string, TroopCount>();
            foreach (var troop in target)
                wanted[troop.CharacterId] = troop;

            // Remove or adjust what the roster has now.
            foreach (var element in roster.GetTroopRoster().ToArray())
            {
                var character = element.Character;
                if (character == null || character.IsHero)
                    continue;
                wanted.TryGetValue(character.StringId, out var goal);
                var goalCount = Math.Max(0, goal.Count);
                var goalWounded = Math.Max(0, Math.Min(goalCount, goal.Wounded));
                var countChange = goalCount - element.Number;
                var woundedChange = goalWounded - element.WoundedNumber;
                if (countChange != 0 || woundedChange != 0)
                    roster.AddToCounts(character, countChange, false, woundedChange, 0, true, -1);
                wanted.Remove(character.StringId);
            }

            // Add troop types the roster did not have (recruited prisoners, upgrades...).
            foreach (var troop in wanted.Values)
            {
                var character = MBObjectManager.Instance.GetObject<CharacterObject>(troop.CharacterId);
                if (character == null || character.IsHero || troop.Count <= 0)
                    continue;
                roster.AddToCounts(character, troop.Count, false, Math.Max(0, Math.Min(troop.Count, troop.Wounded)), 0, true, -1);
            }
        }

        public static List<CultureChoice> PlayableCultures()
        {
            return MBObjectManager.Instance.GetObjectTypeList<CultureObject>()
                .Where(c => c.IsMainCulture && c.LordTemplates != null && c.LordTemplates.Count > 0)
                .Select(c => new CultureChoice(c.StringId, c.Name.ToString()))
                .OrderBy(c => c.Name)
                .ToList();
        }

        public static string CultureName(Hero hero) => hero?.Culture?.Name?.ToString() ?? string.Empty;

        /// <summary>
        /// Creates a new hero for a player: their own independent clan and a party at a town of their culture,
        /// starting like a new single-player game. With a sheet from the character creator, the hero is exactly
        /// the one the player made; without one (quick create), a plain start (<see cref="HeroSheetBridge.ApplyBasicStart"/>).
        /// </summary>
        public static Hero CreatePlayerHero(string name, string cultureId, bool isFemale, HeroSheet sheet = null)
        {
            var culture = MBObjectManager.Instance.GetObject<CultureObject>(cultureId);
            if (culture == null || !culture.IsMainCulture)
                throw new ArgumentException("Unknown culture " + cultureId);

            var templates = culture.LordTemplates.Where(t => t.IsFemale == isFemale).ToList();
            if (templates.Count == 0)
                templates = culture.LordTemplates.ToList();
            var template = templates[MBRandom.RandomInt(templates.Count)];

            var towns = Town.AllTowns.Where(t => t.Culture == culture).ToList();
            if (towns.Count == 0)
                towns = Town.AllTowns.ToList();
            var settlement = towns[MBRandom.RandomInt(towns.Count)].Settlement;

            var hero = HeroCreator.CreateSpecialHero(template, settlement, null, null, 25);
            var heroName = new TextObject(name);
            hero.SetName(heroName, heroName);
            hero.ChangeState(Hero.CharacterStates.Active);

            // Built by hand on purpose. Clan.CreateCompanionToLordClan (used when knighting a companion) hands the
            // new clan a town and ties it to the host's clan, which gave players a fief they never took.
            var clanName = new TextObject(name + "'s Clan");
            var clan = Clan.CreateClan("bmp_clan_" + hero.StringId);
            clan.ChangeClanName(clanName, clanName);
            clan.Culture = culture;
            clan.BasicTroop = culture.BasicTroop;
            var banner = Banner.CreateRandomClanBanner(MBRandom.RandomInt(int.MaxValue));
            clan.Banner = banner;
            clan.UpdateBannerColor(banner.GetPrimaryColor(), banner.GetFirstIconColor());
            clan.SetInitialHomeSettlement(settlement);
            hero.Clan = clan;
            clan.SetLeader(hero);
            clan.IsNoble = true;
            CampaignEventDispatcher.Instance.OnClanCreated(clan, false);

            var party = hero.PartyBelongedTo;
            if (party == null || party.LeaderHero != hero)
                party = LordPartyComponent.CreateLordParty("bmp_party_" + hero.StringId, hero, settlement.GatePosition, 3f, settlement, hero);
            // Start outside the town: a party created inside a settlement it does not belong to confuses menus.
            if (party.CurrentSettlement != null)
                LeaveSettlementAction.ApplyForParty(party);

            // The hero is built from an AI lord template, and a lord party comes with a full roster, food and
            // spending money. A new single-player character has none of that: start from nothing.
            ApplyRoster(party.MemberRoster, new List<TroopCount>());
            ApplyRoster(party.PrisonRoster, new List<TroopCount>());
            party.ItemRoster.Clear();
            hero.Gold = 0;
            clan.Renown = 0f;
            clan.Influence = 0f;

            if (sheet != null)
                HeroSheetBridge.Apply(hero, sheet);
            else
                HeroSheetBridge.ApplyBasicStart(hero);
            Log.Info($"Created player hero {hero.StringId} ({name}, {culture.StringId}) in clan {clan.StringId} near {settlement.StringId}");
            return hero;
        }

        /// <summary>Name of the save the server writes for joining players. It shows up in the server's own save list.</summary>
        public const string ServerSaveName = "BannerlordMP_Server";

        /// <summary>The server's periodic autosave.</summary>
        public const string AutoSaveName = "BannerlordMP_Autosave";

        public static void SaveWorld(string saveName) => Campaign.Current.SaveHandler.SaveAs(saveName);

        public static byte[] ReadSaveFile(string saveName)
        {
            foreach (var path in SaveFileCandidates(saveName))
            {
                if (Common.PlatformFileHelper.FileExists(path))
                    return Common.PlatformFileHelper.GetFileContent(path);
            }
            throw new System.IO.FileNotFoundException("Save not found: " + saveName);
        }

        public static void WriteSaveFile(string saveName, byte[] data)
        {
            var path = SaveFileCandidates(saveName).First();
            var result = Common.PlatformFileHelper.SaveFile(path, data);
            if (result != SaveResult.Success)
                throw new System.IO.IOException($"Could not write {saveName}: {result} {Common.PlatformFileHelper.GetError()}");
        }

        /// <summary>
        /// Presses the game's own "Sandbox" new-game button, so character creation runs exactly as in single player.
        /// False if the button could not be found.
        /// </summary>
        public static bool StartNewSandbox()
        {
            var options = TaleWorlds.MountAndBlade.Module.CurrentModule.GetInitialStateOptions().ToList();
            var option = options.FirstOrDefault(o =>
                o.Id.IndexOf("sandbox", StringComparison.OrdinalIgnoreCase) >= 0 && o.Id.IndexOf("new", StringComparison.OrdinalIgnoreCase) >= 0);
            if (option == null)
            {
                Log.Info("Initial state options: " + string.Join(", ", options.Select(o => o.Id)));
                return false;
            }
            option.DoAction();
            return true;
        }

        /// <summary>Loads a save from the main menu, going through the game's usual module-compatibility checks.</summary>
        public static bool LoadSave(string saveName, Action onCancel)
        {
            var info = MBSaveLoad.GetSaveFileWithName(saveName);
            if (info == null)
                return false;
            SandBoxSaveHelper.TryLoadSave(info, result => MBGameManager.StartNewGame(new SandBoxGameManager(result)), onCancel);
            return true;
        }

        public static List<SaveGameFileInfo> ListSaves()
        {
            return (MBSaveLoad.GetSaveFiles(null) ?? new SaveGameFileInfo[0])
                // BannerlordMP_Join is a player's downloaded copy, never a world to host.
                .Where(s => !s.IsCorrupted && !s.Name.StartsWith("BannerlordMP_Join"))
                .OrderByDescending(s => s.Name == AutoSaveName)
                .ThenByDescending(s => s.Name == ServerSaveName)
                .ToList();
        }

        private static IEnumerable<PlatformFilePath> SaveFileCandidates(string saveName)
        {
            var extension = SaveManager.SaveFileExtension ?? string.Empty;
            if (extension.Length > 0 && !extension.StartsWith("."))
                extension = "." + extension;
            yield return FileDriver.GetSaveFilePath(saveName + extension);
            yield return FileDriver.GetSaveFilePath(saveName);
        }

        /// <summary>
        /// Client: one day of the player clan's finances, exactly as single player computes them (wages, fief,
        /// workshop and caravan income...). The client's daily ticks are off, and the host treats the clan as an
        /// AI clan, so this is where a joined player's money really comes from.
        /// </summary>
        public static int ApplyDailyClanFinances()
        {
            var clan = Clan.PlayerClan;
            var leader = Hero.MainHero;
            if (clan == null || leader == null || leader.Clan != clan)
                return 0;
            var change = Campaign.Current.Models.ClanFinanceModel.CalculateClanGoldChange(clan, false, true, false).RoundedResultNumber;
            leader.Gold = Math.Max(0, leader.Gold + change);
            return change;
        }

        public static bool AtMainMenu => GameStateManager.Current?.ActiveState is InitialState;

        public static bool OnCampaignMap => Campaign.Current != null && GameStateManager.Current?.ActiveState is MapState;

        /// <summary>
        /// Client: take a party out of the world without destroying it. Destroying runs the game's whole
        /// destruction chain (village/caravan owners, notables, quests...) against state the client does not
        /// simulate, which crashed games. A retired party is invisible, inactive and ignored by everyone.
        /// </summary>
        public static void Retire(MobileParty party)
        {
            if (party == null || !party.IsActive || party == MobileParty.MainParty || party.MapEvent != null)
                return;
            try
            {
                party.Ai.DisableAi();
                party.IgnoreByOtherPartiesTill(CampaignTime.YearsFromNow(100));
                // A party inside a settlement is still in its party list; the game walks that list when anyone
                // enters or leaves, so an inactive one there crashes it. Hidden puppets are harmless.
                if (party.CurrentSettlement != null)
                    return;
                party.IsVisible = false;
                party.IsActive = false;
            }
            catch (Exception e)
            {
                Log.Error($"Could not retire party {party.StringId}", e);
            }
        }

        public static void ApplyBattleDestroy(MobileParty party, MobileParty winner)
        {
            if (party == null || !party.IsActive)
                return;
            try
            {
                DestroyPartyAction.Apply(winner?.Party, party);
            }
            catch (Exception e)
            {
                Log.Error($"Could not destroy party {party.StringId} after battle", e);
            }
        }
    }
}
