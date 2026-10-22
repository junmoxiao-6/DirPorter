using System.Runtime.InteropServices;

namespace DirPorter.Core.FileSystem;

/// <summary>advapi32 原生调用声明（权限探测、注册表只读查询）。</summary>
internal static partial class Advapi32
{
    public const uint TokenQuery = 0x0008;
    public const uint TokenAdjustPrivileges = 0x0020;

    public const int TokenPrivileges = 3;
    public const int TokenElevation = 20;

    /// <summary>
    /// TOKEN_INFORMATION_CLASS.TokenElevationType。
    /// 返回 TOKEN_ELEVATION_TYPE：1=Default（UAC 关闭或标准用户）、2=Full（已提权）、3=Limited（拆分令牌，可提权）。
    /// 用它判断"当前用户能否通过 UAC 提权"—— 这是 1223(ERROR_CANCELLED) 之外唯一可靠的判据。
    /// </summary>
    public const int TokenElevationType = 18;

    public const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>HKEY_LOCAL_MACHINE。预定义句柄值为 0x80000002（32 位值，在 64 位进程中按符号扩展）。</summary>
    public static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));

    /// <summary>HKEY_CURRENT_USER：0x80000001。</summary>
    public static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));

    /// <summary>KEY_READ（含 KEY_WOW64_64KEY 之外的常规读取权限）与 KEY_WOW64_32KEY / KEY_WOW64_64KEY。</summary>
    public const uint KeyRead = 0x00020019;
    public const uint KeyWow6464 = 0x0100;
    public const uint KeyWow6432 = 0x0200;
    public const uint RegSz = 1;
    public const uint RegExpandSz = 2;

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumKeyExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int RegEnumKeyEx(
        IntPtr hKey, uint dwIndex, char* lpName, ref uint lpcchName,
        IntPtr lpReserved, char* lpClass, uint* lpcchClass, IntPtr lpftLastWriteTime);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int RegEnumValue(
        IntPtr hKey, uint dwIndex, char* lpValueName, ref uint lpcchValueName,
        IntPtr lpReserved, out uint lpType, byte* lpData, uint* lpcbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int RegQueryValueEx(
        IntPtr hKey, string? lpValueName, IntPtr lpReserved, out uint lpType, byte* lpData, ref uint lpcbData);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int RegCloseKey(IntPtr hKey);

    public const uint RrfRtRegDword = 0x00000010;
    public const uint RrfRtRegSz = 0x00000002;

    /// <summary>SeCreateSymbolicLinkPrivilege 名称（创建符号链接所需特权）。</summary>
    public const string SeCreateSymbolicLink = "SeCreateSymbolicLinkPrivilege";

    /// <summary>开发者模式注册表值：AllowDevelopmentWithoutDevLicense（DWORD）。</summary>
    public const string AppModelUnlockKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
    public const string AllowDevelopmentWithoutDevLicense = "AllowDevelopmentWithoutDevLicense";

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? lpSystemName, string lpName, out Luid lpLuid);

    /// <summary>
    /// 启用/禁用令牌中的特权。
    ///
    /// ⚠️ 关键现实：管理员令牌里 <c>SeRestorePrivilege</c> / <c>SeSecurityPrivilege</c>
    /// 默认是 **disabled** 状态。持有 ≠ 已启用，设置所有者/SACL 前必须显式启用，
    /// 否则 <c>SetNamedSecurityInfo</c> 会返回 <c>ERROR_PRIVILEGE_NOT_HELD(1314)</c>。
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        IntPtr newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    /// <summary>TOKEN_PRIVILEGES（单项）。与常量 TokenPrivileges(=3) 区分命名。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SingleTokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegGetValue(
        IntPtr hkey, string? lpSubKey, string? lpValue, uint dwFlags, out uint pdwType, IntPtr pvData, ref uint pcbData);

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;

        public readonly bool Equals(Luid other) => LowPart == other.LowPart && HighPart == other.HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    // ============================ 安全描述符搬运（复制引擎 B）============================

    /// <summary>SE_OBJECT_TYPE.SE_FILE_OBJECT。</summary>
    public const int SeFileObject = 1;

    // SECURITY_INFORMATION 位
    public const uint OwnerSecurityInformation = 0x00000001;
    public const uint GroupSecurityInformation = 0x00000002;
    public const uint DaclSecurityInformation = 0x00000004;
    public const uint SaclSecurityInformation = 0x00000008;
    public const uint ProtectedDaclSecurityInformation = 0x80000000;

    /// <summary>PROTECTED_SACL_SECURITY_INFORMATION：让目标 SACL 同样禁止继承。</summary>
    public const uint ProtectedSaclSecurityInformation = 0x10000000;

    /// <summary>SeSecurityPrivilege（读写 SACL 审计信息）。</summary>
    public const string SeSecurity = "SeSecurityPrivilege";

    /// <summary>SeRestorePrivilege（写入"不属于自己"的所有者）。</summary>
    public const string SeRestore = "SeRestorePrivilege";

    /// <summary>
    /// 读取对象的安全描述符。返回的 <paramref name="ppSecurityDescriptor"/> 必须由调用方用
    /// <c>LocalFree</c> 释放；其中的 SID/ACL 指针指向该缓冲区内部，不能单独释放。
    /// </summary>
    [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetNamedSecurityInfo(
        string pObjectName,
        int objectType,
        uint securityInfo,
        out IntPtr ppsidOwner,
        out IntPtr ppsidGroup,
        out IntPtr ppDacl,
        out IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);

    /// <summary>
    /// 写入对象的安全描述符。
    /// 权限要求：DACL 需要目标对象的 WRITE_DAC；**OWNER/GROUP 需要 SeRestorePrivilege**；
    /// **SACL 需要 SeSecurityPrivilege**。因此非提权时只能成功写 DACL —— 这正是能力降级的边界。
    /// </summary>
    [DllImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int SetNamedSecurityInfo(
        string pObjectName,
        int objectType,
        uint securityInfo,
        IntPtr psidOwner,
        IntPtr psidGroup,
        IntPtr pDacl,
        IntPtr pSacl);
}
