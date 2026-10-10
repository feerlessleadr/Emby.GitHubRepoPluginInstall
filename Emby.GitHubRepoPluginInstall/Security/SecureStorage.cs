using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.GitHubRepoPluginInstall.Security;

/// <summary>
/// Protects the GitHub token stored in the plugin's config file.
/// New values use AES-256-GCM with a random key kept in its own file, away from the config. That keeps the
/// token out of reach when the config file is shared or backed up on its own (a forum post, a support bundle).
/// It is not protection against someone who can read both files as the Emby user.
/// Values written by earlier versions (XOR with a machine-name key, or plain base64) can still be read and
/// are re-saved in the new format.
/// </summary>
public class SecureStorage : ISecureStorage
{
    private const string CurrentPrefix = "v2:";
    private const int    NonceSize     = 12;
    private const int    TagSize       = 16;
    private const int    KeySize       = 32;

    private static readonly object KeyLock = new object();

    private readonly ILogger _logger;
    private readonly string  _keyFilePath;
    private readonly byte[]  _legacyKey;

    public SecureStorage(ILogger logger, string keyFilePath)
    {
        _logger     = logger;
        _keyFilePath = keyFilePath;

        // Only used to read tokens saved by earlier versions
        var machineId = Environment.MachineName + "EmbyGitHubPlugin2024";
        using (var sha256 = SHA256.Create())
        {
            _legacyKey = sha256.ComputeHash(Encoding.UTF8.GetBytes(machineId));
        }
    }

    /// <summary>True when the value is in the current AES-GCM format.</summary>
    public static bool IsCurrentFormat(string protectedText)
    {
        return !string.IsNullOrEmpty(protectedText) && protectedText.StartsWith(CurrentPrefix, StringComparison.Ordinal);
    }

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        var key = GetOrCreateKey();
        if (key == null)
            throw new InvalidOperationException("The token key file could not be read or created, so the token was not saved.");

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce      = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher     = new byte[plainBytes.Length];
        var tag        = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce,  0, packed, 0,                  NonceSize);
        Buffer.BlockCopy(tag,    0, packed, NonceSize,          TagSize);
        Buffer.BlockCopy(cipher, 0, packed, NonceSize + TagSize, cipher.Length);

        return CurrentPrefix + Convert.ToBase64String(packed);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return protectedText;

        if (IsCurrentFormat(protectedText))
            return UnprotectCurrent(protectedText);

        return UnprotectLegacy(protectedText);
    }

    private string UnprotectCurrent(string protectedText)
    {
        try
        {
            var key = ReadKey();
            if (key == null)
            {
                _logger.Error("The token key file is missing, so the saved GitHub token can't be read. Enter the token again.");
                return null;
            }

            var packed = Convert.FromBase64String(protectedText.Substring(CurrentPrefix.Length));
            if (packed.Length < NonceSize + TagSize)
                return null;

            var nonce  = new byte[NonceSize];
            var tag    = new byte[TagSize];
            var cipher = new byte[packed.Length - NonceSize - TagSize];
            Buffer.BlockCopy(packed, 0,                   nonce,  0, NonceSize);
            Buffer.BlockCopy(packed, NonceSize,           tag,    0, TagSize);
            Buffer.BlockCopy(packed, NonceSize + TagSize, cipher, 0, cipher.Length);

            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain);
            }

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // Never hand the stored ciphertext back as if it were the token
            _logger.Error("Could not decrypt the saved GitHub token ({0}). Enter the token again.", ex.GetType().Name);
            return null;
        }
    }

    // Reads values written by versions before the AES-GCM format.
    private string UnprotectLegacy(string protectedText)
    {
        try
        {
            var obfuscatedBytes = Convert.FromBase64String(protectedText);
            var plainBytes      = new byte[obfuscatedBytes.Length];

            for (int i = 0; i < obfuscatedBytes.Length; i++)
                plainBytes[i] = (byte)(obfuscatedBytes[i] ^ _legacyKey[i % _legacyKey.Length]);

            var xorResult = Encoding.UTF8.GetString(plainBytes);
            if (LooksLikeToken(xorResult))
                return xorResult;

            // Some old installs stored the token as plain base64
            var base64Result = Encoding.UTF8.GetString(obfuscatedBytes);
            if (LooksLikeToken(base64Result))
                return base64Result;

            return xorResult;
        }
        catch (FormatException)
        {
            // Oldest installs stored the token as plain text
            return protectedText;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to read the saved GitHub token: {0}", ex.Message);
            return null;
        }
    }

    private static bool LooksLikeToken(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
            if (c < 0x21 || c > 0x7E) return false;

        return true;
    }

    private byte[] ReadKey()
    {
        lock (KeyLock)
        {
            if (!File.Exists(_keyFilePath)) return null;

            var text = File.ReadAllText(_keyFilePath).Trim();
            var key  = Convert.FromBase64String(text);
            return key.Length == KeySize ? key : null;
        }
    }

    private byte[] GetOrCreateKey()
    {
        lock (KeyLock)
        {
            if (File.Exists(_keyFilePath))
                return ReadKey();

            var dir = Path.GetDirectoryName(_keyFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var key = RandomNumberGenerator.GetBytes(KeySize);
            File.WriteAllText(_keyFilePath, Convert.ToBase64String(key));

            try
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not restrict permissions on the token key file: {0}", ex.Message);
            }

            _logger.Info("Created a new key file for the saved GitHub token.");
            return key;
        }
    }

    public Task<string> ProtectAsync(string plainText)
    {
        return Task.FromResult(Protect(plainText));
    }

    public Task<string> UnprotectAsync(string protectedText)
    {
        return Task.FromResult(Unprotect(protectedText));
    }
}
