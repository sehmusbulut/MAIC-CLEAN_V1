using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MAIC.IntegrationTests;

// The application still runs its real OIDC handler; only the remote provider is local.
internal sealed class OidcTestProvider : HttpMessageHandler
{
    internal const string Issuer = "https://oidc.example.test";
    internal const string ClientId = "maic-protocol-tests";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _otherRsa = RSA.Create(2048);
    internal string Nonce { get; set; } = "";
    internal string Subject { get; } = Guid.NewGuid().ToString("N");
    internal string Fault { get; set; } = "none";
    internal Guid SpoofedUserId { get; set; } = Guid.NewGuid();
    internal string? TokenRequest { get; private set; }

    internal OpenIdConnectConfiguration Configuration
    {
        get
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                AuthorizationEndpoint = Issuer + "/authorize",
                TokenEndpoint = Issuer + "/token"
            };
            configuration.SigningKeys.Add(new RsaSecurityKey(_rsa) { KeyId = "local-provider" });
            return configuration;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri != Issuer + "/token")
            throw new InvalidOperationException("Unexpected OIDC backchannel request: " + request.RequestUri);

        TokenRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
        var key = new RsaSecurityKey(Fault == "signature" ? _otherRsa : _rsa) { KeyId = "local-provider" };
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: Fault == "issuer" ? "https://untrusted.example.test" : Issuer,
            audience: Fault == "audience" ? "another-application" : ClientId,
            claims:
            [
                new Claim("sub", Fault == "blank-subject" ? " " : Subject),
                new Claim("nonce", Fault == "nonce" ? "unrelated-nonce" : Nonce),
                new Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new Claim("email", "provider@example.test"),
                new Claim("role", "administrator"), new Claim("maic:user_id", SpoofedUserId.ToString())
            ],
            notBefore: now.AddMinutes(-20),
            expires: Fault == "expired" ? now.AddMinutes(-10) : now.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        var json = JsonSerializer.Serialize(new
        {
            access_token = "opaque-provider-access-token",
            token_type = "Bearer",
            expires_in = 300,
            id_token = new JwtSecurityTokenHandler().WriteToken(token)
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
            _otherRsa.Dispose();
        }
        base.Dispose(disposing);
    }
}



