using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ConfioConsumer;

internal static partial class Program
{
    // 仅用于跨进程、跨平台验证的公开测试密钥，不属于应用默认行为。
    private static byte[] PortableKey => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    private static byte[] NewEncryptionKey()
    {
        var key = new byte[32];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(key);
        return key;
    }

    private static async Task RunEncryption(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 3 && args[1] is "write" or "read")
        {
            var path = Path.GetFullPath(args[2]);
            using var file = new ConfigurationFile(Contexts, path, options: new ConfigurationFileOptions { EncryptionKey = PortableKey });
            if (args[1] == "write")
            {
                Check(!File.Exists(path), "Portable fixtures must use a new path.");
                await file.SaveAsync(new MailSettings { Password = "portable-test-secret", Account = new Credentials { Token = "portable-test-object" } }, cancellationToken);
            }

            var model = await file.ReadAsync<MailSettings>(cancellationToken);
            Check(model.Password == "portable-test-secret" && model.Account!.Token == "portable-test-object", "The same key and purpose must restore strings and complete objects across platforms.");
        }
        else
        {
            var directory = Path.Combine(Path.GetTempPath(), "ConfioEncryption", Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var extension in new[]
                {
                    "json",
                    "yaml"
                }

                )
                    await VerifyEncryption(Path.Combine(directory, "settings." + extension), cancellationToken);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        Console.WriteLine("PASS: AES-GCM protection, shared metadata and explicit key consumption. Dynamic code supported: " + DynamicCodeSupported);
    }

    private static async Task VerifyEncryption(string path, CancellationToken cancellationToken)
    {
        var key = NewEncryptionKey();
        using (var file = new ConfigurationFile(Contexts, path, options: new ConfigurationFileOptions { EncryptionKey = key }))
        {
            Check(file.Read<MailSettings>().Password == "", "An empty protected default does not create a file.");
            file.Save(new MailSettings { Password = "aes-consumer-secret", Account = new Credentials { Token = "aes-object-secret" } });
            await file.UpdateAsync<MailSettings>(mail => mail.Port = 465, cancellationToken);
            Check(!File.ReadAllText(path).Contains("aes-consumer-secret"), "Protected plaintext cannot reach the file.");
        }

        var services = new ServiceCollection().AddConfigurationFile(Contexts, path, options: new ConfigurationFileOptions { EncryptionKey = key });
        using (var provider = services.BuildServiceProvider())
        {
            var settings = provider.GetRequiredService<ISettings<MailSettings>>();
            Check((await settings.ReadAsync(cancellationToken)).Account!.Token == "aes-object-secret", "DI must decrypt the same complete object.");
            settings.Update(mail => mail.Port = 587);
        }

        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        await nativeServices.AddConfigurationFileAsync(configurationRoot, Contexts, path, cancellationToken: cancellationToken, options: new ConfigurationFileOptions { EncryptionKey = key });
        using (var provider = nativeServices.BuildServiceProvider())
        {
            Check(provider.GetRequiredService<IConfiguration>()["Mail:credential"] == "aes-consumer-secret", "Native projection must use the explicit key.");
            Check(provider.GetRequiredService<IOptions<MailSettings>>().Value.Port == 587, "Options must use the same snapshot.");
        }

        var hostBuilder = Host.CreateEmptyApplicationBuilder(null);
        await hostBuilder.AddConfigurationFileAsync(Contexts, path, cancellationToken: cancellationToken, options: new ConfigurationFileOptions { EncryptionKey = key });
        using (var host = hostBuilder.Build())
        {
            Check(host.Services.GetRequiredService<ISettings<MailSettings>>().Read().Password == "aes-consumer-secret", "Host must use the explicit key.");
        }

        using var wrong = new ConfigurationFile(Contexts, path, options: new ConfigurationFileOptions { EncryptionKey = NewEncryptionKey() });
        var before = File.ReadAllBytes(path);
        try
        {
            await wrong.ReadAsync<MailSettings>(cancellationToken);
            throw new InvalidOperationException("A different key must fail authentication.");
        }
        catch (CryptographicException)
        {
            Check(before.SequenceEqual(File.ReadAllBytes(path)), "Authentication failure must preserve the original file.");
        }
    }

    private static async Task VerifyPlaintextProtection(string path, byte[]? key, bool asynchronous, CancellationToken cancellationToken)
    {
        var json = Path.GetExtension(path) == ".json";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        void WriteInput(string password) => File.WriteAllText(path, json ? $$$"""
                {"Mail":{"credential":"{{{password}}}","account":{"token":"enc:v1:literal-object-token"},
                 "label":"enc:unprotected"},"External":{"$confio":"ordinary-data"}}
                """ : $"""
                Mail:
                  credential: {password}
                  account:
                    token: enc:v1:literal-object-token
                  label: enc:unprotected
                External:
                  $confio: ordinary-data
                """);
        string ReadCiphertext() => json ? JsonNode.Parse(File.ReadAllText(path))!["Mail"]!["credential"]!.GetValue<string>() : ReadYamlCiphertexts(path)[0];
        WriteInput("load-consumer-secret");
        using var file = new ConfigurationFile(Contexts, path, options: new ConfigurationFileOptions { EncryptionKey = key });
        async Task Reload()
        {
            if (asynchronous)
                await file.ReloadAsync(cancellationToken);
            else
                file.Reload();
        }

        var notifications = 0;
        using var subscription = file.OnChange<MailSettings>(_ => notifications++);
        var mail = asynchronous ? await file.ReadAsync<MailSettings>(cancellationToken) : file.Read<MailSettings>();
        Check(mail.Password == "load-consumer-secret" && mail.Account!.Token == "enc:v1:literal-object-token" && mail.Label == "enc:unprotected" && mail.Port == 587 && notifications == 0, "Initial loading must restore plaintext and complete-object values without interpreting nested prefixes or notifying.");
        var ciphertext = ReadCiphertext();
        var persisted = File.ReadAllText(path);
        var missingPort = json ? JsonNode.Parse(persisted)!["Mail"]!["port"] is null : !persisted.Contains("\n  port:");
        Check(ciphertext.StartsWith("enc:v1:", StringComparison.Ordinal) && !persisted.Contains("load-consumer-secret") && !persisted.Contains("literal-object-token") && persisted.Contains("ordinary-data") && missingPort, "Initial loading must protect declared values, preserve unknown data and avoid persisting missing defaults.");
        var before = File.ReadAllBytes(path);
        await Reload();
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "Loading valid ciphertext must not rewrite the file.");
        WriteInput(ciphertext);
        await Reload();
        Check(ReadCiphertext() == ciphertext && !File.ReadAllText(path).Contains("literal-object-token"), "A mixed file must retain existing ciphertext while protecting the remaining plaintext object.");
        Check(file.Read<MailSettings>().Password == "load-consumer-secret", "Existing ciphertext must still decrypt once.");
        mail.Password = "enc:v1:literal-password";
        if (asynchronous)
            await file.SaveAsync(mail, cancellationToken);
        else
            file.Save(mail);
        await Reload();
        Check(file.Read<MailSettings>().Password == "enc:v1:literal-password" && !File.ReadAllText(path).Contains("literal-password"), "Save must treat prefix-like model values as plaintext.");
        if (asynchronous)
            await file.UpdateAsync<MailSettings>(value => value.Password = ciphertext, cancellationToken);
        else
            file.Update<MailSettings>(value => value.Password = ciphertext);
        await Reload();
        Check(file.Read<MailSettings>().Password == ciphertext && ReadCiphertext() != ciphertext, "Update must encrypt even a valid ciphertext string supplied as model data and restore it without a second decryption.");
        WriteInput("enc:v1:literal-password");
        before = File.ReadAllBytes(path);
        var previousNotifications = notifications;
        try
        {
            await Reload();
            throw new InvalidOperationException("A reserved prefix in a handwritten protected field must not fall back to plaintext.");
        }
        catch (InvalidDataException)
        {
            Check(before.SequenceEqual(File.ReadAllBytes(path)) && file.Read<MailSettings>().Password == ciphertext && notifications == previousNotifications, "Malformed ciphertext must preserve the file, snapshot and notifications.");
        }

        using var firstRead = new ConfigurationFile(Contexts, path, options: new ConfigurationFileOptions { EncryptionKey = key });
        Expect<InvalidDataException>(() => firstRead.Read<MailSettings>());
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "Failed initial loading must not protect or commit other plaintext first.");
    }

    private static async Task RunFileKey(string[] args, CancellationToken cancellationToken)
    {
        Check(args.Length == 5 && args[1] is "write" or "read" or "missing" or "corrupt" or "cancel" && args[4] is "sync" or "async",
            "Use: file-key write|read|missing|corrupt|cancel path keyPath sync|async.");
        var path = Path.GetFullPath(args[2]);
        var keyPath = Path.GetFullPath(args[3]);
        using var file = new ConfigurationFile(Contexts, path, new ConfigurationFileOptions { KeyFilePath = keyPath });
        var asynchronous = args[4] == "async";
        if (args[1] == "cancel")
        {
            Check(!File.Exists(path) && !File.Exists(keyPath), "Cancellation requires new, isolated file and key paths.");
            file.Read<MailSettings>();
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            using var keyLock = new FileStream(keyPath + ".confio.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pending = file.SaveAsync(new MailSettings { Password = "cancelled-key-secret" }, cancellation.Token);
            Check(!pending.IsCompleted, "The held key lock must prevent a protected save from completing.");
            cancellation.Cancel();
            try
            {
                await pending;
                throw new InvalidOperationException("Blocked key access must observe cancellation.");
            }
            catch (OperationCanceledException)
            {
                Check(!File.Exists(path) && !File.Exists(keyPath), "Cancellation must leave no configuration or key file.");
            }

            Console.WriteLine("PASS: cancellation while waiting for the automatic key file lock.");
            return;
        }

        if (args[1] == "write")
        {
            Check(!File.Exists(path), "Automatic key verification requires a new configuration file.");
            var model = new MailSettings
            {
                Password = "file-key-test-secret",
                Account = new Credentials
                {
                    Token = "file-key-test-object"
                }
            };
            if (asynchronous)
                await file.SaveAsync(model, cancellationToken);
            else
                file.Save(model);
        }

        if (args[1] is "missing" or "corrupt")
        {
            var before = File.ReadAllBytes(path);
            var keyBefore = File.Exists(keyPath) ? File.ReadAllBytes(keyPath) : null;
            try
            {
                if (asynchronous)
                    await file.ReadAsync<MailSettings>(cancellationToken);
                else
                    file.Read<MailSettings>();
                throw new InvalidOperationException("Unavailable protection must fail.");
            }
            catch (Exception exception) when (args[1] == "missing" ? exception is FileNotFoundException : exception is CryptographicException or InvalidDataException)
            {
                Check(before.SequenceEqual(File.ReadAllBytes(path)), "Protection failure must preserve the configuration file.");
                Check(keyBefore is null ? !File.Exists(keyPath) : keyBefore.SequenceEqual(File.ReadAllBytes(keyPath)),
                    "A missing or corrupt key must never be replaced during decryption.");
            }
        }
        else
        {
            var model = asynchronous ? await file.ReadAsync<MailSettings>(cancellationToken) : file.Read<MailSettings>();
            Check(model.Password == "file-key-test-secret" && model.Account!.Token == "file-key-test-object", "Automatic keys must survive process restart.");
        }

        Console.WriteLine("PASS: automatic key file " + args[1] + " " + args[4] + ". Dynamic code supported: " + DynamicCodeSupported);
    }
}
