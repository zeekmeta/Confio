using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Confio.Formats;

namespace Confio.Internal;

internal sealed class FileDefinition
{
    internal FileDefinition(IEnumerable<ISettingsContext> contexts, string? path, ConfigurationFileOptions? options)
    {
        if (contexts is null) throw new ArgumentNullException(nameof(contexts));
        options ??= new ConfigurationFileOptions();
        if (options.Protection is < ConfigurationProtection.Auto or > ConfigurationProtection.AesGcm)
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown protection mode.");
        if (options.Format is not null) FileFormat.FromFormat(options.Format.Value);
        if (options.EncryptionKey is not null && options.EncryptionKey.Length != 32)
            throw new ArgumentException("An AES-256-GCM key must contain exactly 32 bytes.", nameof(options));
        if (options.EncryptionKey is not null && options.KeyFilePath is not null)
            throw new ArgumentException("Select an encryption key or an automatic key file.", nameof(options));
        var aesSource = options.EncryptionKey is not null || options.KeyFilePath is not null;
        if (options.Protector is not null && (aesSource || options.Protection != ConfigurationProtection.Auto))
            throw new ArgumentException("An external protector cannot be combined with built-in protection settings.", nameof(options));
        if (options.Protection == ConfigurationProtection.Dpapi && aesSource)
            throw new ArgumentException("DPAPI does not use AES key settings.", nameof(options));

        Protection = options.Protection == ConfigurationProtection.Auto
            ? (aesSource || !DefaultPaths.IsWindows ? ConfigurationProtection.AesGcm : ConfigurationProtection.Dpapi)
            : options.Protection;
        if (options.Protector is null && Protection == ConfigurationProtection.Dpapi && !DefaultPaths.IsWindows)
            throw new PlatformNotSupportedException("DPAPI requires Windows.");
        FilePath = Path.GetFullPath(path ?? DefaultPaths.Configuration(options.Format ?? ConfigurationFormat.Json));
        Format = options.Format is { } format ? FileFormat.FromFormat(format) : FileFormat.FromPath(FilePath);
        Encoding = (Encoding)(options.Encoding ?? throw new ArgumentNullException(nameof(options.Encoding))).Clone();
        Encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        Encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        Format.ValidateEncoding(Encoding);
        KeyFilePath = options.Protector is null && Protection == ConfigurationProtection.AesGcm && options.EncryptionKey is null
            ? Path.GetFullPath(options.KeyFilePath ?? DefaultPaths.Key()) : null;
        if (KeyFilePath is not null && string.Equals(FilePath, KeyFilePath,
                DefaultPaths.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The key file must be separate from the configuration file.", nameof(options));
        EncryptionKey = options.EncryptionKey?.ToArray();
        Protector = options.Protector;
        ProtectPlaintextOnLoad = options.ProtectPlaintextOnLoad;

        Declarations = new Dictionary<Type, SettingsDeclaration>();
        foreach (var context in contexts.ToArray())
        {
            if (context is null)
            {
                throw new ArgumentException("Configuration contexts cannot be null.", nameof(contexts));
            }
            foreach (var declaration in context.GetSettingsDeclarations())
            {
                if (declaration is null || Declarations.ContainsKey(declaration.ModelType) ||
                    Declarations.Values.Any(other =>
                        other.SectionPath.Length == 0 || declaration.SectionPath.Length == 0 ||
                        string.Equals(other.SectionPath, declaration.SectionPath, Format.PathComparison) ||
                        other.SectionPath.StartsWith(declaration.SectionPath + ":", Format.PathComparison) ||
                        declaration.SectionPath.StartsWith(other.SectionPath + ":", Format.PathComparison)))
                {
                    throw new ArgumentException("Configuration models and section owners must not overlap.", nameof(contexts));
                }
                Format.Validate(declaration);
                Declarations.Add(declaration.ModelType, declaration);
            }
        }
        if (Declarations.Count == 0)
        {
            throw new ArgumentException("At least one settings declaration must be selected.", nameof(contexts));
        }
    }

    internal ConfigurationProtection Protection { get; }
    internal byte[]? EncryptionKey { get; }
    internal string? KeyFilePath { get; }
    internal IConfigurationProtector? Protector { get; }
    internal bool ProtectPlaintextOnLoad { get; }
    internal string FilePath { get; }
    internal FileFormat Format { get; }
    internal Encoding Encoding { get; }
    internal Dictionary<Type, SettingsDeclaration> Declarations { get; }
}
