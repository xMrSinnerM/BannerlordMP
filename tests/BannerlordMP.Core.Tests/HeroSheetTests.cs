using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Slots;
using Xunit;

namespace BannerlordMP.Core.Tests
{
    public class HeroSheetTests
    {
        private static HeroSheet Sample() => new HeroSheet
        {
            Name = "Ana",
            ClanName = "Ana's Clan",
            CultureId = "vlandia",
            IsFemale = true,
            Age = 25,
            BodyProperties = "<BodyProperties version=\"4\" age=\"25\" />",
            BannerCode = "11.4.4.1528.1528.764.764.1.0.0",
            Gold = 1000,
            UnspentAttributePoints = 1,
            UnspentFocusPoints = 2,
            Attributes = new Dictionary<string, int> { { "vigor", 4 }, { "cunning", 3 } },
            Skills = new Dictionary<string, int> { { "OneHanded", 60 }, { "Riding", 30 } },
            Focus = new Dictionary<string, int> { { "OneHanded", 2 } },
            Traits = new Dictionary<string, int> { { "Mercy", 1 } },
            BattleEquipment = new List<string> { "vlandia_sword_1_t2|", "", "" },
            CivilianEquipment = new List<string> { "" },
            Troops = new List<TroopCount> { new TroopCount("vlandian_recruit", 5, 0) },
            Level = 2,
            Perks = new List<string> { "OneHandedWrappedHandles" },
        };

        [Fact]
        public void CreateHeroCarriesTheSheet()
        {
            var message = new CreateHeroMessage { HeroName = "Ana", CultureId = "vlandia", IsFemale = true, Salt = new byte[16], Key = new byte[32], Sheet = Sample() };
            var decoded = (CreateHeroMessage)MessageCodec.Decode(MessageCodec.Encode(message));
            var sheet = decoded.Sheet;
            Assert.NotNull(sheet);
            Assert.Equal("Ana's Clan", sheet.ClanName);
            Assert.Equal(25, sheet.Age);
            Assert.Equal(60, sheet.Skills["OneHanded"]);
            Assert.Equal("vlandia_sword_1_t2|", sheet.BattleEquipment[0]);
            Assert.Equal(5, sheet.Troops[0].Count);
            Assert.Equal("11.4.4.1528.1528.764.764.1.0.0", sheet.BannerCode);
            Assert.Equal(2, sheet.Level);
            Assert.Equal("OneHandedWrappedHandles", sheet.Perks.Single());
        }

        [Fact]
        public void QuickCreateHasNoSheet()
        {
            var decoded = (CreateHeroMessage)MessageCodec.Decode(MessageCodec.Encode(new CreateHeroMessage { HeroName = "Bo" }));
            Assert.Null(decoded.Sheet);
        }

        [Fact]
        public void AValidSheetIsLeftAlone()
        {
            var sheet = Sample();
            Assert.Empty(HeroSheetRules.Clamp(sheet));
            Assert.Equal(60, sheet.Skills["OneHanded"]);
        }

        [Fact]
        public void ImpossibleValuesAreClamped()
        {
            var sheet = Sample();
            sheet.Attributes["vigor"] = 50;
            sheet.Skills["OneHanded"] = 330;
            sheet.Focus["OneHanded"] = 9;
            sheet.Gold = 1_000_000;
            sheet.Age = 5;
            sheet.Level = 40;
            sheet.Troops = new List<TroopCount> { new TroopCount("a", 50, 60), new TroopCount("b", 50, 0) };

            var changes = HeroSheetRules.Clamp(sheet);
            Assert.NotEmpty(changes);
            Assert.Equal(HeroSheetRules.MaxAttribute, sheet.Attributes["vigor"]);
            Assert.Equal(HeroSheetRules.MaxStartingSkill, sheet.Skills["OneHanded"]);
            Assert.Equal(HeroSheetRules.MaxFocusPerSkill, sheet.Focus["OneHanded"]);
            Assert.Equal(HeroSheetRules.MaxStartingGold, sheet.Gold);
            Assert.Equal(HeroSheetRules.MinAge, sheet.Age);
            Assert.Equal(HeroSheetRules.MaxStartingLevel, sheet.Level);
            Assert.Equal(HeroSheetRules.MaxStartingTroops, sheet.Troops.Sum(t => t.Count));
            Assert.All(sheet.Troops, t => Assert.True(t.Wounded <= t.Count));
        }

        [Fact]
        public void NamesAreCleaned()
        {
            var sheet = Sample();
            sheet.Name = "  Ana|of\nPravend  ";
            HeroSheetRules.Clamp(sheet);
            Assert.Equal("Anaof Pravend", sheet.Name);
        }

        [Fact]
        public void SlotKeepsTheSheetForRebuildingTheHero()
        {
            var salt = Security.PasswordProof.NewSalt();
            var registry = new SlotRegistry(2);
            registry.Add("h1", "Ana", "Vlandia", salt, Security.PasswordProof.DeriveKey("pw", salt), "vlandia", true, Sample().ToBytes());
            registry.Add("h2", "Bo", "Sturgia", salt, Security.PasswordProof.DeriveKey("pw", salt), "sturgia");

            var loaded = SlotRegistry.Deserialize(registry.Serialize(), 2).Slots;
            var sheet = HeroSheet.FromBytes(loaded[0].Sheet);
            Assert.Equal("Ana's Clan", sheet.ClanName);
            Assert.Equal(60, sheet.Skills["OneHanded"]);
            Assert.Empty(loaded[1].Sheet);
        }
    }
}
