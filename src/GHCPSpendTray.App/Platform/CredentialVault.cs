using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GHCPSpendTray.App.Platform;

/// <summary>Stores opaque, account-specific payloads in Windows Credential Manager.</summary>
public sealed partial class CredentialVault
{
    private const uint Generic = 1;
    private const int NotFound = 1168;
    private const int MaximumBlobBytes = 2560;

    public unsafe string? Read(string target)
    {
        ValidateTarget(target);
        if (!CredRead(target, Generic, 0, out var credential))
        {
            var error = Marshal.GetLastPInvokeError();
            credential?.Dispose();
            if (error == NotFound)
                return null;
            throw new Win32Exception(error, "Could not read the GHCPSpendTray credential.");
        }

        using (credential)
        {
            var native = (NativeCredential*)credential.DangerousGetHandle();
            if (native->CredentialBlobSize > MaximumBlobBytes || native->CredentialBlobSize % 2 != 0 ||
                (native->CredentialBlobSize != 0 && native->CredentialBlob == 0))
                throw new InvalidDataException("The saved GHCPSpendTray credential payload is invalid.");
            var blob = new Span<byte>((void*)native->CredentialBlob, checked((int)native->CredentialBlobSize));
            try { return Encoding.Unicode.GetString(blob); }
            finally { CryptographicOperations.ZeroMemory(blob); }
        }
    }

    public unsafe void Write(string target, string payload)
    {
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(payload);
        if (Encoding.Unicode.GetByteCount(payload) > MaximumBlobBytes)
            throw new ArgumentException("Credential payload exceeds the Windows Credential Manager limit.", nameof(payload));
        var blob = Encoding.Unicode.GetBytes(payload);
        try
        {
            fixed (char* targetPointer = target)
            fixed (byte* blobPointer = blob)
            {
                var credential = new NativeCredential
                {
                    Type = Generic,
                    TargetName = (nint)targetPointer,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (nint)blobPointer,
                    Persist = 2 // CRED_PERSIST_LOCAL_MACHINE: current user, subsequent logons.
                };
                if (!CredWrite(in credential, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not save the GHCPSpendTray credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    public void Delete(string target)
    {
        ValidateTarget(target);
        if (!CredDelete(target, Generic, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != NotFound)
                throw new Win32Exception(error, "Could not delete the GHCPSpendTray credential.");
        }
    }

    private static void ValidateTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!target.StartsWith("GHCPSpendTray/", StringComparison.Ordinal) || target.Length > 32767 || target.Contains('\0'))
            throw new ArgumentException("Credential targets must start with GHCPSpendTray/ and contain no null characters.", nameof(target));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    private sealed class CredentialHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CredentialHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            CredFree(handle);
            return true;
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out CredentialHandle credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in NativeCredential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint credential);
}
