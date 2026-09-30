using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.PacketIntercept.Dns
{
    /// <summary>
    /// host文件冲突解决者
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class HostsConflictSolver : IDnsConflictSolver
    {
        /// <summary>
        /// 注释命中域名时使用的专属标记
        /// 只有带该标记的行才会被恢复，不会误伤用户自己写的注释
        /// </summary>
        private const string MARK = "#FastGithub# ";

        private static readonly string hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers/etc/hosts");

        private readonly FastGithubConfig fastGithubConfig;
        private readonly ILogger<HostsConflictSolver> logger;

        /// <summary>
        /// host文件冲突解决者
        /// </summary>
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public HostsConflictSolver(
            FastGithubConfig fastGithubConfig,
            ILogger<HostsConflictSolver> logger)
        {
            this.fastGithubConfig = fastGithubConfig;
            this.logger = logger;
        }

        /// <summary>
        /// 解决冲突
        /// 给命中域名的行加上专属标记注释掉
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task SolveAsync(CancellationToken cancellationToken)
        {
            if (File.Exists(hostsPath) == false)
            {
                return;
            }

            var hosts = await ReadAsync(cancellationToken);
            var hasConflicting = false;
            var lines = new List<string>(hosts.Lines.Length);

            foreach (var line in hosts.Lines)
            {
                if (this.IsConflictingLine(line) == true)
                {
                    hasConflicting = true;
                    lines.Add($"{MARK}{line}");
                }
                else
                {
                    lines.Add(line);
                }
            }

            if (hasConflicting == true)
            {
                await this.WriteAsync(lines, hosts);
            }
        }

        /// <summary>
        /// 恢复冲突
        /// 只还原本程序标记过的行，用户自己的注释与内容保持不变
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task RestoreAsync(CancellationToken cancellationToken)
        {
            if (File.Exists(hostsPath) == false)
            {
                return;
            }

            var hosts = await ReadAsync(cancellationToken);
            var hasMarked = false;
            var lines = new List<string>(hosts.Lines.Length);

            foreach (var line in hosts.Lines)
            {
                if (line.StartsWith(MARK, StringComparison.Ordinal) == true)
                {
                    hasMarked = true;
                    lines.Add(line[MARK.Length..]);
                }
                else
                {
                    lines.Add(line);
                }
            }

            if (hasMarked == true)
            {
                await this.WriteAsync(lines, hosts);
            }
        }

        /// <summary>
        /// 读取hosts内容
        /// 保留原文件的换行风格
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<HostsFile> ReadAsync(CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(hostsPath, cancellationToken);
            var encoding = DetectEncoding(bytes);
            var preambleLength = encoding.GetPreamble().Length;
            var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);

            var newLine = text.Contains("\r\n") == true ? "\r\n" : "\n";
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].TrimEnd('\r');
            }

            return new HostsFile(lines, encoding, newLine);
        }

        /// <summary>
        /// 写回hosts内容
        /// 先写同目录临时文件再原子替换，并且写阶段不接受取消：
        /// 避免停机超时时取消写入，把hosts截断成半截文件
        /// </summary>
        /// <param name="lines"></param>
        /// <param name="hosts"></param>
        /// <returns></returns>
        private async Task WriteAsync(List<string> lines, HostsFile hosts)
        {
            var tempPath = $"{hostsPath}.fastgithub.tmp";
            try
            {
                var preamble = hosts.Encoding.GetPreamble();
                var body = hosts.Encoding.GetBytes(string.Join(hosts.NewLine, lines));
                var bytes = new byte[preamble.Length + body.Length];
                Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
                Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);

                await File.WriteAllBytesAsync(tempPath, bytes, CancellationToken.None);
                File.Move(tempPath, hostsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "写入hosts文件失败，hosts内容保持不变");
                TryDelete(tempPath);
            }
        }

        /// <summary>
        /// 探测hosts文件的编码
        /// 只依据BOM判断；BOM缺失时使用Latin1做字节级无损往返，
        /// 避免猜错为UTF-8而损坏GBK等编码的中文注释
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        private static Encoding DetectEncoding(byte[] bytes)
        {
            var span = bytes.AsSpan();
            if (span.StartsWith(Encoding.UTF8.GetPreamble()) == true)
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            }
            if (span.StartsWith(Encoding.Unicode.GetPreamble()) == true)
            {
                return Encoding.Unicode;
            }
            if (span.StartsWith(Encoding.BigEndianUnicode.GetPreamble()) == true)
            {
                return Encoding.BigEndianUnicode;
            }
            return Encoding.Latin1;
        }

        /// <summary>
        /// 删除临时文件
        /// </summary>
        /// <param name="path"></param>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path) == true)
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 是否为冲突的行
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private bool IsConflictingLine(string? line)
        {
            if (line == null || line.TrimStart().StartsWith("#"))
            {
                return false;
            }

            var items = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 2)
            {
                return false;
            }

            var domain = items[1];
            return this.fastGithubConfig.IsMatch(domain);
        }

        /// <summary>
        /// hosts文件内容
        /// </summary>
        /// <param name="Lines">按行拆分后的内容，不含换行符</param>
        /// <param name="Encoding">原文件编码</param>
        /// <param name="NewLine">原文件换行符</param>
        private sealed record HostsFile(string[] Lines, Encoding Encoding, string NewLine);
    }
}
