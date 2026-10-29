namespace Koi.Api.Auth;

/// <summary>系统账号认证抽象，便于后续替换实现或补充测试替身。</summary>
public interface IAuthenticator
{
    /// <summary>校验系统账号与密码。</summary>
    AuthenticationResult Authenticate(string userName, string password);
}
