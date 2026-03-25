namespace TeamsEchoBot.Models;

public class BotConfiguration
{
    public string AadAppId { get; set; } = string.Empty;
    public string AadAppSecret { get; set; } = string.Empty;
    public string AadTenantId { get; set; } = string.Empty;
    public string BotName { get; set; } = string.Empty;
    public string ServiceDnsName { get; set; } = string.Empty;
    public string CallbackUri { get; set; } = string.Empty;
    public int MediaPort { get; set; }
    public string CertThumbprint { get; set; } = string.Empty;
}

public class SpeechConfiguration
{
    public string Key { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string VoiceName { get; set; } = string.Empty;
}

public class JoinCallRequest
{
    public string JoinUrl { get; set; } = string.Empty;
}