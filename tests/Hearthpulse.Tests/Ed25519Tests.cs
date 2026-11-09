using System.Numerics;
using System.Text.Json;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.Tests;

/// <summary>
/// The verify-only Ed25519 port: every RFC 8032 §7.1 vector checks out, and every way a signature can be wrong is a
/// plain false, never an exception (a flipped bit, S not below the group order, a non-canonical or invalid point,
/// wrong lengths). A false accept would let a forged release install, so the negative cases matter as much.
/// </summary>
public sealed class Ed25519Tests
{
    // The group order L and the field prime p.
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    // The base point B, compressed (y = 4/5, x even).
    private static readonly byte[] BasePoint = Convert.FromHexString("5866666666666666666666666666666666666666666666666666666666666666");

    public static TheoryData<string> VectorNames() => new(Vectors().Select(v => v.Name));

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Every_rfc_8032_vector_verifies(string name)
    {
        var v = Vectors().Single(x => x.Name == name);
        Assert.True(Ed25519.Verify(v.Public, v.Message, v.Signature));
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void A_flipped_bit_in_the_message_r_or_s_fails(string name)
    {
        var v = Vectors().Single(x => x.Name == name);
        var message = v.Message.Length == 0 ? [0] : (byte[])v.Message.Clone();
        message[^1] ^= 1;
        Assert.False(Ed25519.Verify(v.Public, message, v.Signature));

        foreach (var at in new[] { 0, 17, 31, 32, 50, 62 })
        {
            var signature = (byte[])v.Signature.Clone();
            signature[at] ^= 0x04;
            Assert.False(Ed25519.Verify(v.Public, v.Message, signature));
        }

        var key = (byte[])v.Public.Clone();
        key[5] ^= 0x10;
        Assert.False(Ed25519.Verify(key, v.Message, v.Signature));
    }

    [Fact]
    public void S_at_or_above_the_group_order_fails()
    {
        // S + L verifies the same equation (S·B is unchanged); only the S < L rule stops this second form.
        var v = Vectors()[0];
        var s = new BigInteger(v.Signature.AsSpan(32), isUnsigned: true, isBigEndian: false) + L;
        var signature = v.Signature[..32].Concat(Le(s)).ToArray();
        Assert.False(Ed25519.Verify(v.Public, v.Message, signature));

        var exactlyL = v.Signature[..32].Concat(Le(L)).ToArray();
        Assert.False(Ed25519.Verify(v.Public, v.Message, exactlyL));
    }

    [Fact]
    public void A_non_canonical_public_key_fails()
    {
        // y = p + 1 is the identity's y written the long way. Taken as the identity, (R = B, S = 1) would satisfy
        // S·B = R + h·A for any message: a forgery. Canonical decoding (y < p) rejects it.
        var nonCanonicalIdentity = Le(P + 1);
        var forged = BasePoint.Concat(Le(BigInteger.One)).ToArray();
        Assert.False(Ed25519.Verify(nonCanonicalIdentity, "any release"u8, forged));

        // x = 0 with the sign bit set has no point.
        var negativeZero = Le(BigInteger.One);
        negativeZero[31] |= 0x80;
        Assert.False(Ed25519.Verify(negativeZero, "any release"u8, forged));

        // y = p itself, and all ones.
        Assert.False(Ed25519.Verify(Le(P), "any release"u8, forged));
        Assert.False(Ed25519.Verify(Enumerable.Repeat((byte)0xff, 32).ToArray(), "any release"u8, forged));
    }

    [Fact]
    public void A_non_canonical_or_off_curve_r_fails()
    {
        var v = Vectors()[0];
        var nonCanonical = Le(P + 1).Concat(v.Signature[32..]).ToArray();
        Assert.False(Ed25519.Verify(v.Public, v.Message, nonCanonical));

        // y = 2 is not on the curve (x² = 3/(4d + 1) is not a square mod p); either way the result is a plain false.
        var offCurve = Le(2).Concat(v.Signature[32..]).ToArray();
        Assert.False(Ed25519.Verify(v.Public, v.Message, offCurve));
    }

    [Theory]
    [InlineData(31, 64)]
    [InlineData(33, 64)]
    [InlineData(32, 63)]
    [InlineData(32, 65)]
    [InlineData(0, 0)]
    public void Wrong_lengths_fail_without_an_exception(int keyLength, int signatureLength)
    {
        var v = Vectors()[0];
        var key = new byte[keyLength];
        v.Public.AsSpan(0, Math.Min(32, keyLength)).CopyTo(key);
        var signature = new byte[signatureLength];
        v.Signature.AsSpan(0, Math.Min(64, signatureLength)).CopyTo(signature);
        Assert.False(Ed25519.Verify(key, v.Message, signature));
    }

    private static byte[] Le(BigInteger value)
    {
        var bytes = new byte[32];
        Assert.True(value.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false));
        return bytes;
    }

    private sealed record Vector(string Name, byte[] Public, byte[] Message, byte[] Signature);

    /// <summary>RFC 8032 §7.1, copied from the RFC text into Fixtures/updates/ed25519-rfc8032.json.</summary>
    private static List<Vector> Vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "updates", "ed25519-rfc8032.json")));
        return [.. doc.RootElement.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
            v.GetProperty("name").GetString()!,
            Convert.FromHexString(v.GetProperty("public").GetString()!),
            Convert.FromHexString(v.GetProperty("message").GetString()!),
            Convert.FromHexString(v.GetProperty("signature").GetString()!)))];
    }
}
