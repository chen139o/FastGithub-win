using FastGithub.Configuration;
using FastGithub.DomainResolve;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Http
{
    /// <summary>
    /// HttpClientHandler
    /// </summary> 
    class HttpClientHandler : DelegatingHandler
    {
        private readonly DomainConfig domainConfig;
        private readonly IDomainResolver domainResolver;
        private readonly ConnectMonitor monitor;
        private readonly ILogger<HttpClientHandler> logger;
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(10d);

        /// <summary>
        /// HttpClientHandler
        /// </summary>
        /// <param name="domainConfig"></param>
        /// <param name="domainResolver"></param> 
        /// <param name="monitor"></param>
        /// <param name="logger"></param>
        public HttpClientHandler(DomainConfig domainConfig, IDomainResolver domainResolver, ConnectMonitor monitor, ILogger<HttpClientHandler> logger)
        {
            this.domainConfig = domainConfig;
            this.domainResolver = domainResolver;
            this.monitor = monitor;
            this.logger = logger;
            this.InnerHandler = this.CreateSocketsHttpHandler();
        }

        /// <summary>
        /// 发送请求
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri == null)
            {
                throw new FastGithubException("必须指定请求的URI");
            }

            // 请求上下文信息
            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            var tlsSniValue = this.domainConfig.GetTlsSniPattern().WithDomain(uri.Host).WithRandom();
            request.SetRequestContext(new RequestContext(isHttps, tlsSniValue));

            // 设置请求头host，修改协议为http
            request.Headers.Host = uri.Host;
            request.RequestUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp }.Uri;

            if (this.domainConfig.Timeout != null)
            {
                using var timeoutTokenSource = new CancellationTokenSource(this.domainConfig.Timeout.Value);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                return await base.SendAsync(request, linkedTokenSource.Token);
            }
            return await base.SendAsync(request, cancellationToken);
        }

        /// <summary>
        /// 创建转发代理的httpHandler
        /// </summary>
        /// <returns></returns>
        private SocketsHttpHandler CreateSocketsHttpHandler()
        {
            return new SocketsHttpHandler
            {
                Proxy = null,
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectCallback = this.ConnectCallback
            };
        }

        /// <summary>
        /// 连接回调
        /// 
        /// 所有候选ip共享一个总时限：单个ip的尝试有connectTimeout，
        /// 但候选ip的数量没有上限，逐个串行重试会让请求长时间挂起。
        /// 该总时限可通过监控页面调整并立即生效。
        /// </summary>
        /// <param name="context"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var timeoutTotal = this.monitor.ConnectTimeoutTotal;
            using var totalTokenSource = new CancellationTokenSource(timeoutTotal);
            using var totalLinkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalTokenSource.Token);

            // 只有监控页面在轮询时才记录状态，普通用户不承担这部分开销
            var tracking = this.monitor.IsTracking;

            // 登记取消源(轻操作)，使监控页面随时可以中止本次连接
            this.monitor.RegisterCancelSource(totalTokenSource);
            if (tracking == true)
            {
                this.monitor.OnConnectStart(context.DnsEndPoint.Host, timeoutTotal);
            }

            var attempted = 0;
            var innerExceptions = new List<Exception>();
            var ipEndPoints = this.GetIPEndPointsAsync(context.DnsEndPoint, totalLinkedSource.Token);
            try
            {
                await foreach (var ipEndPoint in ipEndPoints)
                {
                    attempted++;
                    if (tracking == true)
                    {
                        this.monitor.OnTryAddress(ipEndPoint.Address.ToString());
                    }

                    try
                    {
                        using var timeoutTokenSource = new CancellationTokenSource(this.connectTimeout);
                        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(totalLinkedSource.Token, timeoutTokenSource.Token);
                        var stream = await this.ConnectAsync(context, ipEndPoint, linkedTokenSource.Token);
                        this.domainResolver.ReportConnectSucceeded(ipEndPoint);
                        this.monitor.OnConnectEnd(true);
                        return stream;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
                    {
                        innerExceptions.Add(new HttpConnectTimeoutException(ipEndPoint.Address));
                        if (tracking == true)
                        {
                            this.monitor.OnError($"{ipEndPoint.Address} 连接超时");
                        }

                        // 只有该ip自己的connectTimeout到期才降权；
                        // 若是总预算耗尽(或被人为中止)，这个ip可能只是排在后面没轮到，不应惩罚
                        if (totalTokenSource.IsCancellationRequested == true)
                        {
                            this.logger.LogWarning(
                                $"{context.DnsEndPoint.Host}:{context.DnsEndPoint.Port} 连接总时限{timeoutTotal.TotalSeconds:0}秒耗尽，已尝试{attempted}个IP后放弃");
                            break;
                        }
                        this.domainResolver.ReportConnectFailed(ipEndPoint);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        innerExceptions.Add(ex);
                        if (tracking == true)
                        {
                            this.monitor.OnError($"{ipEndPoint.Address} {ex.Message}");
                        }
                        this.domainResolver.ReportConnectFailed(ipEndPoint);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
            {
                // 总时限已耗尽（发生在等待下一个候选IP时）
                this.logger.LogWarning(
                    $"{context.DnsEndPoint.Host}:{context.DnsEndPoint.Port} 连接总时限{timeoutTotal.TotalSeconds:0}秒耗尽，已尝试{attempted}个IP后放弃");
            }

            this.monitor.OnConnectEnd(false);
            cancellationToken.ThrowIfCancellationRequested();

            // 记录失败明细，用于区分"候选ip没试完"和"所有候选ip都失败"
            if (innerExceptions.Count > 0)
            {
                var summary = string.Join(" | ", innerExceptions.Take(6).Select(item => item.Message));
                this.logger.LogWarning(
                    $"{context.DnsEndPoint.Host}:{context.DnsEndPoint.Port} 连接失败，共尝试{attempted}个IP，异常明细：{summary}");
            }

            throw new AggregateException($"连接{context.DnsEndPoint.Host}失败：{timeoutTotal.TotalSeconds:0}秒内没有找到可成功连接的IP", innerExceptions);
        }

        /// <summary>
        /// 建立连接
        /// </summary>
        /// <param name="context"></param>
        /// <param name="ipEndPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, IPEndPoint ipEndPoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(ipEndPoint, cancellationToken);
            }
            catch (Exception)
            {
                socket.Dispose();
                throw;
            }

            var stream = new NetworkStream(socket, ownsSocket: true);

            var requestContext = context.InitialRequestMessage.GetRequestContext();
            if (requestContext.IsHttps == false)
            {
                return stream;
            }

            var tlsSniValue = requestContext.TlsSniValue.WithIPAddress(ipEndPoint.Address);
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = tlsSniValue.Value,
                    RemoteCertificateValidationCallback = ValidateServerCertificate
                }, cancellationToken);
            }
            catch (Exception)
            {
                // SslStream释放时会一并释放内部的NetworkStream与Socket
                sslStream.Dispose();
                throw;
            }

            return sslStream;

            // 验证证书有效性
            bool ValidateServerCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
            {
                if (errors != SslPolicyErrors.None)
                {
                    // 记录校验失败的细节：errors的具体值决定了处理方式，
                    // NameMismatch可以靠TlsIgnoreNameMismatch兜底，其它错误则一票否决
                    var dnsNames = string.Join(",", ReadDnsNames(cert));
                    this.logger.LogWarning(
                        $"{context.DnsEndPoint.Host}({ipEndPoint.Address}) 证书校验失败：errors={errors}，Subject={cert?.Subject}，SAN=[{dnsNames}]");
                }

                if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                {
                    if (this.domainConfig.TlsIgnoreNameMismatch == true)
                    {
                        return true;
                    }

                    var domain = context.DnsEndPoint.Host;
                    var dnsNames = ReadDnsNames(cert);
                    return dnsNames.Any(dns => IsMatch(dns, domain));
                }

                return errors == SslPolicyErrors.None;
            }
        }

        /// <summary>
        /// 解析为IPEndPoint
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async IAsyncEnumerable<IPEndPoint> GetIPEndPointsAsync(DnsEndPoint dnsEndPoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(dnsEndPoint.Host, out var address))
            {
                yield return new IPEndPoint(address, dnsEndPoint.Port);
            }
            else
            {
                if (this.domainConfig.IPAddress != null)
                {
                    yield return new IPEndPoint(this.domainConfig.IPAddress, dnsEndPoint.Port);
                }

                await foreach (var item in this.domainResolver.ResolveAsync(dnsEndPoint, cancellationToken))
                {
                    yield return new IPEndPoint(item, dnsEndPoint.Port);
                }
            }
        }

        /// <summary>
        /// 读取使用的DNS名称
        /// </summary>
        /// <param name="cert"></param>
        /// <returns></returns>
        private static IEnumerable<string> ReadDnsNames(X509Certificate? cert)
        {
            if (cert is X509Certificate2 x509)
            {
                var extension = x509.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                if (extension != null)
                {
                    return extension.EnumerateDnsNames();
                }
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// 比较域名
        /// </summary>
        /// <param name="dnsName"></param>
        /// <param name="domain"></param>
        /// <returns></returns>
        private static bool IsMatch(string dnsName, string? domain)
        {
            if (domain == null)
            {
                return false;
            }
            if (dnsName == domain)
            {
                return true;
            }
            if (dnsName[0] == '*')
            {
                return domain.EndsWith(dnsName[1..]);
            }
            return false;
        }
    }
}
