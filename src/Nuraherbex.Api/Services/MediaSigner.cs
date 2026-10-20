using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Short-lived signed links for customer media. Browsers cannot send a login header when loading an image or audio tag,
/// so each link carries its own expiry and signature; it only opens the one file it was made for.
/// </summary>
public class MediaSigner(IOptions<JwtOptions> jwt)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private byte[] Key => Encoding.UTF8.GetBytes("wa-media:" + jwt.Value.Secret);

    private string Mac(long messageId, long expires) =>
        Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"{messageId}.{expires}"))).ToLowerInvariant();

    /// <summary>Path relative to the API root, e.g. api/whatsapp-media/12?exp=1760000000&amp;sig=ab12…</summary>
    public string Url(long messageId, DateTime? nowUtc = null)
    {
        var exp = new DateTimeOffset((nowUtc ?? DateTime.UtcNow).Add(Lifetime)).ToUnixTimeSeconds();
        return $"api/whatsapp-media/{messageId}?exp={exp}&sig={Mac(messageId, exp)}";
    }

    public bool IsValid(long messageId, long expires, string? signature, DateTime? nowUtc = null)
    {
        if (string.IsNullOrEmpty(signature) || expires < new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeSeconds()) return false;
        var expected = Encoding.ASCII.GetBytes(Mac(messageId, expires));
        var given = Encoding.ASCII.GetBytes(signature.ToLowerInvariant());
        return expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
