using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Time;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameState;
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

        /// <summary>True while the mod itself is destroying a party because the host told it to.</summary>
        public static bool ApplyingRemoteDestroy { get; private set; }

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
        /// <param name="unstoppable">Use the "unstoppable" modes the game uses for waiting, so nothing interrupts a catch-up.</param>
        public static void SetLocalTime(TimeSpeed speed, float speedUpMultiplier, bool unstoppable)
        {
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
            return Campaign.Current.CampaignObjectManager.Find<MobileParty>(stringId);
        }

        public static Hero FindHero(string stringId)
        {
            if (string.IsNullOrEmpty(stringId) || Campaign.Current == null)
                return null;
            return Campaign.Current.CampaignObjectManager.Find<Hero>(stringId);
        }

        public static void TakeControlOf(Hero hero)
        {
            if (Hero.MainHero != hero)
                ChangePlayerCharacterAction.Apply(hero);
        }

        /// <summary>
        /// Stops a party from moving or being engaged by AI. Used for other players' parties (they are moved by
        /// the network) and for parties stuck in a battle another player is fighting.
        /// </summary>
        public static void Freeze(MobileParty party)
        {
            if (party == null || party == MobileParty.MainParty)
                return;
            party.Ai.DisableAi();
            party.SetMoveModeHold();
            party.IgnoreByOtherPartiesTill(CampaignTime.YearsFromNow(100));
        }

        /// <summary>Dedicated host: keep the server's own party still and out of every encounter.</summary>
        public static void ParkMainParty()
        {
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
            main.Ai.EnableAi();
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
            if (party == null || !party.IsActive)
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
                var countChange = goal.Count - element.Number;
                var woundedChange = goal.Wounded - element.WoundedNumber;
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
                roster.AddToCounts(character, troop.Count, false, troop.Wounded, 0, true, -1);
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
        /// Creates a new lord for a player: their own independent clan, a party at a town of their culture, starting
        /// troops and gold. Mirrors what a fresh sandbox start gives, without the character creation screens.
        /// </summary>
        public static Hero CreatePlayerHero(string name, string cultureId, bool isFemale)
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

            var clanName = new TextObject(name + "'s Clan");
            Clan clan;
            try
            {
                clan = Clan.CreateCompanionToLordClan(hero, settlement, clanName, BannerManager.Instance.GetRandomBannerIconId(new MBFastRandom()));
            }
            catch (Exception e)
            {
                Log.Error("CreateCompanionToLordClan failed, building the clan by hand", e);
                clan = Clan.CreateClan("bmp_clan_" + hero.StringId);
                clan.ChangeClanName(clanName, clanName);
                clan.Culture = culture;
                clan.Banner = Banner.CreateRandomClanBanner(MBRandom.RandomInt(int.MaxValue));
                clan.SetInitialHomeSettlement(settlement);
                hero.Clan = clan;
                clan.SetLeader(hero);
                clan.IsNoble = true;
            }

            var party = hero.PartyBelongedTo;
            if (party == null || party.LeaderHero != hero)
                party = LordPartyComponent.CreateLordParty("bmp_party_" + hero.StringId, hero, settlement.GatePosition, 3f, settlement, hero);

            if (culture.BasicTroop != null)
                party.MemberRoster.AddToCounts(culture.BasicTroop, 20, false, 0, 0, true, -1);
            hero.Gold = 5000;
            Log.Info($"Created player hero {hero.StringId} ({name}, {culture.StringId}) in clan {clan?.StringId} at {settlement.StringId}");
            return hero;
        }

        /// <summary>Name of the save the server writes for joining players. It shows up in the server's own save list.</summary>
        public const string ServerSaveName = "BannerlordMP_Server";

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
                .Where(s => !s.IsCorrupted && s.Name != ServerSaveName && !s.Name.StartsWith("BannerlordMP_Join"))
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

        public static bool AtMainMenu => GameStateManager.Current?.ActiveState is InitialState;

        public static bool OnCampaignMap => Campaign.Current != null && GameStateManager.Current?.ActiveState is MapState;

        public static void DestroyFromHost(MobileParty party)
        {
            if (party == null || !party.IsActive || party == MobileParty.MainParty)
                return;
            ApplyingRemoteDestroy = true;
            try
            {
                DestroyPartyAction.Apply(null, party);
            }
            catch (Exception e)
            {
                Log.Error($"Could not destroy party {party.StringId}", e);
            }
            finally
            {
                ApplyingRemoteDestroy = false;
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
