using FastGithub.Configuration;
using FastGithub.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Forwarder;

namespace FastGithub.HttpServer.HttpMiddlewares
{
    /// <summary>
    /// 反向代理中间件
    /// </summary>
    sealed class HttpReverseProxyMiddleware
    {
        private static readonly DomainConfig defaultDomainConfig = new() { TlsSni = true };

        private readonly IHttpForwarder httpForwarder;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly FastGithubConfig fastGithubConfig;
        private readonly ILogger<HttpReverseProxyMiddleware> logger;

        public HttpReverseProxyMiddleware(
            IHttpForwarder httpForwarder,
            IHttpClientFactory httpClientFactory,
            FastGithubConfig fastGithubConfig,
            ILogger<HttpReverseProxyMiddleware> logger)
        {
            this.httpForwarder = httpForwarder;
            this.httpClientFactory = httpClientFactory;
            this.fastGithubConfig = fastGithubConfig;
            this.logger = logger;
        }

        /// <summary>
        /// 处理请求
        /// </summary>
        /// <param name="context"></param>
        /// <param name="next"?
        /// <returns></returns>
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var host = context.Request.Host;
            if (TryGetDomainConfig(host, out var domainConfig) == false)
            {
                await next(context);
            }
            else if (domainConfig.Response == null)
            {
                var scheme = context.Request.Scheme;
                var destinationPrefix = GetDestinationPrefix(scheme, host, domainConfig.Destination);
                var httpClient = httpClientFactory.CreateHttpClient(host.Host, domainConfig);
                var error = await httpForwarder.SendAsync(context, destinationPrefix, httpClient, ForwarderRequestConfig.Empty, HttpTransformer.Empty);
                await HandleErrorAsync(context, error);
            }
            else
            {
                context.Response.StatusCode = domainConfig.Response.StatusCode;
                context.Response.ContentType = domainConfig.Response.ContentType;
                if (domainConfig.Response.ContentValue != null)
                {
                    await context.Response.WriteAsync(domainConfig.Response.ContentValue);
                }
            }
        }

        /// <summary>
        /// 获取域名的DomainConfig
        /// </summary>
        /// <param name="host"></param>
        /// <param name="domainConfig"></param>
        /// <returns></returns>
        private bool TryGetDomainConfig(HostString host, [MaybeNullWhen(false)] out DomainConfig domainConfig)
        {
            if (fastGithubConfig.TryGetDomainConfig(host.Host, out domainConfig) == true)
            {
                return true;
            }

            // 未配置的域名，但仍然被解析到本机ip的域名
            if (OperatingSystem.IsWindows() && IsDomain(host.Host))
            {
                logger.LogWarning($"域名{host.Host}可能已经被DNS污染，如果域名为本机域名，请解析为非回环IP");
                domainConfig = defaultDomainConfig;
                return true;
            }

            return false;

            // 是否为域名
            static bool IsDomain(string host)
            {
                return IPAddress.TryParse(host, out _) == false && host.Contains('.');
            }
        }

        /// <summary>
        /// 获取目标前缀
        /// </summary>
        /// <param name="scheme"></param>
        /// <param name="host"></param>
        /// <param name="destination"></param>
        /// <returns></returns>
        private string GetDestinationPrefix(string scheme, HostString host, Uri? destination)
        {
            var defaultValue = $"{scheme}://{host}/";
            if (destination == null)
            {
                return defaultValue;
            }

            var baseUri = new Uri(defaultValue);
            var result = new Uri(baseUri, destination).ToString();
            logger.LogInformation($"{defaultValue} => {result}");
            return result;
        }

        /// <summary>
        /// 处理错误信息
        /// 使用源生成的JsonTypeInfo序列化，避免trim发布下反射元数据缺失导致二次异常
        /// </summary>
        /// <param name="context"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private static async Task HandleErrorAsync(HttpContext context, ForwarderError error)
        {
            if (error == ForwarderError.None || context.Response.HasStarted)
            {
                return;
            }

            var response = new ProxyErrorResponse(
                error.ToString(),
                context.GetForwarderErrorFeature()?.Exception?.Message);

            await context.Response.WriteAsJsonAsync(
                response,
                ProxyErrorContext.Default.ProxyErrorResponse,
                cancellationToken: context.RequestAborted);
        }
    }

    /// <summary>
    /// 反代错误的响应内容
    /// </summary>
    /// <param name="Error"></param>
    /// <param name="Message"></param>
    internal sealed record ProxyErrorResponse(string Error, string? Message);

    /// <summary>
    /// 反代错误响应内容的json序列化上下文
    /// 使用源生成而不依赖反射，以兼容trim发布
    /// </summary>
    [JsonSerializable(typeof(ProxyErrorResponse))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    internal sealed partial class ProxyErrorContext : JsonSerializerContext
    {
    }
}
