// Release gate for the license trust root:
// 1) The issuer private key must derive the public key embedded in the client.
// 2) The issuer's exported public-key file must match the same key.
// 3) A random key must not match the official key.
using System.Security.Cryptography;

AppContext.SetData("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

const string officialPublicKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEoieaqt2/vVECWdyLfmHc+tJOTe1WKtGtQ8fWN6u0QD9+Jz8/CkbzFF7aHwZua5MzPL6HJvyBzi+RwDzxshSVhA==";

var issuerDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "VisionWorkbenchLicenseIssuer");
var privateKeyPath = Path.Combine(issuerDirectory, "license-signing-private.pem");
var publicKeyPath = Path.Combine(issuerDirectory, "license-public-key.txt");
if (!File.Exists(privateKeyPath))
    throw new FileNotFoundException("License issuer private key was not found.", privateKeyPath);

string derived;
using (var official = ECDsa.Create())
{
    official.ImportFromPem(File.ReadAllText(privateKeyPath));
    derived = Convert.ToBase64String(official.ExportSubjectPublicKeyInfo());
}

var privateMatches = string.Equals(derived, officialPublicKey, StringComparison.Ordinal);
Console.WriteLine($"Issuer private key derives embedded public key: {privateMatches}");
if (!privateMatches)
    throw new InvalidOperationException("Issuer private key does not match the public key embedded in the client.");

if (!File.Exists(publicKeyPath))
    throw new FileNotFoundException("License issuer public key was not found.", publicKeyPath);
var exported = File.ReadAllText(publicKeyPath).Trim();
var exportedMatches = string.Equals(exported, officialPublicKey, StringComparison.Ordinal);
Console.WriteLine($"Issuer exported public key matches embedded public key: {exportedMatches}");
if (!exportedMatches)
    throw new InvalidOperationException("Issuer exported public key does not match the public key embedded in the client.");

using (var random = ECDsa.Create(ECCurve.NamedCurves.nistP256))
{
    var randomPublicKey = Convert.ToBase64String(random.ExportSubjectPublicKeyInfo());
    var matches = string.Equals(randomPublicKey, officialPublicKey, StringComparison.Ordinal);
    Console.WriteLine($"Random key matches embedded public key: {matches} (expected False)");
    if (matches)
        throw new InvalidOperationException("Random key unexpectedly matched the official public key.");
}
