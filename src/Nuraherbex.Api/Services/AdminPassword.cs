using System.Security.Cryptography;

namespace Nuraherbex.Api.Services;

/// <summary>Versioned PBKDF2 hashes for the server-configured administrator.</summary>
public static class AdminPassword
{
    /// <summary>Creates a hash in the same versioned format that <see cref="Verify"/> accepts.</summary>
    public static string Hash(string password)
    {
        const int iterations = 210000;
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, 64);
        return $"pbkdf2-sha512:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split(':');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha512" ||
                !int.TryParse(parts[1], out var iterations) || iterations < 210000 || iterations > 1000000)
                return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 32 || expected.Length != 64) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, 64);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
