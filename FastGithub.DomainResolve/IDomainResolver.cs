using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary>
    public interface IDomainResolver
    { 
        /// <summary>
        /// 解析所有ip
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, CancellationToken cancellationToken = default);

        /// <summary>
        /// 对所有节点进行测速
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task TestSpeedAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 报告节点连接失败
        /// 用于降低该节点的优先级，避免后续请求继续优先使用它
        /// </summary>
        /// <param name="endPoint">连接失败的节点</param>
        void ReportConnectFailed(IPEndPoint endPoint);

        /// <summary>
        /// 报告节点连接成功
        /// 真实请求成功是最强的可用性证据，用于给该节点一个短期的排序优先
        /// </summary>
        /// <param name="endPoint">连接成功的节点</param>
        void ReportConnectSucceeded(IPEndPoint endPoint);
    }
}