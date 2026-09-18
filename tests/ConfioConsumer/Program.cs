using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using ConfioConsumerModels;
using Microsoft.Extensions.DependencyInjection;

namespace ConfioConsumer
{
    internal static partial class Program
    {
        private static bool DynamicCodeSupported =>
#if NETFRAMEWORK
            true;
#else
            System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
#endif

        private static ISettingsContext[] Contexts => new ISettingsContext[]
        {
            ConsumerSettingsContext.Default, SharedSettingsContext.Default
        };

        private static async Task<int> Main(string[] args)
        {
            // 四种格式包含真实落盘和多进程启动，使用整个验收矩阵的外层期限。
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var elapsed = Stopwatch.StartNew();
            try
            {
                Check(!System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault,
                    "Model conversion must work with reflection disabled.");
                if (args.Length > 0 && args[0] == "file-key")
                {
                    await RunFileKey(args, deadline.Token);
                    return 0;
                }
                if (args.Length > 0 && args[0] == "encryption")
                {
                    await RunEncryption(args, deadline.Token);
                    return 0;
                }
                if (args.Length > 0 && args[0] == "worker")
                {
                    await RunWorker(args, deadline.Token);
                    return 0;
                }

                if (args.Length != 0 && (args.Length != 2 || args[0] != "--directory"))
                    throw new ArgumentException("Use --directory <root> to verify a specific file system.");
                var root = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.GetTempPath();
                var directory = Path.Combine(root, "ConfioConsumer", Guid.NewGuid().ToString("N"));
                Console.WriteLine("Verification directory: " + directory);
                try
                {
                    await VerifyOptions(Path.Combine(directory, "options"), deadline.Token);
                    await VerifyDirect(Path.Combine(directory, "direct", "settings.json"), deadline.Token);
                    await VerifyInheritedProtection(Path.Combine(directory, "inherited", "settings.json"), deadline.Token);
                    foreach (var extension in new[] { "yaml", "yml" })
                    {
                        await VerifyYaml(Path.Combine(directory, "direct-" + extension, "settings." + extension), deadline.Token);
                    }
                    foreach (var extension in new[] { "toml", "ini" })
                    {
                        await VerifyAdditionalFormats(Path.Combine(directory, extension), extension, deadline.Token);
                    }
                    foreach (var extension in new[] { "json", "yaml" })
                    {
                        var folder = Path.Combine(directory, extension);
                        await VerifyFileCommits(Path.Combine(folder, "commits", "settings." + extension), deadline.Token);
                        await VerifyEncryption(Path.Combine(folder, "encryption", "settings." + extension), deadline.Token);
                        foreach (var asynchronous in new[] { false, true })
                        {
                            await VerifyPlaintextProtection(Path.Combine(folder, "plaintext-system-" + asynchronous, "settings." + extension),
                                null, asynchronous, deadline.Token);
                            await VerifyPlaintextProtection(Path.Combine(folder, "plaintext-aes-" + asynchronous, "settings." + extension),
                                PortableKey, asynchronous, deadline.Token);
                        }
                        await VerifySettingsBehavior(Path.Combine(folder, "behavior", "settings." + extension), deadline.Token);
                        await VerifyContainer(Path.Combine(folder, "container", "settings." + extension), deadline.Token);
                        await VerifyNativeContainer(Path.Combine(folder, "native-container", "settings." + extension), deadline.Token);
                        await VerifyHost(Path.Combine(folder, "host", "settings." + extension), deadline.Token);
                        await VerifyProcesses(Path.Combine(folder, "processes", "settings." + extension), deadline.Token);
                    }
                    Console.WriteLine("PASS: static metadata, JSON, YAML/YML, TOML, INI, root models, explicit formats, sync/async, default protection, persistent AES keys, protected value types, automatic plaintext protection and read-only loading, reserved file prefixes and plaintext model input, model validation, immutable updates and defaults, case-sensitive dictionaries, direct subscriptions, multi-file DI, Configuration, Options, Host, cross-project models, and process coordination.");
                    Console.WriteLine("Runtime: " + RuntimeInformation.FrameworkDescription);
                    Console.WriteLine("Process architecture: " + RuntimeInformation.ProcessArchitecture);
                    Console.WriteLine("Dynamic code supported: " + DynamicCodeSupported);
                    Console.WriteLine("Verification duration: " + elapsed.Elapsed);
                }
                catch
                {
                    Console.Error.WriteLine("Verification artifacts retained: " + directory);
                    throw;
                }
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Verification failed after: " + elapsed.Elapsed);
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static async Task VerifyDirect(string path, CancellationToken cancellationToken)
        {
            using var file = new ConfigurationFile(Contexts, path);
            Check(!Directory.Exists(Path.GetDirectoryName(path)), "Construction must not create a directory.");
            Check(file.Read<MailSettings>().Port == 587, "Missing files must use model defaults.");
            Check((await file.ReadAsync<MailSettings>(cancellationToken)).Password == "", "Empty protected defaults must not need keys.");
            file.Reset<MailSettings>();
            await file.ResetAsync<MailSettings>(cancellationToken);
            Check(!Directory.Exists(Path.GetDirectoryName(path)), "Reading and resetting a missing file must not create a directory.");

            var settings = new MailSettings
            {
                Port = 0,
                Label = null,
                Password = "consumer-test-password",
                Account = new Credentials { Token = "consumer-test-account" },
                OptionalToken = new TokenValue { Value = "consumer-test-nullable" }
            };
            settings.Servers.Add(new Credentials { Token = "consumer-test-server" });
            settings.Accounts["a/b~c"] = new Credentials { Token = "consumer-test-dictionary" };
            file.Save(settings);
            settings.Port = 999;
            var text = File.ReadAllText(path);
            Check(!text.Contains("consumer-test-"), "Protected plaintext must not reach the file.");
            var ciphertext = JsonNode.Parse(text)!["Mail"]!["credential"]!.ToJsonString();
            var copy = await file.ReadAsync<MailSettings>(cancellationToken);
            Check(copy.Port == 0 && copy.Label == null, "Explicit zero and null must survive saving.");
            Check(copy.Password == "consumer-test-password" && copy.Account!.Token == "consumer-test-account" &&
                  copy.Servers[0].Token == "consumer-test-server", "Protected strings, objects and collection members must round-trip.");
            Check(copy.OptionalToken!.Value.Value == "consumer-test-nullable" && copy.Accounts["a/b~c"].Token == "consumer-test-dictionary",
                "Nullable structs and dictionary elements must retain declared protection.");
            copy.Servers[0].Token = "edited-copy";
            Check(file.Read<MailSettings>().Servers[0].Token == "consumer-test-server", "Read results must be independent copies.");

            var document = JsonNode.Parse(text)!;
            document["External"] = JsonNode.Parse("{\"number\":9007199254740993,\"text\":\"00123\"}");
            File.WriteAllText(path, document.ToJsonString());
            await file.SaveAsync(new SharedSettings { Count = 2 }, cancellationToken);
            document = JsonNode.Parse(File.ReadAllText(path))!;
            Check(document["Mail"]!["credential"]!.ToJsonString() == ciphertext, "Saving another section must preserve existing ciphertext.");
            Check(document["External"]!["number"]!.GetValue<long>() == 9007199254740993, "Unowned numbers must retain precision.");

            var calls = 0;
            await file.UpdateAsync<MailSettings>(mail => { calls++; mail.Port = 465; }, cancellationToken);
            Check(calls == 1 && file.Read<MailSettings>().Port == 465, "Update callbacks must run exactly once.");
            file.Update<SharedSettings>(shared => shared with { Count = shared.Count + 1 });
            Check(file.Read<SharedSettings>().Count == 3, "A context from another project must participate in the same file.");
            using (var other = new ConfigurationFile(Contexts, path))
            {
                Check(other.Read<MailSettings>().Password == "consumer-test-password", "A new instance must decrypt the saved file.");
                await other.SaveAsync(new MailSettings { Port = 2525 }, cancellationToken);
            }
            Check(file.Read<MailSettings>().Port == 465, "External commits must not silently mutate old snapshots.");
            await file.ReloadAsync(cancellationToken);
            Check(file.Read<MailSettings>().Port == 2525, "ReloadAsync must publish external edits.");
            file.Reload();
            file.Reset<MailSettings>();
            Check(file.Read<MailSettings>().Port == 587, "Reset must restore model defaults.");
            await file.ResetAsync<SharedSettings>(cancellationToken);
            Check(JsonNode.Parse(File.ReadAllText(path))!["External"] != null, "Reset must preserve unowned sections.");
        }

        private static async Task VerifyFileCommits(string path, CancellationToken cancellationToken)
        {
            using var file = new ConfigurationFile(Contexts, path);
            const string label = "配置中文 café\n引号\"与反斜杠\\ 😀";
            const string secret = "consumer-中文保护";
            file.Save(new MailSettings { Label = label, Password = secret });
            await file.SaveAsync(new CounterSettings(), cancellationToken);
            var original = File.ReadAllText(path);
            Check(original.Contains("配置中文 café") && !original.Contains(secret),
                "Unicode must remain readable while protected values remain encrypted.");

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                var calls = 0;
                var notifications = 0;
                using var subscription = file.OnChange<CounterSettings>(_ => notifications++);
                void Increment(CounterSettings counter) { calls++; counter.Count++; }
                for (var index = 0; index < 64; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index % 2 == 0) file.Update<CounterSettings>(Increment);
                    else await file.UpdateAsync<CounterSettings>(Increment, cancellationToken);
                    // 每次重新打开文件观察提交结果，覆盖连续替换与外部读取相邻发生的情况。
                    using var reopened = new ConfigurationFile(Contexts, path);
                    Check(reopened.Read<CounterSettings>().Count == index + 1, "Every replacement must publish a complete document.");
                    var mail = reopened.Read<MailSettings>();
                    Check(mail.Label == label && mail.Password == secret, "Other sections and protected Unicode must survive replacement.");
                }
                Check(calls == 64 && notifications == 64, "File retries must not repeat updates or notifications.");
                Check(await reader.ReadToEndAsync() == original,
                    "An existing reader allowing replacement must retain its original document.");
            }
            // Windows 的待删除文件可能保留到最后一个读者关闭；Framework 通配符也会匹配 8.3 短文件名。
            var temporaryFiles = Directory.GetFiles(Path.GetDirectoryName(path)!)
                .Where(candidate => string.Equals(Path.GetExtension(candidate), ".tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Check(temporaryFiles.Length == 0,
                "Successful commits must not leave temporary files after readers close. Remaining files: " +
                string.Join(", ", temporaryFiles.Select(Path.GetFileName)));
        }

        private static async Task VerifyContainer(string path, CancellationToken cancellationToken)
        {
            var services = new ServiceCollection();
            Check(ReferenceEquals(services, services.AddConfigurationFile(Contexts, path)), "Registration must return the native collection.");
            Expect<InvalidOperationException>(() => services.AddConfigurationFile(Contexts, path));
            ConfigurationFile owned;
            using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
            {
                owned = provider.GetRequiredService<ConfigurationFile>();
                using var first = provider.CreateScope();
                using var second = provider.CreateScope();
                var settings = first.ServiceProvider.GetRequiredService<ISettings<MailSettings>>();
                Check(ReferenceEquals(settings, second.ServiceProvider.GetRequiredService<ISettings<MailSettings>>()), "Scopes must share the default type service.");
                Check(provider.GetService<MailSettings>() == null, "Mutable models must not be singleton services.");
                Check(!Directory.Exists(Path.GetDirectoryName(path)), "Container construction and resolution must not perform file I/O.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Path.GetExtension(path) == ".json"
                    ? """{"Mail":{"credential":"container-test-password"}}""" : "Mail:\n  credential: container-test-password\n");
                Check((await settings.ReadAsync(cancellationToken)).Password == "container-test-password" &&
                      !File.ReadAllText(path).Contains("container-test-password"), "The first DI read must protect handwritten plaintext.");
                await settings.UpdateAsync(mail => mail.Port = 465, cancellationToken);
                Check(owned.Read<MailSettings>().Port == 465, "File and type services must share one state.");
                provider.GetRequiredService<ISettings<SharedSettings>>().Save(new SharedSettings { Count = 1 });
            }
            Expect<ObjectDisposedException>(() => owned.Read<MailSettings>());

            using var borrowed = new ConfigurationFile(Contexts, path);
            using (var provider = new ServiceCollection().AddConfigurationFile(borrowed).BuildServiceProvider())
            {
                Check(ReferenceEquals(borrowed, provider.GetRequiredService<ConfigurationFile>()), "External-instance registration must reuse the supplied instance.");
                Check(provider.GetRequiredService<ISettings<MailSettings>>().Read().Password == "container-test-password", "DI reads must use real DPAPI protection.");
            }
            await borrowed.ResetAsync<MailSettings>(cancellationToken);
            Check(borrowed.Read<MailSettings>().Port == 587, "The container must not dispose a borrowed file.");

            var replacements = new ServiceCollection();
            replacements.AddSingleton<ISettings<MailSettings>>(_ => throw new NotImplementedException("explicit override"));
            replacements.AddConfigurationFile(Contexts, path);
            using var replacementProvider = replacements.BuildServiceProvider();
            Expect<NotImplementedException>(() => replacementProvider.GetRequiredService<ISettings<MailSettings>>());
        }

        private static async Task VerifyProcesses(string path, CancellationToken cancellationToken)
        {
            using var file = new ConfigurationFile(Contexts, path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Path.GetExtension(path) switch
            {
                ".json" => """{"Mail":{"credential":"process-test-secret"},"Counter":{"count":0}}""",
                ".toml" => "[Mail]\ncredential = \"process-test-secret\"\n[Counter]\ncount = 0\n",
                ".ini" => "[Mail]\ncredential=process-test-secret\n[Counter]\ncount=0\n",
                _ => "Mail:\n  credential: process-test-secret\nCounter:\n  count: 0\n"
            });
            var start = path + ".start";
            var ready = new[] { path + ".ready0", path + ".ready1" };
            var children = new Process[2];
            try
            {
                for (var index = 0; index < children.Length; index++)
                {
                    var entry = Environment.GetCommandLineArgs()[0];
                    var isAssembly = entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                    var command = (isAssembly ? Quote(entry) + " " : "") + "worker " + Quote(path) + " " +
                                  Quote(ready[index]) + " " + Quote(start) + " " + (index == 0 ? "sync" : "async");
                    children[index] = Process.Start(new ProcessStartInfo(isAssembly ? "dotnet" : entry, command)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }) ?? throw new InvalidOperationException("Cannot start the coordination consumer.");
                }
                while (ready.Any(marker => !File.Exists(marker)))
                {
                    Check(children.All(child => !child.HasExited), "A worker exited before coordination started.");
                    await Task.Delay(10, cancellationToken);
                }
                File.WriteAllText(start, "start");
                while (children.Any(child => !child.HasExited))
                {
                    await Task.Delay(10, cancellationToken);
                }
                Check(children.All(child => child.ExitCode == 0), "Each independent process must complete its updates.");
                file.Reload();
                Check(file.Read<CounterSettings>().Count == 24 && file.Read<SharedSettings>().Count == 24,
                    "Concurrent processes must preserve updates to both shared and separate sections.");
                Check(file.Read<MailSettings>().Password == "process-test-secret" && !File.ReadAllText(path).Contains("process-test-secret"),
                    "Concurrent initial loads must protect plaintext once and preserve it through subsequent updates.");
            }
            finally
            {
                foreach (var child in children)
                {
                    if (child != null)
                    {
                        if (!child.HasExited)
                        {
                            child.Kill();
                            child.WaitForExit(5000);
                        }
                        child.Dispose();
                    }
                }
            }
        }

        private static async Task RunWorker(string[] args, CancellationToken cancellationToken)
        {
            using var file = new ConfigurationFile(Contexts, args[1]);
            File.WriteAllText(args[2], "ready");
            while (!File.Exists(args[3]))
            {
                await Task.Delay(10, cancellationToken);
            }
            var mail = args[4] == "sync" ? file.Read<MailSettings>() : await file.ReadAsync<MailSettings>(cancellationToken);
            Check(mail.Password == "process-test-secret", "Both processes must read the same secret after coordinated initial protection.");
            for (var iteration = 0; iteration < 12; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (args[4] == "sync")
                {
                    file.Update<CounterSettings>(counter => counter.Count++);
                    file.Update<SharedSettings>(shared => shared with { Count = shared.Count + 1 });
                }
                else
                {
                    await file.UpdateAsync<CounterSettings>(counter => counter.Count++, cancellationToken);
                    await file.UpdateAsync<SharedSettings>(shared => shared with { Count = shared.Count + 1 }, cancellationToken);
                }
            }
        }

        private static string Quote(string argument) => "\"" + argument + "\"";

        private static void Check(bool condition, string behavior)
        {
            if (!condition)
            {
                throw new InvalidOperationException(behavior);
            }
        }

        private static void Expect<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }
    }
}
