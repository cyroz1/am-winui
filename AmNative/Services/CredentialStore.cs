using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AmNative.Services;

public sealed class CredentialStore
{
    private const string Resource = "AMNative.AppleMusic";
    private const string DeveloperTokenName = "developer-token";
    private const string DeveloperTeamIdName = "developer-team-id";
    private const string DeveloperKeyIdName = "developer-key-id";
    private const string DeveloperPrivateKeyName = "developer-private-key";
    private const string UserTokenName = "music-user-token";
    private const string CookieName = "web-cookie";
    private readonly object _developerTokenSync = new();
    private string? _cachedDeveloperToken;
    private DateTimeOffset _cachedDeveloperTokenExpiry;

    public string? DeveloperToken
    {
        get
        {
            lock (_developerTokenSync)
            {
                var savedToken = Read(DeveloperTokenName);
                return !string.IsNullOrWhiteSpace(savedToken) ? savedToken : GetGeneratedDeveloperToken();
            }
        }
    }
    public string? DeveloperTeamId => Read(DeveloperTeamIdName);
    public string? DeveloperKeyId => Read(DeveloperKeyIdName);
    public bool HasSavedDeveloperPrivateKey => !string.IsNullOrWhiteSpace(Read(DeveloperPrivateKeyName));
    public string? AppleMusicWebCookie => ReadWebCookie();
    public string? MusicUserToken => NormalizeUserToken(Read(UserTokenName)) ?? ExtractUserToken(AppleMusicWebCookie);
    public bool HasDeveloperToken => !string.IsNullOrWhiteSpace(Read(DeveloperTokenName)) ||
                                    (!string.IsNullOrWhiteSpace(DeveloperTeamId) &&
                                     !string.IsNullOrWhiteSpace(DeveloperKeyId) &&
                                     HasSavedDeveloperPrivateKey);
    public bool HasUserCredential => !string.IsNullOrWhiteSpace(MusicUserToken);
    public bool HasWebCookie => !string.IsNullOrWhiteSpace(AppleMusicWebCookie);
    public bool HasCompleteCredentials => HasDeveloperToken && HasUserCredential;
    public bool HasAnyCredential => HasDeveloperToken || HasUserCredential || HasWebCookie;

    public void Save(
        string? developerToken,
        string? musicUserToken,
        string? webCookie,
        string? developerTeamId = null,
        string? developerKeyId = null,
        string? developerPrivateKeyPem = null)
    {
        // Blank fields mean "keep the saved value"; Forget is the explicit clear action.
        if (!string.IsNullOrWhiteSpace(developerToken) &&
            (developerToken.Length > 16384 || developerToken.Contains('\r') || developerToken.Contains('\n')))
        {
            throw new ArgumentException("The developer token is too long or contains invalid line breaks.", nameof(developerToken));
        }

        var manualDeveloperTokenSupplied = !string.IsNullOrWhiteSpace(developerToken);
        var signingInputSupplied = !string.IsNullOrWhiteSpace(developerTeamId) ||
                                   !string.IsNullOrWhiteSpace(developerKeyId) ||
                                   !string.IsNullOrWhiteSpace(developerPrivateKeyPem);
        if (manualDeveloperTokenSupplied && signingInputSupplied)
        {
            throw new ArgumentException("Use either a developer token or a .p8 signing key, not both.");
        }

        ValidateIdentifier(developerTeamId, nameof(developerTeamId), "Team ID");
        ValidateIdentifier(developerKeyId, nameof(developerKeyId), "Key ID");
        if (!string.IsNullOrWhiteSpace(developerPrivateKeyPem))
        {
            if (Encoding.UTF8.GetByteCount(developerPrivateKeyPem) > 8192)
            {
                throw new ArgumentException("The Apple Music .p8 private key file is unexpectedly large.", nameof(developerPrivateKeyPem));
            }

            DeveloperTokenSigner.ValidatePrivateKey(developerPrivateKeyPem);
        }

        string? savedTeamId = null;
        string? savedKeyId = null;
        string? savedPrivateKey = null;
        if (signingInputSupplied)
        {
            savedTeamId = string.IsNullOrWhiteSpace(developerTeamId) ? DeveloperTeamId : developerTeamId.Trim();
            savedKeyId = string.IsNullOrWhiteSpace(developerKeyId) ? DeveloperKeyId : developerKeyId.Trim();
            savedPrivateKey = string.IsNullOrWhiteSpace(developerPrivateKeyPem)
                ? Read(DeveloperPrivateKeyName)
                : developerPrivateKeyPem.Trim();
            if (string.IsNullOrWhiteSpace(savedTeamId) ||
                string.IsNullOrWhiteSpace(savedKeyId) ||
                string.IsNullOrWhiteSpace(savedPrivateKey))
            {
                throw new ArgumentException("Enter the Team ID and Key ID, and select the matching Apple Music .p8 key file.");
            }

            ValidateIdentifier(savedTeamId, nameof(developerTeamId), "Team ID");
            ValidateIdentifier(savedKeyId, nameof(developerKeyId), "Key ID");
            DeveloperTokenSigner.ValidatePrivateKey(savedPrivateKey);
        }

        var normalizedUserToken = NormalizeUserToken(musicUserToken);
        if (!string.IsNullOrWhiteSpace(musicUserToken) && normalizedUserToken is null)
        {
            throw new ArgumentException("Enter a Music User Token or a cookie header containing media-user-token.", nameof(musicUserToken));
        }

        if (!string.IsNullOrWhiteSpace(musicUserToken) &&
            (musicUserToken.Length > 16384 || musicUserToken.Contains('\r') || musicUserToken.Contains('\n')))
        {
            throw new ArgumentException("The Music User Token is too long or contains invalid line breaks.", nameof(musicUserToken));
        }

        string? cookieToken = null;
        string? savedWebCookie = null;
        if (!string.IsNullOrWhiteSpace(webCookie))
        {
            if (webCookie.Length > 16384 || Encoding.UTF8.GetByteCount(webCookie) > 16384 ||
                webCookie.Contains('\r') || webCookie.Contains('\n'))
            {
                throw new ArgumentException("The Apple Music cookie is too long or contains invalid line breaks.", nameof(webCookie));
            }

            cookieToken = ExtractUserToken(webCookie);
            if (cookieToken is null)
            {
                throw new ArgumentException("The cookie header must contain media-user-token or music-user-token.", nameof(webCookie));
            }

            savedWebCookie = NormalizeCookieHeader(webCookie);
        }

        var savedUserToken = normalizedUserToken ?? cookieToken ?? MusicUserToken;
        if (savedWebCookie is not null)
        {
            WindowsDataProtection.Write(GetWebCookiePath(), savedWebCookie);
        }

        lock (_developerTokenSync)
        {
            if (manualDeveloperTokenSupplied)
            {
                Write(DeveloperTokenName, developerToken);
                Write(DeveloperTeamIdName, null);
                Write(DeveloperKeyIdName, null);
                Write(DeveloperPrivateKeyName, null);
            }
            else if (signingInputSupplied)
            {
                Write(DeveloperTokenName, null);
                Write(DeveloperTeamIdName, savedTeamId);
                Write(DeveloperKeyIdName, savedKeyId);
                Write(DeveloperPrivateKeyName, savedPrivateKey);
            }

            _cachedDeveloperToken = null;
            _cachedDeveloperTokenExpiry = DateTimeOffset.MinValue;
        }

        if (!string.IsNullOrWhiteSpace(savedUserToken))
        {
            Write(UserTokenName, savedUserToken);
        }

        // Preserve the supplied web cookie, but use only its Music User Token for API calls.
        Write(CookieName, null);
    }

    public void Clear()
    {
        lock (_developerTokenSync)
        {
            foreach (var name in new[]
                     {
                         DeveloperTokenName,
                         DeveloperTeamIdName,
                         DeveloperKeyIdName,
                         DeveloperPrivateKeyName,
                         UserTokenName,
                         CookieName
                     })
            {
                WindowsCredentialManager.Delete(GetTargetName(name));
            }

            _cachedDeveloperToken = null;
            _cachedDeveloperTokenExpiry = DateTimeOffset.MinValue;
        }

        WindowsDataProtection.Delete(GetWebCookiePath());
    }

    private string? GetGeneratedDeveloperToken()
    {
        lock (_developerTokenSync)
        {
            var teamId = DeveloperTeamId;
            var keyId = DeveloperKeyId;
            var privateKey = Read(DeveloperPrivateKeyName);
            if (string.IsNullOrWhiteSpace(teamId) ||
                string.IsNullOrWhiteSpace(keyId) ||
                string.IsNullOrWhiteSpace(privateKey))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(_cachedDeveloperToken) &&
                _cachedDeveloperTokenExpiry > now.AddMinutes(5))
            {
                return _cachedDeveloperToken;
            }

            _cachedDeveloperToken = DeveloperTokenSigner.CreateToken(teamId, keyId, privateKey, now);
            _cachedDeveloperTokenExpiry = now.AddHours(1);
            return _cachedDeveloperToken;
        }
    }

    private static void ValidateIdentifier(string? value, string parameterName, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var identifier = value.Trim();
        if (identifier.Length > 64 || !identifier.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException($"The Apple Music {label} can contain only letters and numbers.", parameterName);
        }
    }

    private static string? Read(string name)
    {
        try
        {
            var value = WindowsCredentialManager.Read(GetTargetName(name));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void Write(string name, string? value)
    {
        var targetName = GetTargetName(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            WindowsCredentialManager.Delete(targetName);
        }
        else
        {
            WindowsCredentialManager.Write(targetName, name, value.Trim());
        }
    }

    private static string GetTargetName(string name) => $"{Resource}/{name}";

    private static string GetWebCookiePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AMNative",
        "credentials",
        "apple-music-web-cookie.bin");

    private static string? ReadWebCookie()
    {
        try
        {
            return WindowsDataProtection.Read(GetWebCookiePath());
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeCookieHeader(string cookie)
    {
        var value = cookie.Trim();
        return value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
            ? value[7..].Trim()
            : value;
    }

    private static string? NormalizeUserToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim();
        return token.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) || token.Contains(';')
            ? ExtractUserToken(token)
            : token;
    }

    private static string? ExtractUserToken(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        var value = cookie.Trim();
        if (value.Length > 16384 || value.Contains('\r') || value.Contains('\n'))
        {
            return null;
        }

        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..].Trim();
        }

        foreach (var pair in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            if ((name.Equals("media-user-token", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("music-user-token", StringComparison.OrdinalIgnoreCase)) &&
                pair[(separator + 1)..].Trim() is { Length: > 0 } token)
            {
                return token;
            }
        }

        return null;
    }

    private static class WindowsCredentialManager
    {
        private const uint GenericCredentialType = 1;
        private const uint PersistLocalMachine = 2;
        private const uint ErrorNotFound = 1168;
        private const int MaximumCredentialBlobSize = 5 * 512;

        public static string? Read(string targetName)
        {
            if (!CredReadW(targetName, GenericCredentialType, 0, out var credentialPointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    return null;
                }

                throw new Win32Exception(error);
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                {
                    return null;
                }

                if (credential.CredentialBlobSize > MaximumCredentialBlobSize)
                {
                    throw new InvalidOperationException("A saved Apple Music credential exceeds the Windows Credential Manager limit.");
                }

                var bytes = new byte[(int)credential.CredentialBlobSize];
                try
                {
                    Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            finally
            {
                CredFree(credentialPointer);
            }
        }

        public static void Write(string targetName, string userName, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var blob = IntPtr.Zero;
            try
            {
                if (bytes.Length > MaximumCredentialBlobSize)
                {
                    throw new ArgumentException("This credential exceeds the Windows Credential Manager size limit.", nameof(value));
                }

                blob = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Type = GenericCredentialType,
                    TargetName = targetName,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = userName
                };

                if (!CredWriteW(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                if (blob != IntPtr.Zero)
                {
                    for (var index = 0; index < bytes.Length; index++)
                    {
                        Marshal.WriteByte(blob, index, 0);
                    }

                    Marshal.FreeHGlobal(blob);
                }

                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public static void Delete(string targetName)
        {
            if (!CredDeleteW(targetName, GenericCredentialType, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound)
                {
                    throw new Win32Exception(error);
                }
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDeleteW(string targetName, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        }
    }

    private static class WindowsDataProtection
    {
        private const uint UiForbidden = 0x1;
        private const int MaximumProtectedFileSize = 64 * 1024;
        private static readonly object Sync = new();

        public static string? Read(string path)
        {
            lock (Sync)
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var storedLength = new FileInfo(path).Length;
                if (storedLength <= 0 || storedLength > MaximumProtectedFileSize)
                {
                    throw new InvalidDataException("The saved Apple Music web cookie file has an invalid size.");
                }

                var protectedBytes = File.ReadAllBytes(path);
                try
                {
                    if (protectedBytes.Length == 0 || protectedBytes.Length > MaximumProtectedFileSize)
                    {
                        throw new InvalidDataException("The saved Apple Music web cookie file has an invalid size.");
                    }

                    var plainBytes = Transform(protectedBytes, protect: false);
                    try
                    {
                        return Encoding.UTF8.GetString(plainBytes);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plainBytes);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
        }

        public static void Write(string path, string value)
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(path)
                    ?? throw new InvalidOperationException("The web cookie storage path is invalid.");
                Directory.CreateDirectory(directory);

                var plainBytes = Encoding.UTF8.GetBytes(value);
                byte[]? protectedBytes = null;
                var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    protectedBytes = Transform(plainBytes, protect: true);
                    if (protectedBytes.Length > MaximumProtectedFileSize)
                    {
                        throw new InvalidOperationException("The encrypted Apple Music web cookie exceeds the local storage limit.");
                    }

                    File.WriteAllBytes(temporaryPath, protectedBytes);
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                    if (protectedBytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(protectedBytes);
                    }

                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
        }

        public static void Delete(string path)
        {
            lock (Sync)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static byte[] Transform(byte[] value, bool protect)
        {
            var input = new DataBlob
            {
                Size = checked((uint)value.Length),
                Data = Marshal.AllocHGlobal(value.Length)
            };
            DataBlob output = default;
            try
            {
                Marshal.Copy(value, 0, input.Data, value.Length);
                var succeeded = protect
                    ? CryptProtectData(ref input, "AM Native Apple Music web session", IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, UiForbidden, out output);
                if (!succeeded)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (output.Size == 0 || output.Size > MaximumProtectedFileSize || output.Data == IntPtr.Zero)
                {
                    throw new InvalidDataException("Windows returned an invalid protected Apple Music web cookie.");
                }

                var result = new byte[checked((int)output.Size)];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (input.Data != IntPtr.Zero)
                {
                    for (var index = 0; index < value.Length; index++)
                    {
                        Marshal.WriteByte(input.Data, index, 0);
                    }

                    Marshal.FreeHGlobal(input.Data);
                }

                if (output.Data != IntPtr.Zero)
                {
                    var clearLength = checked((int)Math.Min(output.Size, (uint)MaximumProtectedFileSize));
                    for (var index = 0; index < clearLength; index++)
                    {
                        Marshal.WriteByte(output.Data, index, 0);
                    }

                    LocalFree(output.Data);
                }
            }
        }

        [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr optionalEntropy,
            IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy,
            IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

        [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public uint Size;
            public IntPtr Data;
        }
    }
}
