using System.Runtime.InteropServices;

namespace RailReader2.Services;

/// <summary>The reader's name as the operating system knows it, offered (never applied silently) as the
/// annotation author: <see cref="MainWindowViewModel"/>'s one-time "sign your notes as …?" prompt and the
/// Settings name box's suggestion. Local only: reads the user account, never the network.
/// <list type="bullet">
/// <item>Linux: the full-name (GECOS) field of the account (<c>getpwuid</c>, so LDAP/sssd accounts work too).</item>
/// <item>Windows: the display name (<c>GetUserNameEx(NameDisplay)</c>), which a domain or local account
/// with a full name has; a Microsoft-account sign-in often has none.</item>
/// <item>Otherwise the login name, as Acrobat does.</item>
/// </list></summary>
public static class AccountNameDetector
{
    static readonly Lazy<string?> s_name = new(DetectUncached);

    /// <summary>The detected name, or null. Cached for the process.</summary>
    public static string? Detect() => s_name.Value;

    static string? DetectUncached()
    {
        string? full = null;
        try
        {
            if (OperatingSystem.IsLinux()) full = FromGecos(LinuxGecos());
            else if (OperatingSystem.IsWindows()) full = Clean(WindowsDisplayName());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
        }
        return full ?? Clean(SafeUserName());
    }

    /// <summary>The name in a GECOS field: its first comma-separated part ("Jane Doe,Room 4,,").</summary>
    public static string? FromGecos(string? gecos)
        => gecos is null ? null : Clean(gecos.Split(',')[0]);

    /// <summary>Trimmed, or null when blank or not a name (no letters, a placeholder).</summary>
    public static string? Clean(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || !name.Any(char.IsLetter)) return null;
        return name.ToLowerInvariant() is "root" or "user" or "admin" or "administrator" ? null : name;
    }

    static string? SafeUserName()
    {
        try { return Environment.UserName; }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return null; }
    }

    // ---- Linux

    [DllImport("libc")]
    static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    static extern IntPtr getpwuid(uint uid);

    // glibc's struct passwd on 64-bit: pw_name, pw_passwd (pointers), pw_uid, pw_gid (uint32 each), pw_gecos.
    const int GecosOffset64 = 24;

    static string? LinuxGecos()
    {
        if (IntPtr.Size != 8) return null;
        IntPtr pw = getpwuid(geteuid());
        if (pw == IntPtr.Zero) return null;
        IntPtr gecos = Marshal.ReadIntPtr(pw, GecosOffset64);
        return gecos == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(gecos);
    }

    // ---- Windows

    const int NameDisplay = 3;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserNameExW(int nameFormat, [Out] char[]? buffer, ref uint size);

    static string? WindowsDisplayName()
    {
        uint size = 0;
        GetUserNameExW(NameDisplay, null, ref size); // ERROR_MORE_DATA with the size, or failure (none mapped)
        if (size == 0 || size > 1024) return null;
        var buffer = new char[size];
        if (!GetUserNameExW(NameDisplay, buffer, ref size)) return null;
        return new string(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
    }
}
