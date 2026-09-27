using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiWeb_Launcher.Models
{
    /// <summary>打开 Web 端的方式(或打开主界面/无动作)。</summary>
    public enum WebOpenAction
    {
        None = 0,
        WebView = 1,
        Browser = 2,
        MainWindow = 3,
    }

    /// <summary>
    /// “重复启动应用时”这一项在设置页下拉框里的选项顺序,以及它与 <see cref="WebOpenAction"/> 值的互转。
    /// <para>
    /// 为什么需要这张表:下拉框按使用习惯排(无动作 → 打开主界面 → WebView → 浏览器),
    /// 与枚举值顺序(None=0 / WebView=1 / Browser=2 / MainWindow=3,托盘那两项正是靠它直接按索引映射)**不同**。
    /// 若把映射留在界面里,顺序与设置值的对应关系就只存在于 XAML 与事件处理器的对照中,改一处会静默错位;
    /// 放在这里既被单元测试盯住,界面也只剩两行转调。
    /// </para>
    /// </summary>
    public static class RepeatLaunchOptions
    {
        /// <summary>默认动作:打开主界面(加入本设置之前的行为),也是识别不出旧值时回退到的项。</summary>
        public const WebOpenAction DefaultAction = WebOpenAction.MainWindow;

        /// <summary>下拉框选项顺序:索引即界面顺序,必须与设置页 ComboBoxItem 的书写顺序一致。</summary>
        private static readonly WebOpenAction[] Items =
        {
            WebOpenAction.None,
            WebOpenAction.MainWindow,
            WebOpenAction.WebView,
            WebOpenAction.Browser,
        };

        /// <summary>下拉框的选项顺序(界面顺序)。</summary>
        public static System.Collections.Generic.IReadOnlyList<WebOpenAction> Order => Items;

        /// <summary>动作 → 下拉框索引;识别不出的值(手改设置文件写出越界值)回退到 <see cref="DefaultAction"/> 的项。</summary>
        public static int ToIndex(WebOpenAction action)
        {
            var index = Array.IndexOf(Items, action);
            return index >= 0 ? index : Array.IndexOf(Items, DefaultAction);
        }

        /// <summary>下拉框索引 → 动作;越界索引(理论上不会出现)回退到 <see cref="DefaultAction"/>。</summary>
        public static WebOpenAction FromIndex(int index)
            => index >= 0 && index < Items.Length ? Items[index] : DefaultAction;
    }

    /// <summary>
    /// 在应用内 WebView 里点击链接(页面请求新窗口/新标签,如 target="_blank"、window.open)时的处理方式。
    /// 枚举顺序与设置页下拉框的索引一一对应,不要随意调换。
    /// </summary>
    public enum WebViewLinkTarget
    {
        /// <summary>接管并由系统默认浏览器打开(默认)。</summary>
        SystemBrowser = 0,

        /// <summary>不接管,完全交给 WebView2 底层默认行为。</summary>
        AppWebView = 1,
    }

    /// <summary>
    /// pi 包目录的列表排序方式。取值与 https://pi.dev/packages 的 <c>sort</c> 查询参数一一对应
    /// (目录页的“Sort packages”下拉框:Most downloads / Recently published / A-Z)。
    /// <para>
    /// 与 Pi 启动器不同,这里的排序**不在本地做**:目录有 5000+ 条,启动器一次只拉若干页,
    /// 本地排序只能排出“已拉到的那几页”。所以排序值直接透传给目录站点,由它排好再分页。
    /// 枚举按**名字**解析(见 <see cref="LenientEnumConverter{T}"/>),所以增删成员不会读错已有设置。
    /// </para>
    /// </summary>
    public enum PackageCatalogSort
    {
        /// <summary>按月下载量降序(目录默认)。</summary>
        Downloads = 0,

        /// <summary>按发布时间降序(新发布的在前)。</summary>
        RecentlyPublished = 1,

        /// <summary>按包名字母序。</summary>
        Name = 2,
    }

    /// <summary>
    /// pi 包目录的**类型**筛选项,取值与目录页的 <c>type</c> 查询参数一一对应
    /// (All types / extension / skill / theme / prompt)。
    /// <para>
    /// 目录把没有声明任何已知资源类型的包标成 <c>package</c>,但筛选下拉框里**没有**这一项
    /// (选“All types” 才能看到它们),所以这里也没有对应成员。
    /// </para>
    /// </summary>
    public enum PackageCatalogType
    {
        /// <summary>不筛选(空 type 参数)。</summary>
        All = 0,

        /// <summary>扩展(可执行代码,能注册工具与命令)。</summary>
        Extension = 1,

        /// <summary>技能(给模型看的说明文档)。</summary>
        Skill = 2,

        /// <summary>主题(TUI 配色)。</summary>
        Theme = 3,

        /// <summary>提示词模板。</summary>
        Prompt = 4,
    }

    /// <summary>
    /// npm 源(registry)。设置页下拉框的索引与枚举值一一对应。
    /// <list type="bullet">
    /// <item><see cref="Config"/> = **不注入任何环境变量**,沿用用户自己的 .npmrc / 环境变量
    /// (即“使用配置源”,默认)。</item>
    /// <item>其余成员 = 已知的公共镜像,启动器会把地址注入子进程的 <c>npm_config_registry</c>,
    /// npm 与 pnpm 都认这个变量(等价于 <c>--registry</c>,对 <c>pi install</c> 内部转发的
    /// npm/pnpm/bun 也生效)。</item>
    /// </list>
    /// 地址映射见 <c>ChildEnvironment.ResolveRegistry</c>。
    /// 枚举按**名字**解析(见 <see cref="LenientEnumConverter{T}"/>),所以增删成员不会读错已有设置。
    /// </summary>
    public enum NpmRegistrySource
    {
        /// <summary>使用本机配置(.npmrc / 环境变量),不覆盖 registry,默认。</summary>
        Config = 0,

        /// <summary>npm 官方源(registry.npmjs.org)。</summary>
        NpmOfficial = 1,

        /// <summary>npmmirror(原淘宝源)。</summary>
        Npmmirror = 2,

        /// <summary>腾讯云 npm 镜像。</summary>
        TencentCloud = 3,

        /// <summary>华为云 npm 镜像。</summary>
        HuaweiCloud = 4,
    }

    /// <summary>应用设置(持久化为 JSON)。</summary>
    public sealed class AppSettings
    {
        /// <summary>单击托盘图标的动作,默认在 WebView 中打开。</summary>
        [JsonConverter(typeof(LenientEnumConverter<WebOpenAction>))]
        public WebOpenAction TraySingleClick { get; set; } = WebOpenAction.WebView;

        /// <summary>双击托盘图标的动作,默认在浏览器中打开。</summary>
        [JsonConverter(typeof(LenientEnumConverter<WebOpenAction>))]
        public WebOpenAction TrayDoubleClick { get; set; } = WebOpenAction.Browser;

        /// <summary>应用启动时自动运行 Pi Web 服务(仅在已安装时生效),默认关闭。</summary>
        public bool RunServiceOnStartup { get; set; }

        /// <summary>
        /// 登录系统后自动启动启动器(开机自启动),默认关闭。
        /// <para>
        /// 这是**用户意图**,系统侧的注册项才是实际生效的东西;两者的一致性由
        /// <c>AutoStartService.Reconcile</c> 在每次启动时对齐(设置开着但注册项被删/指向旧路径会补写,
        /// 设置关着但注册项残留会清除)。属性名带 OnLogon 是因为 Windows 的 Run 项与 macOS 的
        /// LaunchAgent 都在**登录**时触发,而不是开机时。
        /// </para>
        /// </summary>
        public bool AutoStartOnLogon { get; set; }

        /// <summary>Pi Web 服务启动并检测到 Web 地址后的动作(设置页仅提供 None/WebView/Browser),默认无动作。</summary>
        [JsonConverter(typeof(LenientEnumConverter<WebOpenAction>))]
        public WebOpenAction AfterServiceStarted { get; set; } = WebOpenAction.None;

        /// <summary>
        /// 重复启动应用(已有实例在运行时又启动了一次)时,已有实例要做的动作。
        /// 默认打开主界面 —— 与加入本设置之前的行为一致(早期版本无条件显示主界面)。
        /// 选 WebView/Browser 但服务未运行(拿不到 Web 地址)时回退为打开主界面,
        /// 判定与托盘动作同一套(见 <c>App.OpenByAction</c>)。
        /// </summary>
        [JsonConverter(typeof(LenientEnumConverter<WebOpenAction>))]
        public WebOpenAction RepeatLaunchAction { get; set; } = RepeatLaunchOptions.DefaultAction;

        /// <summary>应用启动时打开主界面;关闭时启动到系统托盘,默认打开。</summary>
        public bool ShowMainWindowOnStartup { get; set; } = true;

        /// <summary>
        /// Pi Web 服务监听端口。0 = 不指定,沿用 pi-web 默认端口(30141),对应启动参数 --port。
        /// </summary>
        public int ListenPort { get; set; }

        /// <summary>
        /// 服务监听的主机名,空 = 沿用 pi-web 默认(127.0.0.1),对应启动参数 --hostname。
        /// <para>
        /// ⚠ 填非回环地址会把一个能执行高权限操作、可读写本机文件的 Agent 暴露到网络上。
        /// 界面上的提示与 README 里的说明都基于这一条,不要在别处弱化它。
        /// </para>
        /// </summary>
        public string ListenHostname { get; set; } = string.Empty;

        /// <summary>
        /// Pi Web 的浏览器登录密码,空 = 不启用认证,对应子进程环境变量 <c>PI_WEB_PASSWORD</c>。
        /// <para>
        /// ⚠ 明文存在设置文件里(与 npm 的 token 一样属于本机用户凭据,文件权限即边界)。
        /// 仅在监听非回环地址时才有意义 —— 只监听 127.0.0.1 时开启它只是多一次登录。
        /// </para>
        /// </summary>
        public string WebPassword { get; set; } = string.Empty;

        /// <summary>
        /// 由启动器自己拉起 pi-web 时,是否仍然让它打开系统浏览器,默认关闭。
        /// 关闭 = 传 <c>--no-open</c>:界面上有「打开 WebView / 浏览器」按钮,不该再自己弹一个浏览器窗口。
        /// </summary>
        public bool LetServiceOpenBrowser { get; set; }

        /// <summary>在应用内 WebView 中点击链接(页面请求新窗口)时的打开方式,默认交给系统浏览器。</summary>
        [JsonConverter(typeof(LenientEnumConverter<WebViewLinkTarget>))]
        public WebViewLinkTarget WebViewLink { get; set; } = WebViewLinkTarget.SystemBrowser;

        /// <summary>
        /// 关闭 WebView 窗口时是否保留(仅隐藏)窗口。开启则下次打开是毫秒级复用,
        /// 但会持续占用 WebView2 进程内存;关闭则立即释放、下次需重新加载。默认保留。
        /// </summary>
        public bool KeepWebViewAlive { get; set; } = true;

        /// <summary>
        /// 隐藏后保留 WebView 窗口的时长(分钟),超过则自动关闭并释放 WebView2 内存。
        /// 0 或超出范围 = 使用默认值 <see cref="DefaultWebViewIdleTimeoutMinutes"/> 分钟。
        /// 仅在 <see cref="KeepWebViewAlive"/> 开启时有效。
        /// </summary>
        public int WebViewIdleTimeoutMinutes { get; set; }

        /// <summary>pi 包目录的排序方式,默认按下载量。</summary>
        [JsonConverter(typeof(LenientEnumConverter<PackageCatalogSort>))]
        public PackageCatalogSort PackageCatalogSort { get; set; } = PackageCatalogSort.Downloads;

        /// <summary>pi 包目录的类型筛选,默认不筛选。</summary>
        [JsonConverter(typeof(LenientEnumConverter<PackageCatalogType>))]
        public PackageCatalogType PackageCatalogType { get; set; } = PackageCatalogType.All;

        /// <summary>npm 源,默认“使用配置源”(不覆盖本机 .npmrc 里的 registry)。</summary>
        [JsonConverter(typeof(LenientEnumConverter<NpmRegistrySource>))]
        public NpmRegistrySource NpmRegistry { get; set; } = NpmRegistrySource.Config;

        /// <summary>
        /// 代理地址(如 <c>http://127.0.0.1:7890</c>),空 = 不使用代理。
        /// 会注入 npm/pnpm 等子进程的 HTTP_PROXY/HTTPS_PROXY 与 npm_config_proxy,
        /// 也用于 pi 包目录下载的 HTTP 客户端。改写前先经 <c>ChildEnvironment.NormalizeProxyUrl</c> 归一化。
        /// </summary>
        public string ProxyUrl { get; set; } = string.Empty;

        /// <summary>
        /// 不走代理的地址(逗号分隔的主机名或域名后缀,如 <c>localhost,127.0.0.1,.corp.com</c>),
        /// 仅在使用代理时生效。对应 NO_PROXY / npm_config_noproxy。
        /// </summary>
        public string NoProxy { get; set; } = string.Empty;

        /// <summary>“保留超时”的默认值(分钟)。</summary>
        public const int DefaultWebViewIdleTimeoutMinutes = 5;

        /// <summary>“保留超时”允许的最小值(分钟)。</summary>
        public const int MinWebViewIdleTimeoutMinutes = 1;

        /// <summary>“保留超时”允许的最大值(分钟,24 小时)。</summary>
        public const int MaxWebViewIdleTimeoutMinutes = 1440;
    }

    /// <summary>
    /// 宽容的字符串枚举转换器:无法识别的名称(例如设置文件里存着已被删除的枚举值)
    /// 回退为 <c>default</c>(即第一个成员,约定为各枚举的默认值),而不是抛异常。
    ///
    /// 必要性:属性上直接用 <see cref="JsonStringEnumConverter"/> 时,
    /// 遇到未知名称会抛 <c>JsonException</c>,而 <c>SettingsService.Load()</c> 是整体回退,
    /// 结果是**所有设置静默重置为默认**(已实测)。故从枚举中删值属于破坏性变更,必须靠宽容解析兜住。
    /// </summary>
    internal sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<T>(reader.GetString(), out var fromName))
            {
                return fromName;
            }

            if (reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out var number)
                && Enum.IsDefined(typeof(T), number))
            {
                return (T)Enum.ToObject(typeof(T), number);
            }

            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
