using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AmNative.Services;

public sealed class CredentialStore
{
    private const string Resource = "AMNative.AppleMusic";
    private const string DeveloperTokenName = "developer-token";
    private const string UserTokenName = "music-user-token";
    private const string CookieName = "web-cookie";

    public string? DeveloperToken => Read(DeveloperTokenName);
    public string? MusicUserToken => NormalizeUserToken(Read(UserTokenName)) ?? ExtractUserToken(Read(CookieName));
    public bool HasDeveloperToken => !string.IsNullOrWhiteSpace(DeveloperToken);
    public bool HasUserCredential => !string.IsNullOrWhiteSpace(MusicUserToken);
    public bool HasCompleteCredentials => HasDeveloperToken && HasUserCredential;
    public bool HasAnyCredential => !string.IsNullOrWhiteSpace(DeveloperToken) ||
                                    !string.IsNullOrWhiteSpace(MusicUserToken);

    public void Save(string? developerToken, string? musicUserToken, string? webCookie)
    {
        // Blank fields mean "keep the saved value"; Forget is the explicit clear action.
        if (!string.IsNullOrWhiteSpace(developerToken) &&
            (developerToken.Length > 16384 || developerToken.Contains('\r') || developerToken.Contains('\n')))
        {
            throw new ArgumentException("The developer token is too long or contains invalid line breaks.", nameof(developerToken));
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
        if (!string.IsNullOrWhiteSpace(webCookie))
        {
            if (webCookie.Length > 16384 || webCookie.Contains('\r') || webCookie.Contains('\n'))
            {
                throw new ArgumentException("The Apple Music cookie is too long or contains invalid line breaks.", nameof(webCookie));
            }

            cookieToken = ExtractUserToken(webCookie);
            if (cookieToken is null)
            {
                throw new ArgumentException("The cookie header must contain media-user-token or music-user-token.", nameof(webCookie));
            }
        }

        var savedUserToken = normalizedUserToken ?? cookieToken ?? MusicUserToken;
        if (!string.IsNullOrWhiteSpace(developerToken)) Write(DeveloperTokenName, developerToken);
        if (!string.IsNullOrWhiteSpace(savedUserToken))
        {
            Write(UserTokenName, savedUserToken);
        }

        // Keep only the API's Music User Token; the full cookie header is discarded.
        Write(CookieName, null);
    }

    public void Clear()
    {
        foreach (var name in new[] { DeveloperTokenName, UserTokenName, CookieName })
        {
            WindowsCredentialManager.Delete(GetTargetName(name));
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
}
