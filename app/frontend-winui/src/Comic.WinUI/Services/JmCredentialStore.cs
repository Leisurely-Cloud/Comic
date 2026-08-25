using System;
using System.Linq;
using Windows.Security.Credentials;

namespace Comic.WinUI.Services;

public interface IJmCredentialStore
{
    bool HasCredential { get; }
    bool TrySave(string username, string password);
    bool TryLoad(out string username, out string password);
    void Clear();
}

/// <summary>
/// 使用 Windows Credential Locker 保存 JM 凭据。凭据由当前 Windows 用户加密保护，
/// 不写入应用配置、日志或普通文件。
/// </summary>
public sealed class WindowsJmCredentialStore : IJmCredentialStore
{
    private const string ResourceName = "Comic.WinUI.JmAccount";

    public bool HasCredential
    {
        get
        {
            try { return new PasswordVault().RetrieveAll().Any(item => item.Resource == ResourceName); }
            catch { return false; }
        }
    }

    public bool TrySave(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;
        try
        {
            Clear();
            new PasswordVault().Add(new PasswordCredential(ResourceName, username.Trim(), password));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryLoad(out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;
        try
        {
            var credential = new PasswordVault().RetrieveAll().FirstOrDefault(item => item.Resource == ResourceName);
            if (credential is null) return false;
            credential.RetrievePassword();
            username = credential.UserName;
            password = credential.Password;
            return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
        }
        catch
        {
            username = string.Empty;
            password = string.Empty;
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var credential in vault.RetrieveAll().Where(item => item.Resource == ResourceName).ToList())
                vault.Remove(credential);
        }
        catch
        {
            // 凭据不存在或系统凭据库暂不可用时，无需影响退出登录。
        }
    }
}

internal sealed class VolatileJmCredentialStore : IJmCredentialStore
{
    public bool HasCredential => false;
    public bool TrySave(string username, string password) => false;
    public bool TryLoad(out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;
        return false;
    }
    public void Clear() { }
}
