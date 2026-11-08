using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Stoop.Nostr;

/// <summary>
/// Minimal pure-C# secp256k1 + BIP-340 Schnorr signatures.
/// Verification works on public data only, so variable-time arithmetic is acceptable.
/// Signing exists for tests and developer tooling ONLY: the server never holds or uses user keys.
/// </summary>
public static class Secp256k1
{
    private static readonly BigInteger P = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F");
    private static readonly BigInteger N = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");
    private static readonly BigInteger Gx = Hex("79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798");
    private static readonly BigInteger Gy = Hex("483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8");

    private readonly record struct JPoint(BigInteger X, BigInteger Y, BigInteger Z)
    {
        public bool IsInfinity => Z.IsZero;
    }

    private static readonly JPoint Infinity = new(BigInteger.One, BigInteger.One, BigInteger.Zero);
    private static readonly JPoint G = new(Gx, Gy, BigInteger.One);

    private static BigInteger Hex(string hex) => BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static BigInteger Mod(BigInteger a) { var r = a % P; return r.Sign < 0 ? r + P : r; }
    private static BigInteger ModN(BigInteger a) { var r = a % N; return r.Sign < 0 ? r + N : r; }
    private static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static JPoint Double(JPoint p)
    {
        if (p.IsInfinity || p.Y.IsZero) return Infinity;
        var ySq = Mod(p.Y * p.Y);
        var s = Mod(4 * p.X * ySq);
        var m = Mod(3 * p.X * p.X);
        var x = Mod(m * m - 2 * s);
        var y = Mod(m * (s - x) - 8 * ySq * ySq);
        var z = Mod(2 * p.Y * p.Z);
        return new JPoint(x, y, z);
    }

    private static JPoint Add(JPoint a, JPoint b)
    {
        if (a.IsInfinity) return b;
        if (b.IsInfinity) return a;
        var z1Sq = Mod(a.Z * a.Z);
        var z2Sq = Mod(b.Z * b.Z);
        var u1 = Mod(a.X * z2Sq);
        var u2 = Mod(b.X * z1Sq);
        var s1 = Mod(a.Y * z2Sq * b.Z);
        var s2 = Mod(b.Y * z1Sq * a.Z);
        if (u1 == u2) return s1 == s2 ? Double(a) : Infinity;
        var h = Mod(u2 - u1);
        var r = Mod(s2 - s1);
        var h2 = Mod(h * h);
        var h3 = Mod(h * h2);
        var u1h2 = Mod(u1 * h2);
        var x = Mod(r * r - h3 - 2 * u1h2);
        var y = Mod(r * (u1h2 - x) - s1 * h3);
        var z = Mod(h * a.Z * b.Z);
        return new JPoint(x, y, z);
    }

    private static JPoint Mul(BigInteger k, JPoint p)
    {
        var result = Infinity;
        var addend = p;
        while (k.Sign > 0)
        {
            if (!k.IsEven) result = Add(result, addend);
            addend = Double(addend);
            k >>= 1;
        }
        return result;
    }

    private static (BigInteger X, BigInteger Y)? ToAffine(JPoint p)
    {
        if (p.IsInfinity) return null;
        var zInv = Inv(p.Z);
        var zInv2 = Mod(zInv * zInv);
        return (Mod(p.X * zInv2), Mod(p.Y * zInv2 * zInv));
    }

    private static (BigInteger X, BigInteger Y)? LiftX(BigInteger x)
    {
        if (x.Sign < 0 || x >= P) return null;
        var c = Mod(x * x * x + 7);
        var y = BigInteger.ModPow(c, (P + 1) / 4, P);
        if (Mod(y * y) != c) return null;
        return (x, y.IsEven ? y : P - y);
    }

    private static byte[] TaggedHash(string tag, params byte[][] parts)
    {
        var tagHash = SHA256.HashData(Encoding.UTF8.GetBytes(tag));
        using var ms = new MemoryStream();
        ms.Write(tagHash);
        ms.Write(tagHash);
        foreach (var part in parts) ms.Write(part);
        return SHA256.HashData(ms.ToArray());
    }

    private static BigInteger FromBytes(ReadOnlySpan<byte> b) => new(b, isUnsigned: true, isBigEndian: true);

    private static byte[] To32(BigInteger v)
    {
        var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == 32) return raw;
        var padded = new byte[32];
        raw.CopyTo(padded, 32 - raw.Length);
        return padded;
    }

    /// <summary>BIP-340 verification. Returns false for any malformed input.</summary>
    public static bool VerifySchnorr(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> message32, ReadOnlySpan<byte> signature64)
    {
        if (publicKey32.Length != 32 || message32.Length != 32 || signature64.Length != 64) return false;

        var lifted = LiftX(FromBytes(publicKey32));
        if (lifted is null) return false;

        var r = FromBytes(signature64[..32]);
        var s = FromBytes(signature64[32..]);
        if (r >= P || s >= N) return false;

        var e = ModN(FromBytes(TaggedHash("BIP0340/challenge", signature64[..32].ToArray(), publicKey32.ToArray(), message32.ToArray())));

        var pub = new JPoint(lifted.Value.X, lifted.Value.Y, BigInteger.One);
        // R = s*G - e*P  ==  s*G + (n-e)*P
        var rPoint = Add(Mul(s, G), Mul(ModN(N - e), pub));
        var affine = ToAffine(rPoint);
        if (affine is null) return false;
        return affine.Value.Y.IsEven && affine.Value.X == r;
    }

    /// <summary>Derive the x-only public key (32 bytes) for a 32-byte secret key. Tests / dev tooling only.</summary>
    public static byte[] GetPublicKey(ReadOnlySpan<byte> secretKey32)
    {
        var d = FromBytes(secretKey32);
        if (d.IsZero || d >= N) throw new ArgumentException("Invalid secret key.", nameof(secretKey32));
        var p = ToAffine(Mul(d, G))!.Value;
        return To32(p.X);
    }

    /// <summary>BIP-340 signing. Tests / dev tooling only.</summary>
    public static byte[] SignSchnorr(ReadOnlySpan<byte> secretKey32, ReadOnlySpan<byte> message32, ReadOnlySpan<byte> aux32)
    {
        var dPrime = FromBytes(secretKey32);
        if (dPrime.IsZero || dPrime >= N) throw new ArgumentException("Invalid secret key.", nameof(secretKey32));
        var pub = ToAffine(Mul(dPrime, G))!.Value;
        var d = pub.Y.IsEven ? dPrime : N - dPrime;
        var pubX = To32(pub.X);

        var aux = TaggedHash("BIP0340/aux", aux32.ToArray());
        var dBytes = To32(d);
        var t = new byte[32];
        for (var i = 0; i < 32; i++) t[i] = (byte)(dBytes[i] ^ aux[i]);

        var kPrime = ModN(FromBytes(TaggedHash("BIP0340/nonce", t, pubX, message32.ToArray())));
        if (kPrime.IsZero) throw new CryptographicException("Nonce generation failed.");
        var rPoint = ToAffine(Mul(kPrime, G))!.Value;
        var k = rPoint.Y.IsEven ? kPrime : N - kPrime;
        var rX = To32(rPoint.X);

        var e = ModN(FromBytes(TaggedHash("BIP0340/challenge", rX, pubX, message32.ToArray())));
        var s = ModN(k + e * d);

        var sig = new byte[64];
        rX.CopyTo(sig, 0);
        To32(s).CopyTo(sig, 32);
        return sig;
    }
}
