namespace scpm;

public class AcceptorConfig
    : acfg.Config
{
    public int Port { get; set; } = 4684;
    public bool SecureChannel { get; set; } = true;
}
