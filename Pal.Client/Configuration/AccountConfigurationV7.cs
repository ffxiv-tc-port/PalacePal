using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Pal.Client.Configuration
{
    public sealed class AccountConfigurationV7 : IAccountConfiguration
    {
        private const int DefaultEntropyLength = 16;

        private static readonly ILogger _logger =
            DependencyInjectionContext.LoggerProvider.CreateLogger<AccountConfigurationV7>();

        [JsonConstructor]
        public AccountConfigurationV7()
        {
        }

        public AccountConfigurationV7(string server, Guid accountId)
        {
            Server = server;
            (EncryptedId, Entropy, Format) = EncryptAccountId(accountId);
        }

        [Obsolete("for V1 import")]
        public AccountConfigurationV7(string server, string accountId)
        {
            Server = server;

            if (accountId.StartsWith("s:"))
            {
                EncryptedId = accountId.Substring(2);
                Entropy = ConfigurationData.FixedV1Entropy;
                Format = EFormat.UseProtectedData;
                EncryptIfNeeded();
            }
            else if (Guid.TryParse(accountId, out Guid guid))
                (EncryptedId, Entropy, Format) = EncryptAccountId(guid);
            else
                throw new InvalidOperationException($"Invalid account id format, can't migrate account for server {server}");
        }

        [JsonInclude]
        [JsonRequired]
        public EFormat Format { get; private set; } = EFormat.Unencrypted;

        /// <summary>
        /// Depending on <see cref="Format"/>, this is either a Guid as string or a base64 encoded byte array.
        /// </summary>
        [JsonPropertyName("Id")]
        [JsonInclude]
        [JsonRequired]
        public string EncryptedId { get; private set; } = null!;

        [JsonInclude]
        public byte[]? Entropy { get; private set; }

        [JsonRequired]
        public string Server { get; init; } = null!;

        [JsonIgnore] public bool IsUsable => DecryptAccountId() != null;

        [JsonIgnore] public Guid AccountId => DecryptAccountId() ?? throw new InvalidOperationException("Account id can't be read");

        public List<string> CachedRoles { get; set; } = new();

        // 上一次 DPAPI 解密成功的結果。密文或熵換了（參考不同）就重解；
        // 失敗刻意不記，照舊每次重試。
        private DecryptedId? _decrypted;

        private Guid? DecryptAccountId()
        {
            if (Format == EFormat.UseProtectedData && ConfigurationData.SupportsDpapi)
            {
                string encryptedId = EncryptedId;
                byte[]? entropy = Entropy;
                DecryptedId? decrypted = _decrypted;
                if (decrypted != null && ReferenceEquals(decrypted.EncryptedId, encryptedId) &&
                    ReferenceEquals(decrypted.Entropy, entropy))
                    return decrypted.Value;

                try
                {
                    byte[] guidBytes = ProtectedData.Unprotect(Convert.FromBase64String(encryptedId), entropy, DataProtectionScope.CurrentUser);
                    Guid value = new Guid(guidBytes);
                    _decrypted = new DecryptedId(encryptedId, entropy, value);
                    return value;
                }
                catch (Exception e)
                {
                    _logger.LogTrace(e, "Could not load account id {Id}", EncryptedId);
                    return null;
                }
            }
            else if (Format == EFormat.Unencrypted)
                return Guid.Parse(EncryptedId);
            else if (Format == EFormat.ProtectedDataUnsupported && !ConfigurationData.SupportsDpapi)
                return Guid.Parse(EncryptedId);
            else
                return null;
        }

        private sealed class DecryptedId
        {
            public DecryptedId(string encryptedId, byte[]? entropy, Guid value)
            {
                EncryptedId = encryptedId;
                Entropy = entropy;
                Value = value;
            }

            public string EncryptedId { get; }
            public byte[]? Entropy { get; }
            public Guid Value { get; }
        }

        private (string encryptedId, byte[]? entropy, EFormat format) EncryptAccountId(Guid g)
        {
            if (!ConfigurationData.SupportsDpapi)
                return (g.ToString(), null, EFormat.ProtectedDataUnsupported);
            else
            {
                try
                {
                    byte[] entropy = RandomNumberGenerator.GetBytes(DefaultEntropyLength);
                    byte[] guidBytes = ProtectedData.Protect(g.ToByteArray(), entropy, DataProtectionScope.CurrentUser);
                    return (Convert.ToBase64String(guidBytes), entropy, EFormat.UseProtectedData);
                }
                catch (Exception)
                {
                    return (g.ToString(), null, EFormat.Unencrypted);
                }
            }
        }

        public bool EncryptIfNeeded()
        {
            if (Format == EFormat.Unencrypted)
            {
                var (newId, newEntropy, newFormat) = EncryptAccountId(Guid.Parse(EncryptedId));
                if (newFormat != EFormat.Unencrypted)
                {
                    EncryptedId = newId;
                    Entropy = newEntropy;
                    Format = newFormat;
                    return true;
                }
            }
            else if (Format == EFormat.UseProtectedData && Entropy is { Length: < DefaultEntropyLength })
            {
                Guid? g = DecryptAccountId();
                if (g != null)
                {
                    (EncryptedId, Entropy, Format) = EncryptAccountId(g.Value);
                    return true;
                }
            }

            return false;
        }

        public enum EFormat
        {
            Unencrypted = 1,
            UseProtectedData = 2,

            /// <summary>
            /// Used for filtering: We don't want to overwrite any entries of this value using DPAPI, ever.
            /// This is mostly a wine fallback.
            /// </summary>
            ProtectedDataUnsupported = 3,
        }
    }
}
