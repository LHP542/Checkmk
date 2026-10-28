using Checkmk.App.Services;

// Signiert ein Release-ZIP und schreibt das update.json daneben.
//
// Derselbe Ablauf wie frueher in Checkmk.App --sign-update, nur ohne
// Windows-Bindung. Aufruf:
//
//     checkmk-sign <paket.zip> <version> <privkey-base64> [ziel]

if (args.Length < 3)
{
    Console.Error.WriteLine(
        "Aufruf: checkmk-sign <paket.zip> <version> <privkey-base64> [ziel]");
    return 2;
}

var zip = args[0];
var version = args[1];
var privateKey = args[2];
var ziel = args.Length > 3 && !args[3].StartsWith('-')
    ? args[3]
    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zip)) ?? ".", "update.json");

if (!File.Exists(zip))
{
    Console.Error.WriteLine($"Paket nicht gefunden: {zip}");
    return 2;
}

try
{
    var manifest = new UpdateManifest(
        Version: version.TrimStart('v', 'V'),
        File: Path.GetFileName(zip),
        Sha256: UpdateSignature.HashFile(zip),
        Size: new FileInfo(zip).Length);

    var signiert = new SignedUpdateManifest
    {
        Version = manifest.Version,
        File = manifest.File,
        Sha256 = manifest.Sha256,
        Size = manifest.Size,
        Signature = UpdateSignature.Sign(manifest, privateKey)
    };

    File.WriteAllText(ziel, UpdateSignature.ToJson(signiert));
    Console.WriteLine($"Geschrieben: {ziel}");
    Console.WriteLine($"  Version {signiert.Version}, {signiert.Size} Bytes, SHA-256 {signiert.Sha256}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fehlgeschlagen: {ex.Message}");
    return 1;
}
