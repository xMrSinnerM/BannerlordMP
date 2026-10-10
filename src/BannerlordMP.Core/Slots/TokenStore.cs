using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Security;

namespace BannerlordMP.Core.Slots
{
    /// <summary>
    /// One-time tokens that let a player who just downloaded the world reconnect after loading it, without
    /// typing their passwords again.
    /// </summary>
    public sealed class TokenStore
    {
        private readonly Dictionary<string, (int SlotId, double ExpiresAt)> _tokens = new Dictionary<string, (int, double)>();
        private readonly double _lifetimeSeconds;

        public TokenStore(double lifetimeSeconds = 600)
        {
            _lifetimeSeconds = lifetimeSeconds;
        }

        public string Issue(int slotId, double nowSeconds)
        {
            Prune(nowSeconds);
            var token = PasswordProof.NewToken();
            _tokens[token] = (slotId, nowSeconds + _lifetimeSeconds);
            return token;
        }

        /// <returns>The slot the token was issued for, or null. A token works once.</returns>
        public int? Redeem(string token, double nowSeconds)
        {
            Prune(nowSeconds);
            if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var entry))
                return null;
            _tokens.Remove(token);
            return entry.SlotId;
        }

        private void Prune(double nowSeconds)
        {
            foreach (var expired in _tokens.Where(t => t.Value.ExpiresAt < nowSeconds).Select(t => t.Key).ToList())
                _tokens.Remove(expired);
        }
    }
}
