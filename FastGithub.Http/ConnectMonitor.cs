using System;
using System.Threading;

namespace FastGithub.Http
{
    /// <summary>
    /// 连接阶段的进度快照
    /// </summary>
    /// <param name="Tracking">是否正在跟踪</param>
    /// <param name="Active">是否有正在进行的连接尝试</param>
    /// <param name="Host">目标域名</param>
    /// <param name="CurrentIp">当前正在尝试的IP</param>
    /// <param name="Attempted">已尝试的IP数量</param>
    /// <param name="ElapsedMs">本次连接已耗时(毫秒)</param>
    /// <param name="DeadlineMs">本次连接的总时限(毫秒)</param>
    /// <param name="LastError">最近一次失败的原因</param>
    public record ConnectProgress(
        bool Tracking,
        bool Active,
        string? Host,
        string? CurrentIp,
        int Attempted,
        long ElapsedMs,
        long DeadlineMs,
        string? LastError);

    /// <summary>
    /// 连接阶段的可观测性与控制
    /// 
    /// 普通用户不会上传大文件，而"状态跟踪"是每次连接尝试都要写入的(push模型)，
    /// 因此默认关闭：只有监控页面在轮询期间才启用，停止轮询一段时间后自动关闭。
    /// 端点与页面本身是pull模型，不访问就不产生任何开销。
    /// </summary>
    public sealed class ConnectMonitor
    {
        private readonly object locker = new();

        /// <summary>
        /// 跟踪状态是否被显式开启过
        /// </summary>
        private volatile bool tracking;

        /// <summary>
        /// 最后一次被轮询的时间，用于判断监控页面是否已经关闭
        /// </summary>
        private long lastPollTicks;

        /// <summary>
        /// 连接阶段的当前超时值(以Ticks保存，便于无锁读写)
        /// </summary>
        private long connectTimeoutTicks = TimeSpan.FromSeconds(30d).Ticks;

        private string? host;
        private string? currentIp;
        private int attempted;
        private long startedTicks;
        private long deadlineTicks;
        private string? lastError;
        private volatile bool active;
        private CancellationTokenSource? currentTokenSource;

        /// <summary>
        /// 监控页面停止轮询多久之后自动关闭跟踪
        /// </summary>
        private readonly TimeSpan trackingIdleTimeout = TimeSpan.FromSeconds(15d);

        /// <summary>
        /// 连接阶段的总时限
        /// 通过监控页面调整后立即生效
        /// </summary>
        public TimeSpan ConnectTimeoutTotal
        {
            get => TimeSpan.FromTicks(Volatile.Read(ref this.connectTimeoutTicks));
            set => Volatile.Write(ref this.connectTimeoutTicks, value.Ticks);
        }

        /// <summary>
        /// 是否需要记录连接状态
        /// 监控页面停止轮询超过空闲时长后自动返回false，从而恢复零开销
        /// </summary>
        public bool IsTracking
        {
            get
            {
                if (this.tracking == false)
                {
                    return false;
                }

                var idle = DateTimeOffset.UtcNow - new DateTimeOffset(Volatile.Read(ref this.lastPollTicks), TimeSpan.Zero);
                return idle < this.trackingIdleTimeout;
            }
        }

        /// <summary>
        /// 监控页面每次轮询时调用，启用并续期跟踪
        /// </summary>
        public void KeepAlive()
        {
            this.tracking = true;
            Volatile.Write(ref this.lastPollTicks, DateTimeOffset.UtcNow.UtcTicks);
        }

        /// <summary>
        /// 连接开始
        /// </summary>
        /// <param name="host"></param>
        /// <param name="timeoutTotal"></param>
        public void OnConnectStart(string host, TimeSpan timeoutTotal)
        {
            var now = DateTimeOffset.UtcNow;
            lock (this.locker)
            {
                this.host = host;
                this.currentIp = null;
                this.attempted = 0;
                this.lastError = null;
                this.startedTicks = now.UtcTicks;
                this.deadlineTicks = timeoutTotal.Ticks;
                this.active = true;
            }
        }

        /// <summary>
        /// 即将尝试某个IP
        /// </summary>
        /// <param name="ip"></param>
        public void OnTryAddress(string ip)
        {
            lock (this.locker)
            {
                this.currentIp = ip;
                this.attempted++;
            }
        }

        /// <summary>
        /// 记录一次失败原因
        /// </summary>
        /// <param name="error"></param>
        public void OnError(string error)
        {
            lock (this.locker)
            {
                this.lastError = error;
            }
        }

        /// <summary>
        /// 连接结束
        /// </summary>
        /// <param name="success"></param>
        public void OnConnectEnd(bool success)
        {
            lock (this.locker)
            {
                this.active = false;
                this.currentTokenSource = null;
                if (success == true)
                {
                    this.lastError = null;
                }
            }
        }

        /// <summary>
        /// 登记当前连接的可取消源，供页面中止使用
        /// </summary>
        /// <param name="tokenSource"></param>
        public void RegisterCancelSource(CancellationTokenSource tokenSource)
        {
            lock (this.locker)
            {
                this.currentTokenSource = tokenSource;
            }
        }

        /// <summary>
        /// 中止当前正在进行的连接
        /// </summary>
        /// <returns>是否确实中止了某个连接</returns>
        public bool CancelCurrent()
        {
            lock (this.locker)
            {
                if (this.active == false || this.currentTokenSource == null)
                {
                    return false;
                }

                this.lastError = "已被手动中止";
                try
                {
                    this.currentTokenSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                return true;
            }
        }

        /// <summary>
        /// 获取当前进度快照
        /// </summary>
        /// <returns></returns>
        public ConnectProgress GetProgress()
        {
            lock (this.locker)
            {
                var elapsed = this.active == true
                    ? DateTimeOffset.UtcNow - new DateTimeOffset(this.startedTicks, TimeSpan.Zero)
                    : TimeSpan.Zero;

                return new ConnectProgress(
                    Tracking: this.IsTracking,
                    Active: this.active,
                    Host: this.host,
                    CurrentIp: this.currentIp,
                    Attempted: this.attempted,
                    ElapsedMs: (long)elapsed.TotalMilliseconds,
                    DeadlineMs: this.deadlineTicks / TimeSpan.TicksPerMillisecond,
                    LastError: this.lastError);
            }
        }
    }
}
