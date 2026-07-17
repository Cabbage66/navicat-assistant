using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NavicatTextProbe.Services;

internal static class WindowsCredentialService
{
    private const string DeepSeekTargetName = "NavicatSqlCopilot/DeepSeekApiKey";
    private const string ZhipuTargetName = "Assistant/ZhipuApiKey";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;

    public static void SaveApiKey(string apiKey) =>
        Save(DeepSeekTargetName, "DeepSeek API", apiKey);

    public static string? ReadApiKey() => Read(DeepSeekTargetName);

    public static void DeleteApiKey() => Delete(DeepSeekTargetName);

    public static void SaveZhipuApiKey(string apiKey) =>
        Save(ZhipuTargetName, "智谱 GLM API", apiKey);

    public static string? ReadZhipuApiKey() => Read(ZhipuTargetName);

    public static void DeleteZhipuApiKey() => Delete(ZhipuTargetName);

    private static void Save(string targetName, string userName, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Delete(targetName);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(apiKey.Trim());
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = userName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "保存 API Key 到 Windows 凭据管理器失败。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private static string? Read(string targetName)
    {
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168 ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static void Delete(string targetName)
    {
        if (!CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new Win32Exception(error, "删除 Windows 凭据失败。");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credentialPointer);
}
