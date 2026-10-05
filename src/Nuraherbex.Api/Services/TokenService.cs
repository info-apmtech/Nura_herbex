using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

public class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _o = options.Value;

    public static SymmetricSecurityKey Key(string secret) => new(Encoding.UTF8.GetBytes(secret));

    public string CreateCustomerToken(Customer c) => Create(
        [new Claim(JwtRegisteredClaimNames.Sub, c.Id), new Claim(ClaimTypes.NameIdentifier, c.Id),
         new Claim(ClaimTypes.Email, c.Email), new Claim(ClaimTypes.Name, c.FullName), new Claim(ClaimTypes.Role, "customer")],
        TimeSpan.FromDays(_o.CustomerTokenDays));

    public string CreateAdminToken(string email) => Create(
        [new Claim(JwtRegisteredClaimNames.Sub, email), new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Role, "admin")],
        TimeSpan.FromHours(_o.AdminTokenHours));

    private string Create(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var creds = new SigningCredentials(Key(_o.Secret), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience, claims, expires: DateTime.UtcNow.Add(lifetime), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
