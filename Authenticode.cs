using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MarkdownViewer;

/// <summary>Code-signature checks for downloaded updates.</summary>
static class Authenticode
{
    /// <summary>
    /// If this app is signed, the update must carry a valid signature from the same publisher.
    /// An unsigned build (no certificate set up yet) relies on the checksum alone.
    /// </summary>
    public static void RequireSamePublisher(string file)
    {
        var mine = GetSigner(Environment.ProcessPath!);
        if (mine == null) return;

        var theirs = GetSigner(file);
        if (theirs == null)
            throw new InvalidDataException("The update is not signed.");
        if (theirs.Subject != mine.Subject)
            throw new InvalidDataException($"The update is signed by someone else ({theirs.GetNameInfo(X509NameType.SimpleName, false)}).");
        if (!IsTrusted(file))
            throw new InvalidDataException("The update's signature is not valid.");
    }

    static X509Certificate2? GetSigner(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057 // reads the signer of a signed file; there is no loader replacement for this
            return new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException) { return null; } // not signed
    }

    /// <summary>WinVerifyTrust: the signature is intact and chains to a trusted root.</summary>
    static bool IsTrusted(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
