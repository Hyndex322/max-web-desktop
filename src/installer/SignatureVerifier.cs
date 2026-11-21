using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MaxSetup
{
    /// <summary>
    /// Проверка Authenticode-подписи встроенного загрузчика WebView2.
    /// </summary>
    internal static class SignatureVerifier
    {
        private static readonly Guid GenericVerifyV2 =
            new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
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

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            ref WinTrustData data);

        public static bool VerifyMicrosoftFile(string path, out string error)
        {
            error = null;

            try
            {
                if (!System.IO.File.Exists(path))
                {
                    error = "file not found";
                    return false;
                }

                string publisher;
                if (!VerifyAuthenticode(path, out publisher, out error))
                    return false;

                if (string.IsNullOrEmpty(publisher) ||
                    publisher.IndexOf("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    error = "publisher is not Microsoft Corporation";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8");
                return false;
            }
        }

        private static bool VerifyAuthenticode(string path, out string publisher, out string error)
        {
            publisher = null;
            error = null;

            try
            {
                using (var certificate = new X509Certificate2(
                    X509Certificate.CreateFromSignedFile(path)))
                {
                    publisher = certificate.Subject;
                }
            }
            catch (Exception ex)
            {
                error = "no signer certificate 0x" + ex.HResult.ToString("X8");
                return false;
            }

            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };

            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = fileInfoPtr,
                    dwStateAction = WtdStateActionVerify,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = IntPtr.Zero,
                    dwProvFlags = WtdCacheOnlyUrlRetrieval,
                    dwUIContext = 0,
                    pSignatureSettings = IntPtr.Zero
                };

                uint result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);

                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);

                if (result != 0)
                {
                    error = "WinVerifyTrust 0x" + result.ToString("X8");
                    return false;
                }

                return true;
            }
            finally
            {
                Marshal.DestroyStructure(fileInfoPtr, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(fileInfoPtr);
            }
        }
    }
}
