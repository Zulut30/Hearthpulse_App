using System.Numerics;
using System.Security.Cryptography;

namespace Hearthpulse.Core.Updates;

/// <summary>
/// Ed25519 signature check (RFC 8032, pure Ed25519, no prehash), verify only. Ported from the reference implementation
/// in RFC 8032, section 6 (S. Josefsson, I. Liusvaara); Copyright (c) IETF Trust and the persons identified as the
/// document authors; code components of RFCs are licensed under the Revised BSD License (THIRD-PARTY-NOTICES.txt).
/// The key is public and nothing secret is handled, so the arithmetic is plain <see cref="BigInteger"/>, not constant
/// time. Every malformed input is a false, never an exception: a non-canonical or off-curve point, S not below the
/// group order, wrong lengths.
/// </summary>
internal static class Ed25519
{
    public const int PublicKeySize = 32;

    public const int SignatureSize = 64;

    // Base field Z_p.
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    // Group order.
    private static readonly BigInteger Q = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);

    // Curve constant d = -121665 / 121666.
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));

    // Square root of -1.
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);

    private static readonly BigInteger Mask255 = (BigInteger.One << 255) - 1;

    private static readonly Point G = BasePoint();

    /// <summary>True only when <paramref name="signature"/> is a valid signature of <paramref name="message"/> by <paramref name="publicKey"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        if (Decompress(publicKey) is not { } a)
        {
            return false;
        }

        var rBytes = signature[..32];
        if (Decompress(rBytes) is not { } r)
        {
            return false;
        }

        var s = LittleEndian(signature[32..]);
        if (s >= Q)
        {
            return false;
        }

        var hashInput = new byte[32 + PublicKeySize + message.Length];
        rBytes.CopyTo(hashInput);
        publicKey.CopyTo(hashInput.AsSpan(32));
        message.CopyTo(hashInput.AsSpan(32 + PublicKeySize));
        var h = LittleEndian(SHA512.HashData(hashInput)) % Q;

        var sB = Multiply(s, G);
        var hA = Multiply(h, a);
        return Equal(sB, Add(r, hA));
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(BigInteger s, Point p)
    {
        var q = new Point(0, 1, 1, 0);
        while (s > 0)
        {
            if (!s.IsEven)
            {
                q = Add(q, p);
            }

            p = Add(p, p);
            s >>= 1;
        }

        return q;
    }

    private static bool Equal(Point p, Point q) =>
        Mod(p.X * q.Z - q.X * p.Z).IsZero && Mod(p.Y * q.Z - q.Y * p.Z).IsZero;

    /// <summary>The x coordinate whose low bit is <paramref name="sign"/>; null when there is none (or y is not canonical).</summary>
    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P)
        {
            return null;
        }

        var x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        if (x2.IsZero)
        {
            return sign == 1 ? null : BigInteger.Zero;
        }

        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero)
        {
            x = Mod(x * SqrtM1);
        }

        if (!Mod(x * x - x2).IsZero)
        {
            return null;
        }

        if ((int)(x & 1) != sign)
        {
            x = P - x;
        }

        return x;
    }

    private static Point? Decompress(ReadOnlySpan<byte> bytes)
    {
        var y = LittleEndian(bytes);
        var sign = (int)(y >> 255);
        y &= Mask255;
        return RecoverX(y, sign) is { } x ? new Point(x, y, 1, Mod(x * y)) : null;
    }

    private static Point BasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, 0)!.Value;
        return new Point(x, y, 1, Mod(x * y));
    }

    private static BigInteger LittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static BigInteger Inverse(BigInteger x) => BigInteger.ModPow(Mod(x), P - 2, P);

    private static BigInteger Mod(BigInteger x)
    {
        var r = BigInteger.Remainder(x, P);
        return r.Sign < 0 ? r + P : r;
    }
}
