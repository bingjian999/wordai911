using System;
using System.Security.Cryptography;
using System.Threading;
using WordAI.Agent.Gateway;
using WordAI.Agent.Pi;
using WordAI.Addin.Ui;
using WordAI.Addin.Word;

namespace WordAI.Addin
{
    /// <summary>
    /// VSTO 入口：组装四层架构的宿主侧（见 docs/technical-plan.md 第四章）。
    ///
    /// 启动顺序：
    ///  1. 捕获 Word 主 STA 线程的 SynchronizationContext —— 所有 COM 调用与
    ///     UI 呈现都必须 marshal 回主线程；
    ///  2. 每次会话生成随机 token（网关只认本会话 token，经环境变量注入
    ///     Pi 子进程，再由 TS 扩展携带连接）；
    ///  3. 启动通道 B 网关（127.0.0.1:47611，JSON Lines）；
    ///  4. Pi 子进程运行时（通道 A）随安装包部署后启用（见下方 TODO）。
    /// </summary>
    public partial class ThisAddIn
    {
        /// <summary>通道 B 网关固定监听端口（与 docs/protocol-b.md 一致）。</summary>
        internal const int GatewayPort = 47611;

        /// <summary>当前插件实例（Ribbon / 诊断用）。</summary>
        internal static ThisAddIn Instance { get; private set; }

        private SynchronizationContext _ui;
        private WordGatewayServer _gateway;
        private AgentRuntimeHost _piRuntime;
        private string _token;

        /// <summary>通道 B 网关是否运行中。</summary>
        internal bool GatewayRunning => _gateway != null;

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            Instance = this;
            _ui = SynchronizationContext.Current; // Word 主 STA 线程

            // 1. 会话 token：32 字节随机数；仅本会话内经环境变量下发给 Pi 子进程
            _token = NewSessionToken();

            // 2. 通道 B 网关：TS 扩展经 TCP 直达（IWordHost 的 COM 实现 + UI 桥）
            var host = new WordHostCom(
                () => Application,
                () => Application == null ? null : Application.ActiveDocument,
                _ui);
            var uiBridge = new WinFormsUiBridge(_ui);

            try
            {
                _gateway = WordGatewayServer.Start(
                    GatewayPort,
                    _token,
                    host,
                    serverVersion: "wordai-addin/0.4.0",
                    log: s => System.Diagnostics.Debug.WriteLine(s));
            }
            catch (Exception ex)
            {
                // 端口被占用（如另一个 Word 实例已起网关）等启动失败：记日志不拖垮 Word
                System.Diagnostics.Debug.WriteLine("[wordai] gateway start failed: " + ex.Message);
            }

            // 3. 通道 A：Pi 子进程运行时。运行时目录（pi-runtime.json + node_modules）
            //    随安装器部署，路径从注册表/配置读取。骨架阶段不自动启动，接线如下：
            //
            //    string runtimeDir = LoadRuntimeDirFromConfig();          // e.g. %ProgramData%\wordai911\pi
            //    var lockInfo = PiRuntimeLock.Load(runtimeDir);          // 完整性校验（sha256）
            //    _piRuntime = new AgentRuntimeHost(new AgentRuntimeHost.Options
            //    {
            //        NodePath = "node",
            //        RuntimeDir = runtimeDir,
            //    }, log: s => System.Diagnostics.Debug.WriteLine(s));
            //    _piRuntime.OnFatal += ex => System.Diagnostics.Debug.WriteLine("[wordai] pi fatal: " + ex);
            //    // 启动前把网关坐标经环境变量注入子进程（扩展用它连通道 B）
            //    //   WORD_GATEWAY_PORT=47611  WORD_GATEWAY_TOKEN=<本会话 token>
            //    await _piRuntime.StartAsync(CancellationToken.None);
            //
            // 注意：uiBridge 供 AgentRuntimeHost 在收到 extension_ui_request（通道 C）时调用。
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            try
            {
                _piRuntime?.Dispose();
            }
            finally
            {
                _gateway?.Dispose();
                _gateway = null;
            }
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        /// <summary>生成 32 字节随机 token（hex）。</summary>
        internal static string NewSessionToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        #region VSTO 生成代码

        private void InternalStartup()
        {
            this.Startup += new EventHandler(ThisAddIn_Startup);
            this.Shutdown += new EventHandler(ThisAddIn_Shutdown);
        }

        #endregion
    }
}
