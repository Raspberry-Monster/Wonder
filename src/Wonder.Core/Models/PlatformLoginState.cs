namespace Wonder.Core.Models;

public enum PlatformLoginState
{
    Stopped = 0,
    Connecting = 1,
    Online = 5,
    Disconnected = 6,
    LoginFailed = 7
}