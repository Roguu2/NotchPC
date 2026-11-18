using System.Runtime.InteropServices;
using System.Text;

namespace NotchPC.Platform.Security;

/// <summary>
/// Windows DPAPI (CryptProtectData), scoped to the current Windows user: other users and other PCs cannot
/// decrypt the value. Used for secrets in settings.json (calendar link). P/Invoke to avoid an extra package.
/// </summary>
public static unsafe partial class Dpapi
{
    private const uint CryptprotectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "NotchPC.v1"u8.ToArray();

    public static string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        return Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    /// <returns>The plain text, or null if the value is empty, corrupt or from another user/PC.</returns>
    public static string? TryUnprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedBase64), protect: false));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Encrypts bytes for the current Windows user.</summary>
    public static byte[] ProtectBytes(byte[] plain) => Transform(plain, protect: true);

    /// <exception cref="InvalidOperationException">Not decryptable (other user/PC or corrupt).</exception>
    public static byte[] UnprotectBytes(byte[] encrypted) => Transform(encrypted, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* pData = data)
        fixed (byte* pEntropy = Entropy)
        {
            var input = new DataBlob { Size = (uint)data.Length, Data = pData };
            var entropy = new DataBlob { Size = (uint)Entropy.Length, Data = pEntropy };
            DataBlob output = default;
            var ok = protect
                ? CryptProtectData(&input, null, &entropy, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, &output)
                : CryptUnprotectData(&input, IntPtr.Zero, &entropy, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, &output);
            if (!ok)
            {
                throw new InvalidOperationException($"DPAPI failed (error {Marshal.GetLastPInvokeError()})");
            }

            try
            {
                return new ReadOnlySpan<byte>(output.Data, (int)output.Size).ToArray();
            }
            finally
            {
                LocalFree((IntPtr)output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(DataBlob* dataIn, string? description, DataBlob* entropy, IntPtr reserved, IntPtr prompt, uint flags, DataBlob* dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(DataBlob* dataIn, IntPtr description, DataBlob* entropy, IntPtr reserved, IntPtr prompt, uint flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);
}
