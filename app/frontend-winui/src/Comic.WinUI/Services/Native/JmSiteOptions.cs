namespace Comic.WinUI.Services.Native;

/// <summary>
/// 禁漫天堂站点协议配置。站点改版(域名、密钥、签名规则)时只需调整此处，
/// 无需改动业务代码。
/// </summary>
public sealed class JmSiteOptions
{
    public static JmSiteOptions Default { get; } = new();

    public string AppTokenSecret { get; init; } = "18comicAPP";

    public string AppTokenSecretContent { get; init; } = "18comicAPPContent";

    public string AppDataSecret { get; init; } = "185Hcomic3PAPP7R";

    public string AppVersion { get; init; } = "2.0.13";

    public string PublicSiteDomain { get; init; } = "18comic.vip";

    public string CoverDomain { get; init; } = "cdn-msp3.18comic.vip";

    public string ImageDomain { get; init; } = "cdn-msp2.jmapiproxy2.cc";

    public int DefaultScrambleId { get; init; } = 220_980;

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    public string[] ApiDomains { get; init; } =
    [
        "www.cdnhth.cc",
        "www.cdnhth.net",
        "www.cdnbea.net",
        "www.cdnzack.cc",
        "www.cdn-mspjmapiproxy.xyz",
    ];
}
