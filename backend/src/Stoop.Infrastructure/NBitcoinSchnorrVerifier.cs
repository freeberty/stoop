using Stoop.Nostr;
using NBitcoin.Secp256k1;

namespace Stoop.Infrastructure;

/// <summary>BIP-340 verification backed by the NBitcoin.Secp256k1 library.</summary>
public sealed class NBitcoinSchnorrVerifier : ISchnorrVerifier
{
    public bool Verify(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> message32, ReadOnlySpan<byte> signature64)
    {
        if (publicKey32.Length != 32 || message32.Length != 32 || signature64.Length != 64) return false;
        if (!ECXOnlyPubKey.TryCreate(publicKey32, Context.Instance, out var pubKey) || pubKey is null) return false;
        if (!SecpSchnorrSignature.TryCreate(signature64, out var signature) || signature is null) return false;
        return pubKey.SigVerifyBIP340(signature, message32);
    }
}
