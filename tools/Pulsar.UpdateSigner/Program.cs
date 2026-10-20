using System.Security.Cryptography;
using System.Text;
using Pulsar.Contracts;
using Pulsar.UpdateSigner;

// pulsar-update-signer: makes the update signing key and signs releases with it (docs/guides/releasing.md).
// The passphrase is typed (not shown), or read from PULSAR_UPDATE_KEY_PASSPHRASE: never a command-line argument.
const string Usage = """
    Usage:
      pulsar-update-signer new-key <key file>                 Makes a signing key, encrypted with a passphrase, and prints its public key.
      pulsar-update-signer public-key <key file>              Prints a key file's public key and id.
      pulsar-update-signer sign <key file> <manifest>         Signs a release's pulsar-update.json: writes <manifest>.sig.
                           [--allow-untrusted-key]            (tests only: a key UpdateKeys.Trusted does not list)
      pulsar-update-signer verify <manifest> [--trust <key>]  Checks <manifest>.sig against the keys this build trusts (or <key>).
    """;

try
{
    switch (args)
    {
        case ["new-key", var path]:
            {
                if (File.Exists(path))
                {
                    return Fail($"{path} exists: a key is never written over another.");
                }

                var passphrase = Passphrase("Passphrase for the new key", confirm: true);
                var publicKey = Signing.NewKey(path, passphrase);
                Console.WriteLine($"Key written to {path} (encrypted). Keep it and its passphrase safe, with a copy offline: a lost key cannot sign updates.");
                PrintPublicKey(publicKey);
                Console.WriteLine("Add the public key to UpdateKeys.Trusted (src/Pulsar.Contracts/Updates.cs), so releases built from then on trust it.");
                return 0;
            }

        case ["public-key", var path]:
            {
                using var key = Signing.Open(path, Passphrase("Passphrase of the key", confirm: false));
                PrintPublicKey(key.ExportSubjectPublicKeyInfo());
                return 0;
            }

        case ["sign", var path, var manifestPath, .. var rest] when rest is [] or ["--allow-untrusted-key"]:
            {
                var manifest = File.ReadAllBytes(manifestPath);
                using var key = Signing.Open(path, Passphrase("Passphrase of the key", confirm: false));
                var signature = Signing.Sign(key, manifest, allowUntrusted: rest.Length > 0);
                File.WriteAllBytes(manifestPath + ".sig", signature);
                Console.WriteLine($"Signed {UpdateVerifier.Read(manifest)!.Version}: {manifestPath}.sig ({ProtocolCrypto.Fingerprint(key.ExportSubjectPublicKeyInfo())})");
                return 0;
            }

        case ["verify", var manifestPath, .. var rest] when rest is [] or ["--trust", _]:
            {
                var verifier = rest is ["--trust", var trusted] ? new UpdateVerifier([trusted]) : UpdateVerifier.Default;
                var check = verifier.Verify(File.ReadAllBytes(manifestPath), File.ReadAllBytes(manifestPath + ".sig"));
                if (check.Manifest is null)
                {
                    return Fail($"{check.ErrorCode}: {check.Error}");
                }

                Console.WriteLine($"Pulsar {check.Manifest.Version}, signed by {check.KeyId}: {check.Manifest.Files.Count} file(s).");
                return 0;
            }

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static void PrintPublicKey(byte[] publicKey)
{
    Console.WriteLine($"Public key: {Convert.ToBase64String(publicKey)}");
    Console.WriteLine($"Key id:     {ProtocolCrypto.Fingerprint(publicKey)}");
}

// Typed without echo; from the environment when there is no console to type in (a script), never from the arguments.
static string Passphrase(string prompt, bool confirm)
{
    if (Environment.GetEnvironmentVariable("PULSAR_UPDATE_KEY_PASSPHRASE") is { Length: > 0 } fromEnvironment)
    {
        return fromEnvironment;
    }

    if (Console.IsInputRedirected)
    {
        throw new InvalidOperationException("No console to type the passphrase in: set PULSAR_UPDATE_KEY_PASSPHRASE.");
    }

    var first = ReadHidden($"{prompt}: ");
    if (confirm)
    {
        Signing.CheckPassphrase(first);
        if (ReadHidden("Again: ") != first)
        {
            throw new ArgumentException("The passphrases differ.");
        }
    }

    return first;
}

static string ReadHidden(string prompt)
{
    Console.Write(prompt);
    var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return text.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0)
            {
                text.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            text.Append(key.KeyChar);
        }
    }
}
