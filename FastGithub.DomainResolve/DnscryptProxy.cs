using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static PInvoke.AdvApi32;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// DnscryptProxy服务
    /// </summary>
    sealed class DnscryptProxy
    {
        private readonly ILogger<DnscryptProxy> logger;
        private readonly string processName;
        private readonly string serviceName;
        private readonly string exeFilePath;
        private readonly string tomlFilePath;

        /// <summary>
        /// 相关进程
        /// </summary>
        private Process? process;

        /// <summary>
        /// 承载dnscrypt-proxy的Job
        /// 保证父进程被强杀时，子进程也会被内核连带结束
        /// </summary>
        private ProcessJob? processJob;

        /// <summary>
        /// 获取监听的节点
        /// </summary>
        public IPEndPoint? LocalEndPoint { get; private set; }

        /// <summary>
        /// DnscryptProxy服务
        /// </summary>
        /// <param name="logger"></param>
        public DnscryptProxy(ILogger<DnscryptProxy> logger)
        {
            const string PATH = "dnscrypt-proxy";
            const string NAME = "dnscrypt-proxy";

            this.logger = logger;
            this.processName = NAME;
            this.serviceName = $"{nameof(FastGithub)}.{NAME}";
            this.exeFilePath = Path.Combine(PATH, OperatingSystem.IsWindows() ? $"{NAME}.exe" : NAME);
            this.tomlFilePath = Path.Combine(PATH, $"{NAME}.toml");
        }

        /// <summary>
        /// 启动dnscrypt-proxy
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await this.StartCoreAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, $"{this.processName}启动失败，将只使用FallbackDns解析域名");
            }
        }

        /// <summary>
        /// 启动dnscrypt-proxy
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            var port = GlobalListener.GetAvailablePort(5533);
            var localEndPoint = new IPEndPoint(IPAddress.Loopback, port);

            await TomlUtil.SetListensAsync(this.tomlFilePath, localEndPoint, cancellationToken);
            await TomlUtil.SetLogLevelAsync(this.tomlFilePath, 6, cancellationToken);
            await TomlUtil.SetLBStrategyAsync(this.tomlFilePath, "ph", cancellationToken);
            await TomlUtil.SetMinMaxTTLAsync(this.tomlFilePath, TimeSpan.FromMinutes(1d), TimeSpan.FromMinutes(2d), cancellationToken);

            // 启动之前先清掉上一次可能残留的进程：
            // Stop()只负责结束自己启动的那个，若上次是被强杀退出的，
            // 它的子进程会一直残留，下一次启动必须在这里兜住
            this.KillExistingProcesses();

            if (OperatingSystem.IsWindows() && Environment.UserInteractive == false)
            {
                ServiceInstallUtil.StopAndDeleteService(this.serviceName);
                ServiceInstallUtil.InstallAndStartService(this.serviceName, this.exeFilePath, ServiceStartType.SERVICE_DEMAND_START);
                this.process = Process.GetProcessesByName(this.processName).FirstOrDefault(item => item.SessionId == 0);
            }
            else
            {
                this.process = StartDnscryptProxy();

                // 加入Job：父进程无论以何种方式退出(优雅关闭/强杀/崩溃)，
                // 内核都会结束Job内的进程
                this.processJob ??= ProcessJob.TryCreate();
                if (this.process != null && this.processJob != null)
                {
                    this.processJob.TryAssign(this.process);
                }
            }

            if (this.process != null)
            {
                this.LocalEndPoint = localEndPoint;
                this.process.EnableRaisingEvents = true;
                this.process.Exited += (s, e) => this.LocalEndPoint = null;
            }
            else
            {
                this.logger.LogError($"{this.processName}进程未能启动，将只使用FallbackDns解析域名（以服务方式运行时需要管理员权限）");
            }
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        public void Stop()
        {
            try
            {
                if (OperatingSystem.IsWindows() && Environment.UserInteractive == false)
                {
                    ServiceInstallUtil.StopAndDeleteService(this.serviceName);
                }

                // 按进程名清理，同时覆盖"本次启动的"和"历史残留的"
                this.KillExistingProcesses();
                this.process = null;

                // 关闭Job句柄同样会结束Job内的进程，作为最后一道保险
                this.processJob?.Dispose();
                this.processJob = null;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"{this.processName}停止失败：{ex.Message }");
            }
            finally
            {
                this.LocalEndPoint = null;
            }
        }

        /// <summary>
        /// 结束所有同名进程
        /// </summary>
        private void KillExistingProcesses()
        {
            foreach (var item in Process.GetProcessesByName(this.processName))
            {
                try
                {
                    item.Kill();
                    item.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning($"清理已存在的{this.processName}(pid={item.Id})失败：{ex.Message}");
                }
                finally
                {
                    item.Dispose();
                }
            }
        }

        /// <summary>
        /// 启动DnscryptProxy进程
        /// </summary> 
        /// <returns></returns>
        private Process? StartDnscryptProxy()
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = this.exeFilePath,
                WorkingDirectory = Path.GetDirectoryName(this.exeFilePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        /// <summary>
        /// 转换为字符串
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return this.processName;
        }
    }
}
