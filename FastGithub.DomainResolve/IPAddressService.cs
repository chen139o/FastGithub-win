using FastGithub.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP服务
    /// 域名IP关系缓存10分钟
    /// IPEndPoint时延缓存5分钟
    /// </summary>
    sealed class IPAddressService
    {
        /// <summary>
        /// https端口
        /// </summary>
        private const int HTTPS_PORT = 443;

        private record DomainAddress(string Domain, IPAddress Address);
        private readonly TimeSpan domainAddressExpiration = TimeSpan.FromMinutes(10d);
        private readonly IMemoryCache domainAddressCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        /// <summary>
        /// 节点测速结果
        /// BaseElapsed是纯测速时延，Penalty是累计惩罚，分开存放是为了让
        /// "真实请求成功"能够清掉惩罚而不丢失基础时延
        /// </summary>
        private record AddressElapsed(IPAddress Address, TimeSpan BaseElapsed, TimeSpan Penalty, DateTimeOffset? SuccessAt)
        {
            /// <summary>
            /// 是否在测速阶段就被判定为不可用
            /// </summary>
            public bool IsUnavailable => this.BaseElapsed == TimeSpan.MaxValue;

            /// <summary>
            /// 参与排序的时延
            /// </summary>
            public TimeSpan Elapsed => this.IsUnavailable == true ? TimeSpan.MaxValue : this.BaseElapsed + this.Penalty;
        }

        private readonly IMemoryCache addressElapsedCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        /// <summary>
        /// 本机网络异常时的结果缓存，本机问题恢复快，用较短缓存以便尽快重测
        /// </summary>
        private readonly TimeSpan problemElapsedExpiration = TimeSpan.FromMinutes(1d);

        /// <summary>
        /// 节点不可用时的结果缓存，坏节点需要较长惩罚期，避免刚压下去又立刻回到队首
        /// </summary>
        private readonly TimeSpan failedElapsedExpiration = TimeSpan.FromMinutes(10d);

        /// <summary>
        /// 正常结果的缓存
        /// </summary>
        private readonly TimeSpan normalElapsedExpiration = TimeSpan.FromMinutes(5d);

        /// <summary>
        /// tcp连接超时
        /// </summary>
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(5d);

        /// <summary>
        /// tls握手超时（在tcp连通之后单独计时）
        /// </summary>
        private readonly TimeSpan tlsHandshakeTimeout = TimeSpan.FromSeconds(5d);

        /// <summary>
        /// tcp可达但tls握手失败时叠加的惩罚
        /// 这类节点是"tcp放行、tls阻断"的诱饵，必须排到健康节点之后；
        /// 但不把它们淘汰出候选集，避免全部节点都被阻断时候选集为空、请求直接失败
        /// </summary>
        private readonly TimeSpan tlsFailedPenalty = TimeSpan.FromSeconds(30d);

        /// <summary>
        /// 真实请求连接失败时叠加的时延惩罚
        /// </summary>
        private readonly TimeSpan connectFailedPenalty = TimeSpan.FromSeconds(10d);

        /// <summary>
        /// 累计惩罚的上限，防止反复失败把时延叠加到溢出
        /// </summary>
        private readonly TimeSpan maxPenalty = TimeSpan.FromMinutes(10d);

        /// <summary>
        /// 真实请求成功后正向标记的有效期
        /// 窗口刻意取短：ip会轮换和老化，长期死用同一个节点反而有害
        /// </summary>
        private readonly TimeSpan successMarkWindow = TimeSpan.FromMinutes(2d);

        /// <summary>
        /// 正向标记带来的排序加分
        /// 只用于在时延接近的节点之间做倾向，不足以盖过明显的时延差距
        /// </summary>
        private readonly TimeSpan successMarkBonus = TimeSpan.FromMilliseconds(200d);

        private readonly DnsClient dnsClient;
        private readonly FastGithubConfig fastGithubConfig;
        private readonly ILogger<IPAddressService> logger;

        /// <summary>
        /// IP服务
        /// </summary>
        /// <param name="dnsClient"></param>
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public IPAddressService(DnsClient dnsClient, FastGithubConfig fastGithubConfig, ILogger<IPAddressService> logger)
        {
            this.dnsClient = dnsClient;
            this.fastGithubConfig = fastGithubConfig;
            this.logger = logger;
        }

        /// <summary>
        /// 并行获取可连接的IP
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="oldAddresses"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IPAddress[]> GetAddressesAsync(DnsEndPoint dnsEndPoint, IEnumerable<IPAddress> oldAddresses, CancellationToken cancellationToken)
        {
            var ipEndPoints = new HashSet<IPEndPoint>();

            // 历史未过期的IP节点
            foreach (var address in oldAddresses)
            {
                var domainAddress = new DomainAddress(dnsEndPoint.Host, address);
                if (this.domainAddressCache.TryGetValue(domainAddress, out _))
                {
                    ipEndPoints.Add(new IPEndPoint(address, dnsEndPoint.Port));
                }
            }

            // 新解析出的IP节点
            await foreach (var address in this.dnsClient.ResolveAsync(dnsEndPoint, fastSort: false, cancellationToken))
            {
                ipEndPoints.Add(new IPEndPoint(address, dnsEndPoint.Port));
                var domainAddress = new DomainAddress(dnsEndPoint.Host, address);
                this.domainAddressCache.Set(domainAddress, default(object), this.domainAddressExpiration);
            }

            if (ipEndPoints.Count == 0)
            {
                return Array.Empty<IPAddress>();
            }

            // 域名配置用于tls验证时决定SNI取值
            this.fastGithubConfig.TryGetDomainConfig(dnsEndPoint.Host, out var domainConfig);

            var addressElapsedTasks = ipEndPoints.Select(item => this.GetAddressElapsedAsync(dnsEndPoint, item, domainConfig, cancellationToken));
            var addressElapseds = await Task.WhenAll(addressElapsedTasks);
            var now = DateTimeOffset.UtcNow;

            return addressElapseds
                .Where(item => item.IsUnavailable == false)
                .OrderBy(item => this.GetScore(item, now))
                .Select(item => item.Address)
                .ToArray();
        }

        /// <summary>
        /// 计算排序分值
        /// 有过近期成功记录的节点获得小幅优先：失败有惩罚、成功也应当有奖励
        /// </summary>
        /// <param name="item"></param>
        /// <param name="now"></param>
        /// <returns></returns>
        private TimeSpan GetScore(AddressElapsed item, DateTimeOffset now)
        {
            if (item.SuccessAt.HasValue == true && now - item.SuccessAt.Value <= this.successMarkWindow)
            {
                return item.Elapsed - this.successMarkBonus;
            }
            return item.Elapsed;
        }

        /// <summary>
        /// 获取IP节点的可用性与时延
        /// 对https端口，"可用"的判据是能完成真实的tls握手，而不只是tcp连通：
        /// 被墙环境下的典型手法是放行tcp三次握手、阻断tls的clienthello，
        /// 只测tcp会把这些诱饵节点误判为最快，导致每次真实请求都白等一次超时
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="endPoint"></param>
        /// <param name="domainConfig">域名的tls配置，决定tls握手的SNI取值</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<AddressElapsed> GetAddressElapsedAsync(DnsEndPoint dnsEndPoint, IPEndPoint endPoint, DomainConfig? domainConfig, CancellationToken cancellationToken)
        {
            if (this.addressElapsedCache.TryGetValue<AddressElapsed>(endPoint, out var cached) == true)
            {
                return cached;
            }

            var useTls = dnsEndPoint.Port == HTTPS_PORT && domainConfig != null;
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var stopWatch = Stopwatch.StartNew();
            var tcpElapsed = TimeSpan.Zero;

            try
            {
                // tcp阶段
                using (var timeoutSource = new CancellationTokenSource(this.connectTimeout))
                {
                    using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
                    await socket.ConnectAsync(endPoint, linkedSource.Token);
                }
                tcpElapsed = stopWatch.Elapsed;

                if (useTls == false)
                {
                    socket.Dispose();
                    var tcpOnly = new AddressElapsed(endPoint.Address, tcpElapsed, TimeSpan.Zero, null);
                    return this.addressElapsedCache.Set(endPoint, tcpOnly, this.normalElapsedExpiration);
                }

                // tls阶段
                using (var stream = new NetworkStream(socket, ownsSocket: true))
                {
                    using var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    using (var timeoutSource = new CancellationTokenSource(this.tlsHandshakeTimeout))
                    {
                        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
                        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                        {
                            TargetHost = GetTlsSni(domainConfig!, dnsEndPoint.Host, endPoint.Address),
                            RemoteCertificateValidationCallback = static (sender, cert, chain, errors) => true
                        }, linkedSource.Token);
                    }
                }

                var available = new AddressElapsed(endPoint.Address, stopWatch.Elapsed, TimeSpan.Zero, null);
                return this.addressElapsedCache.Set(endPoint, available, this.normalElapsedExpiration);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                cancellationToken.ThrowIfCancellationRequested();

                // tcp已通但tls失败：降权但保留在候选集里作为兜底
                if (tcpElapsed > TimeSpan.Zero)
                {
                    this.logger.LogInformation(
                        $"{dnsEndPoint.Host}:{dnsEndPoint.Port} {endPoint.Address} tcp={tcpElapsed.TotalMilliseconds:0}ms可达但tls握手失败({ex.GetType().Name})，降权+{this.tlsFailedPenalty.TotalSeconds:0}s");
                    var degraded = new AddressElapsed(endPoint.Address, tcpElapsed, this.tlsFailedPenalty, null);
                    return this.addressElapsedCache.Set(endPoint, degraded, this.failedElapsedExpiration);
                }

                var expiration = IsLocalNetworkProblem(ex) ? this.problemElapsedExpiration : this.failedElapsedExpiration;
                var unavailable = new AddressElapsed(endPoint.Address, TimeSpan.MaxValue, TimeSpan.Zero, null);
                return this.addressElapsedCache.Set(endPoint, unavailable, expiration);
            }
            finally
            {
                stopWatch.Stop();
            }
        }

        /// <summary>
        /// 计算tls握手的SNI取值
        /// 必须复用域名的TlsSni配置：例如github.com配的是"不发SNI"，
        /// 硬带SNI反而会被按明文域名阻断
        /// </summary>
        /// <param name="domainConfig"></param>
        /// <param name="host"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        private static string GetTlsSni(DomainConfig domainConfig, string host, IPAddress address)
        {
            return domainConfig.GetTlsSniPattern().WithDomain(host).WithRandom().WithIPAddress(address).Value;
        }

        /// <summary>
        /// 报告节点连接失败
        /// 在基础时延之上叠加惩罚值，让它在排序中靠后但不被淘汰（可能只是瞬时抖动）
        /// </summary>
        /// <param name="endPoint"></param>
        public void ReportConnectFailed(IPEndPoint endPoint)
        {
            var current = this.addressElapsedCache.TryGetValue<AddressElapsed>(endPoint, out var cached) == true
                ? cached
                : new AddressElapsed(endPoint.Address, TimeSpan.Zero, TimeSpan.Zero, null);

            var penalty = current.Penalty + this.connectFailedPenalty;
            if (penalty > this.maxPenalty)
            {
                penalty = this.maxPenalty;
            }

            var addressElapsed = current with { Penalty = penalty };
            this.addressElapsedCache.Set(endPoint, addressElapsed, this.failedElapsedExpiration);
        }

        /// <summary>
        /// 报告节点连接成功
        /// 真实请求成功是最强的可用性证据：清掉此前累积的惩罚，并打上一个短期的排序优先标记。
        /// 若该节点此前在测速阶段被判不可用，说明测速误判，清掉结果让下一轮重新测
        /// </summary>
        /// <param name="endPoint"></param>
        public void ReportConnectSucceeded(IPEndPoint endPoint)
        {
            if (this.addressElapsedCache.TryGetValue<AddressElapsed>(endPoint, out var current) == false)
            {
                return;
            }

            if (current.IsUnavailable == true)
            {
                this.addressElapsedCache.Remove(endPoint);
                return;
            }

            var addressElapsed = current with
            {
                Penalty = TimeSpan.Zero,
                SuccessAt = DateTimeOffset.UtcNow
            };
            this.addressElapsedCache.Set(endPoint, addressElapsed, this.normalElapsedExpiration);
        }

        /// <summary>
        /// 是否为本机网络问题
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        private static bool IsLocalNetworkProblem(Exception ex)
        {
            if (ex is not SocketException socketException)
            {
                return false;
            }

            var code = socketException.SocketErrorCode;
            return code == SocketError.NetworkDown || code == SocketError.NetworkUnreachable;
        }
    }
}
