namespace BackupApp.Interfaces;

public interface IWebMessageBridge
{
    void SendToWeb(string message);
    event EventHandler<string> MessageReceived;
}
