namespace SingBoxClient.Core.Platform;

public interface IPlatformService
{
    void SetSystemProxy(string host, int port);
    void ClearSystemProxy();
    void SetAutoStart(bool enable, string exePath);
    bool GetAutoStart(string appName);
    string GetSystemLanguage();
    bool IsAdmin();
    string GetAppDirectory();
}
