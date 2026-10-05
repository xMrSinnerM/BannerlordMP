using System;
using System.IO;
using System.Linq;
using BannerlordMP.Core.Security;
using BannerlordMP.Core.Slots;
using BannerlordMP.Core.Transfer;
using Xunit;

namespace BannerlordMP.Core.Tests
{
    public class SecurityAndSlotsTests
    {
        [Fact]
        public void CorrectPasswordProves_WrongOneDoesNot()
        {
            var salt = PasswordProof.NewSalt();
            var key = PasswordProof.DeriveKey("hunter2", salt);
            var nonce = PasswordProof.NewNonce();

            Assert.True(PasswordProof.Verify(key, nonce, PasswordProof.Prove("hunter2", salt, nonce)));
            Assert.False(PasswordProof.Verify(key, nonce, PasswordProof.Prove("hunter3", salt, nonce)));
        }

        [Fact]
        public void ProofCannotBeReplayedWithAnotherNonce()
        {
            var salt = PasswordProof.NewSalt();
            var key = PasswordProof.DeriveKey("pw", salt);
            var captured = PasswordProof.Prove(key, PasswordProof.NewNonce());
            Assert.False(PasswordProof.Verify(key, PasswordProof.NewNonce(), captured));
        }

        [Fact]
        public void SlotClaimRequiresTheHeroPassword()
        {
            var registry = new SlotRegistry(2);
            var salt = PasswordProof.NewSalt();
            var slot = registry.Add("hero_1", "Ana", "Vlandia", salt, PasswordProof.DeriveKey("ana-pw", salt));
            var nonce = PasswordProof.NewNonce();

            Assert.True(registry.VerifyClaim(slot.SlotId, nonce, PasswordProof.Prove("ana-pw", salt, nonce)));
            Assert.False(registry.VerifyClaim(slot.SlotId, nonce, PasswordProof.Prove("guess", salt, nonce)));
            Assert.False(registry.VerifyClaim(99, nonce, PasswordProof.Prove("ana-pw", salt, nonce)));
        }

        [Fact]
        public void SlotLimitIsEnforced()
        {
            var registry = new SlotRegistry(1);
            var salt = PasswordProof.NewSalt();
            registry.Add("h1", "A", "", salt, PasswordProof.DeriveKey("x", salt));
            Assert.False(registry.CanCreate);
            Assert.Throws<InvalidOperationException>(() => registry.Add("h2", "B", "", salt, PasswordProof.DeriveKey("x", salt)));
            Assert.True(registry.IsNameTaken(" a "));
        }

        [Fact]
        public void SlotsSurviveSerialization()
        {
            var registry = new SlotRegistry(3);
            var salt = PasswordProof.NewSalt();
            registry.Add("hero|weird", "Ána | the Bold", "Vlandia", salt, PasswordProof.DeriveKey("pw", salt));

            var loaded = SlotRegistry.Deserialize(registry.Serialize(), 8);
            Assert.Equal(3, loaded.MaxSlots);
            var slot = loaded.Slots.Single();
            Assert.Equal("hero|weird", slot.HeroId);
            Assert.Equal("Ána | the Bold", slot.HeroName);
            var nonce = PasswordProof.NewNonce();
            Assert.True(loaded.VerifyClaim(slot.SlotId, nonce, PasswordProof.Prove("pw", salt, nonce)));
        }

        [Fact]
        public void EmptyOrForeignSlotFiles()
        {
            Assert.Empty(SlotRegistry.Deserialize("", 4).Slots);
            Assert.Throws<FormatException>(() => SlotRegistry.Deserialize("something else", 4));
        }

        [Fact]
        public void TokensWorkOnceAndExpire()
        {
            var tokens = new TokenStore(lifetimeSeconds: 10);
            var t = tokens.Issue(5, 0);
            Assert.Equal(5, tokens.Redeem(t, 1));
            Assert.Null(tokens.Redeem(t, 2));

            var late = tokens.Issue(6, 0);
            Assert.Null(tokens.Redeem(late, 11));
            Assert.Null(tokens.Redeem("", 0));
        }

        [Fact]
        public void SaveTransfersIntactInChunks()
        {
            var data = new byte[200_000];
            new Random(1).NextBytes(data);
            var sender = new SaveSender(data, chunkSize: 30_000);
            var receiver = new SaveReceiver(sender.Size, sender.Hash);

            while (!sender.Done)
            {
                var (offset, chunk) = sender.Peek();
                receiver.Add(offset, chunk);
                sender.Advance(chunk.Length);
            }

            Assert.True(receiver.Complete);
            Assert.Equal(data, receiver.GetVerifiedData());
        }

        [Fact]
        public void CorruptOrMisorderedSaveIsRejected()
        {
            var data = new byte[1000];
            var sender = new SaveSender(data, 400);
            var receiver = new SaveReceiver(sender.Size, sender.Hash);
            Assert.Throws<InvalidDataException>(() => receiver.Add(400, new byte[400]));

            var tampered = new SaveReceiver(sender.Size, sender.Hash);
            tampered.Add(0, Enumerable.Repeat((byte)1, 1000).ToArray());
            Assert.Throws<InvalidDataException>(() => tampered.GetVerifiedData());
        }
    }
}
