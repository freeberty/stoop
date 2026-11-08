namespace Intersect.Nostr;

/// <summary>BIP-340 signature verification. Swappable so a vetted library can be used in production.</summary>
public interface ISchnorrVerifier
{
    bool Verify(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> message32, ReadOnlySpan<byte> signature64);
}

/// <summary>Dependency-free fallback (used where no secp256k1 library can be restored, and in tests).</summary>
public sealed class ManagedSchnorrVerifier : ISchnorrVerifier
{
    public bool Verify(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> message32, ReadOnlySpan<byte> signature64)
        => Secp256k1.VerifySchnorr(publicKey32, message32, signature64);
}
