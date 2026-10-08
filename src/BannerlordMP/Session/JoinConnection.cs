using System;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Security;
using BannerlordMP.Core.Transfer;
using BannerlordMP.Net;
using BannerlordMP.Steam;

namespace BannerlordMP.Session
{
    /// <summary>
    /// The main-menu half of joining: authenticate with the server, pick or create a hero, and download the world.
    /// The UI drives it through the callbacks; nothing here touches a campaign (none is loaded yet).
    /// </summary>
    internal sealed class JoinConnection : IDisposable
    {
        private readonly ITransport _net;
        private readonly string _playerName;
        private AuthChallengeMessage _challenge;
        private SaveReceiver _receiver;
        private JoinAcceptedMessage _accepted;
        private int _lastProgressTenth = -1;
        private bool _finished;

        /// <summary>The server wants its password. Answer with <see cref="SendHello"/>.</summary>
        public Action<string> PasswordNeeded;
        /// <summary>Authenticated: choose with <see cref="ClaimSlot"/> or <see cref="CreateHero"/>.</summary>
        public Action<SlotListMessage> SlotsReceived;
        public Action<string> Progress;
        /// <summary>World downloaded and verified: save bytes plus the join details.</summary>
        public Action<byte[], JoinAcceptedMessage> WorldReceived;
        public Action<string> Failed;

        public JoinConnection(ConnectTarget target, string playerName)
        {
            Target = target;
            _playerName = playerName;
            _net = target.CreateClientTransport();
            _net.MessageReceived += (peer, message) => OnMessage(message);
            _net.PeerDisconnected += (peer, reason) => Fail(_receiver != null ? "Lost connection while downloading the world." : "Could not reach the server (" + reason + ").");
        }

        public ConnectTarget Target { get; }

        public string ServerName => _challenge?.ServerName ?? Target.ToString();

        /// <summary>The server password last sent, kept in memory only (to rejoin after the character creator).</summary>
        public string ServerPassword { get; private set; }

        public void Poll()
        {
            if (!_finished)
                _net.Poll();
        }

        public void SendHello(string serverPassword)
        {
            ServerPassword = serverPassword;
            byte[] proof = new byte[0];
            if (_challenge.PasswordRequired)
                proof = PasswordProof.Prove(serverPassword ?? string.Empty, _challenge.ServerSalt, _challenge.Nonce);
            _net.SendToAll(new HelloMessage
            {
                ModVersion = typeof(JoinConnection).Assembly.GetName().Version.ToString(),
                PlayerName = _playerName,
                ServerProof = proof,
            });
        }

        public void ClaimSlot(SlotInfo slot, string heroPassword)
        {
            _net.SendToAll(new ClaimSlotMessage
            {
                SlotId = slot.SlotId,
                Proof = PasswordProof.Prove(heroPassword ?? string.Empty, slot.Salt, _challenge.Nonce),
            });
            Progress?.Invoke("Checking your hero...");
        }

        /// <param name="sheet">The hero from the character creator, or null for a quick create.</param>
        public void CreateHero(string name, string cultureId, bool isFemale, string heroPassword, HeroSheet sheet = null)
        {
            var salt = PasswordProof.NewSalt();
            _net.SendToAll(new CreateHeroMessage
            {
                HeroName = name,
                CultureId = cultureId,
                IsFemale = isFemale,
                Salt = salt,
                Key = PasswordProof.DeriveKey(heroPassword ?? string.Empty, salt),
                Sheet = sheet,
            });
            Progress?.Invoke("Creating your hero...");
        }

        public void Dispose()
        {
            _finished = true;
            _net.Dispose();
        }

        private void OnMessage(INetMessage message)
        {
            switch (message)
            {
                case AuthChallengeMessage challenge:
                    if (challenge.ProtocolVersion != MessageCodec.ProtocolVersion)
                    {
                        Fail($"Version mismatch (server protocol {challenge.ProtocolVersion}, yours {MessageCodec.ProtocolVersion}). Use the same mod version.");
                        return;
                    }
                    _challenge = challenge;
                    Log.Info($"Join: connected to '{challenge.ServerName}' (password: {challenge.PasswordRequired})");
                    if (challenge.PasswordRequired)
                        PasswordNeeded?.Invoke(challenge.ServerName);
                    else
                        SendHello(null);
                    break;

                case SlotListMessage slots:
                    Log.Info($"Join: {slots.Slots.Count}/{slots.MaxSlots} slots, {slots.Cultures.Count} cultures");
                    SlotsReceived?.Invoke(slots);
                    break;

                case RejectMessage reject:
                    Log.Info("Join: rejected: " + reject.Reason);
                    Fail(reject.Reason);
                    break;

                case JoinAcceptedMessage accepted:
                    Log.Info($"Join: accepted as {accepted.HeroName}, world is {accepted.SaveSize} bytes");
                    _accepted = accepted;
                    try
                    {
                        _receiver = new SaveReceiver(accepted.SaveSize, accepted.SaveHash);
                    }
                    catch (Exception e)
                    {
                        Fail(e.Message);
                        return;
                    }
                    Progress?.Invoke($"Downloading the world ({accepted.SaveSize / (1024 * 1024.0):0.0} MB)...");
                    break;

                case SaveChunkMessage chunk when _receiver != null:
                    try
                    {
                        _receiver.Add(chunk.Offset, chunk.Data);
                    }
                    catch (Exception e)
                    {
                        Fail("World download failed: " + e.Message);
                        return;
                    }
                    var tenth = (int)(_receiver.Progress * 10);
                    if (tenth != _lastProgressTenth && tenth < 10)
                    {
                        _lastProgressTenth = tenth;
                        Progress?.Invoke($"Downloading the world: {tenth * 10}%");
                    }
                    if (_receiver.Complete)
                        Finish();
                    break;
            }
        }

        private void Finish()
        {
            byte[] data;
            try
            {
                data = _receiver.GetVerifiedData();
            }
            catch (Exception e)
            {
                Fail(e.Message);
                return;
            }
            Dispose();
            WorldReceived?.Invoke(data, _accepted);
        }

        private void Fail(string reason)
        {
            if (_finished)
                return;
            Dispose();
            Failed?.Invoke(reason);
        }
    }
}
