using System.Security.Cryptography;
using System.Text;

namespace BackupApp.Utils;

public static class CryptoHelper
{
    public static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public static void EncryptFileAes(string inputPath, string outputPath, string password)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var salt = RandomNumberGenerator.GetBytes(16);
        var key = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256);

        aes.Key = key.GetBytes(32);
        aes.IV = key.GetBytes(16);

        using var fsInput = File.OpenRead(inputPath);
        using var fsOutput = File.Create(outputPath);
        fsOutput.Write(salt, 0, salt.Length);

        using var cryptoStream = new CryptoStream(fsOutput, aes.CreateEncryptor(), CryptoStreamMode.Write);
        fsInput.CopyTo(cryptoStream);
    }

    public static void DecryptFileAes(string inputPath, string outputPath, string password)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var fsInput = File.OpenRead(inputPath);
        var salt = new byte[16];
        fsInput.Read(salt, 0, 16);

        var key = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256);
        aes.Key = key.GetBytes(32);
        aes.IV = key.GetBytes(16);

        using var fsOutput = File.Create(outputPath);
        using var cryptoStream = new CryptoStream(fsInput, aes.CreateDecryptor(), CryptoStreamMode.Read);
        cryptoStream.CopyTo(fsOutput);
    }

    private const string DpapiPrefix = "DPAPI|";

    public static string ProtectString(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectString(string protectedString)
    {
        if (protectedString.StartsWith(DpapiPrefix))
            protectedString = protectedString[DpapiPrefix.Length..];
        var protectedBytes = Convert.FromBase64String(protectedString);
        var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
