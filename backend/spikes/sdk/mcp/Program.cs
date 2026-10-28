using System.Security.Cryptography;
using McpSpike;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// Dev runner for the interop script (python_client_interop.py): a throwaway RSA key
// and one minted token, printed to stdout. The tests build their own app.
const string issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";
var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "spike" };
var settings = new SpikeSettings("http://127.0.0.1:8788/mcp", issuer, ["spike-client"], key,
    ["127.0.0.1:*", "localhost:*"]);
var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
{
    Issuer = issuer,
    Expires = DateTime.UtcNow.AddHours(1),
    Claims = new Dictionary<string, object>
        { ["sub"] = "user-1", ["client_id"] = "spike-client", ["token_use"] = "access" },
    SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
});
Console.WriteLine($"SPIKE_TOKEN={token}");
McpSpikeApp.Build(settings, args, "http://127.0.0.1:8788").Run();
