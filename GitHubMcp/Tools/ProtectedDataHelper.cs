using System.Security.Cryptography;
using System.Text;

#if NET
using System.Security.Cryptography.Xml;
#endif

/// <summary>
/// Windows DPAPI 加密帮助类，用于安全存储敏感数据
/// </summary>
public static class ProtectedDataHelper
{
    /// <summary>
    /// 使用 DPAPI 加密字符串
    /// </summary>
    /// <param name="plainText">要加密的明文</param>
    /// <returns>加密后的 Base64 字符串</returns>
    public static string EncryptString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        byte[] data = Encoding.UTF8.GetBytes(plainText);
        byte[] encryptedData = System.Security.Cryptography.ProtectedData.Protect(data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedData);
    }

    /// <summary>
    /// 使用 DPAPI 解密字符串
    /// </summary>
    /// <param name="encryptedText">加密的 Base64 字符串</param>
    /// <returns>解密后的明文</returns>
    public static string DecryptString(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return string.Empty;

        try
        {
            byte[] encryptedData = Convert.FromBase64String(encryptedText);
            byte[] decryptedData = System.Security.Cryptography.ProtectedData.Unprotect(encryptedData, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedData);
        }
        catch
        {
            // 如果解密失败（可能是不同用户或机器），返回空字符串
            return string.Empty;
        }
    }
}