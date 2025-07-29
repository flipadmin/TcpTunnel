namespace TcpTunnel.Proxy;

public enum ProxyState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    ClosedByGateway,
    Failed,
    FailedAuth
} 