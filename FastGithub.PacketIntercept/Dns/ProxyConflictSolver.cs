using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.PacketIntercept.Dns
{
    /// <summary>
    /// 代理冲突解决者
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class ProxyConflictSolver : IDnsConflictSolver
    {
        private const int INTERNET_OPTION_REFRESH = 37;
        private const int INTERNET_OPTION_PROXY_SETTINGS_CHANGED = 95;

        private const char PROXYOVERRIDE_SEPARATOR = ';';
        private const string PROXYOVERRIDE_KEY = "ProxyOverride";
        private const string INTERNET_SETTINGS = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private readonly IOptions<FastGithubOptions> options;
        private readonly ILogger<ProxyConflictSolver> logger;

        /// <summary>
        /// 记录本次运行由FastGithub新增的ProxyOverride项
        /// 恢复时只移除这些项，不触碰用户自己写的同名项
        /// </summary>
        private readonly HashSet<string> addedItems = new(StringComparer.OrdinalIgnoreCase);

        [DllImport("wininet.dll")]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);


        /// <summary>
        /// 代理冲突解决者
        /// </summary>
        /// <param name="options"></param>
        /// <param name="logger"></param>
        public ProxyConflictSolver(
            IOptions<FastGithubOptions> options,
            ILogger<ProxyConflictSolver> logger)
        {
            this.options = options;
            this.logger = logger;
        }

        /// <summary>
        /// 解决冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task SolveAsync(CancellationToken cancellationToken)
        {
            try
            {
                this.SetToProxyOvride();
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "设置ProxyOverride失败");
            }

            this.CheckProxyConflict();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 恢复冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task RestoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                this.RemoveFromProxyOvride();
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "恢复ProxyOverride失败");
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 添加到ProxyOvride
        /// 保持用户原有项的书写顺序与大小写，只追加缺失的项
        /// </summary>
        private void SetToProxyOvride()
        {
            using var settings = Registry.CurrentUser.OpenSubKey(INTERNET_SETTINGS, writable: true);
            if (settings == null)
            {
                this.logger.LogWarning("无法打开Internet Settings注册表项，ProxyOverride未设置");
                return;
            }

            var existing = GetProxyOvride(settings);
            var items = new List<string>(existing);
            var itemSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

            this.addedItems.Clear();
            foreach (var domain in this.options.Value.DomainConfigs.Keys)
            {
                if (itemSet.Add(domain) == true)
                {
                    items.Add(domain);
                    this.addedItems.Add(domain);
                }
            }

            if (this.addedItems.Count > 0)
            {
                SetProxyOvride(settings, items);
            }
        }

        /// <summary>
        /// 从ProxyOvride移除
        /// 只移除本次运行由本程序新增的项，用户原有的项一律保留
        /// </summary>
        private void RemoveFromProxyOvride()
        {
            if (this.addedItems.Count == 0)
            {
                return;
            }

            using var settings = Registry.CurrentUser.OpenSubKey(INTERNET_SETTINGS, writable: true);
            if (settings == null)
            {
                this.logger.LogWarning("无法打开Internet Settings注册表项，ProxyOverride未恢复");
                return;
            }

            var items = GetProxyOvride(settings)
                .Where(item => this.addedItems.Contains(item) == false)
                .ToList();

            this.addedItems.Clear();
            SetProxyOvride(settings, items);
        }

        /// <summary>
        /// 检测代理冲突
        /// </summary>
        private void CheckProxyConflict()
        {
            var systemProxy = HttpClient.DefaultProxy;
            if (systemProxy == null)
            {
                return;
            }

            foreach (var domain in this.options.Value.DomainConfigs.Keys)
            {
                var destination = new Uri($"https://{domain.Replace('*', 'a')}");
                var proxyServer = systemProxy.GetProxy(destination);
                if (proxyServer != null)
                {
                    this.logger.LogError($"由于系统设置了代理{proxyServer}，{nameof(FastGithub)}无法加速{domain}");
                }
            }
        }

        /// <summary>
        /// 获取ProxyOverride
        /// </summary>
        /// <param name="registryKey"></param>
        /// <returns></returns>
        private static string[] GetProxyOvride(RegistryKey registryKey)
        {
            var value = registryKey.GetValue(PROXYOVERRIDE_KEY, null)?.ToString();
            if (value == null)
            {
                return Array.Empty<string>();
            }

            return value
                .Split(PROXYOVERRIDE_SEPARATOR, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .ToArray();
        }

        /// <summary>
        /// 设置ProxyOverride
        /// </summary>
        /// <param name="registryKey"></param>
        /// <param name="items"></param>
        private static void SetProxyOvride(RegistryKey registryKey, IEnumerable<string> items)
        {
            var value = string.Join(PROXYOVERRIDE_SEPARATOR, items);
            if (string.IsNullOrEmpty(value) == true)
            {
                // 没有内容时删除该值，避免在注册表里留下一个空字符串
                registryKey.DeleteValue(PROXYOVERRIDE_KEY, throwOnMissingValue: false);
            }
            else
            {
                registryKey.SetValue(PROXYOVERRIDE_KEY, value, RegistryValueKind.String);
            }

            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PROXY_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }
}
