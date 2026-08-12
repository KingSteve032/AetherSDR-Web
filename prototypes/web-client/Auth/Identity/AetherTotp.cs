using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AetherSDR.Web.Auth.Identity;

internal static class AetherTotp
{
    private const int Digits = 6;
    private const int PeriodSeconds = 30;

    internal static bool TryVerify(
        ReadOnlySpan<byte> secret,
        string? code,
        DateTimeOffset now,
        out long acceptedStep)
    {
        acceptedStep = -1;
        if (secret.Length != 20 ||
            code is not { Length: Digits } ||
            code.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        long currentStep = now.ToUnixTimeSeconds() / PeriodSeconds;
        ReadOnlySpan<int> driftSteps = [0, -1, 1];
        foreach (int drift in driftSteps)
        {
            long candidate = currentStep + drift;
            if (candidate >= 0 &&
                FixedTimeCodeEquals(
                    Compute(secret, candidate),
                    code))
            {
                acceptedStep = candidate;
                return true;
            }
        }
        return false;
    }

    private static string Compute(
        ReadOnlySpan<byte> secret,
        long step)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(secret, counter);
        try
        {
            int offset = hash[^1] & 0x0f;
            int binaryCode =
                ((hash[offset] & 0x7f) << 24) |
                ((hash[offset + 1] & 0xff) << 16) |
                ((hash[offset + 2] & 0xff) << 8) |
                (hash[offset + 3] & 0xff);
            return (binaryCode % 1_000_000).ToString(
                $"D{Digits}",
                CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static bool FixedTimeCodeEquals(
        string expected,
        string actual)
    {
        byte[] expectedBytes = Encoding.ASCII.GetBytes(expected);
        byte[] actualBytes = Encoding.ASCII.GetBytes(actual);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }
}
