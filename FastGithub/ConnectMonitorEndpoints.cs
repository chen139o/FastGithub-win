using FastGithub.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FastGithub
{
    /// <summary>
    /// 连接监控与超时控制的HTTP端点
    /// 
    /// 这些端点是pull模型：不访问就不产生任何开销。
    /// 状态跟踪则由 /connectProgress 的轮询自动启用，停止轮询后自动关闭。
    /// </summary>
    static class ConnectMonitorEndpoints
    {
        /// <summary>
        /// 注册连接监控相关端点
        /// </summary>
        /// <param name="app"></param>
        public static void MapConnectMonitor(this WebApplication app)
        {
            app.MapGet("/connect", context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                return context.Response.WriteAsync(Html);
            });

            app.MapGet("/connectProgress", context =>
            {
                var monitor = context.RequestServices.GetRequiredService<ConnectMonitor>();
                monitor.KeepAlive();
                var json = JsonSerializer.Serialize(monitor.GetProgress(), ConnectMonitorContext.Default.ConnectProgress);
                return context.Response.WriteAsync(json);
            });

            app.MapPost("/setConnectTimeout", context =>
            {
                var monitor = context.RequestServices.GetRequiredService<ConnectMonitor>();
                var text = context.Request.Query["seconds"].ToString();
                if (int.TryParse(text, out var seconds) == true && seconds >= 5 && seconds <= 3600)
                {
                    monitor.ConnectTimeoutTotal = TimeSpan.FromSeconds(seconds);
                }
                return context.Response.WriteAsync(monitor.ConnectTimeoutTotal.TotalSeconds.ToString("0"));
            });

            app.MapPost("/cancelConnect", context =>
            {
                var monitor = context.RequestServices.GetRequiredService<ConnectMonitor>();
                return context.Response.WriteAsync(monitor.CancelCurrent() == true ? "cancelled" : "idle");
            });
        }

        /// <summary>
        /// 监控页面
        /// </summary>
        private const string Html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>FastGithub 连接监控</title>
<style>
  body { font-family: "Segoe UI", "Microsoft YaHei", sans-serif; background:#f5f7fa; color:#1f2937; margin:0; padding:24px; }
  .card { background:#fff; border:1px solid #dbe4ee; border-radius:10px; padding:18px 20px; max-width:640px; margin:0 auto 16px; }
  h1 { font-size:16px; font-weight:600; margin:0 0 14px; color:#1d4ed8; }
  table { border-collapse:collapse; width:100%; font-size:13px; }
  td { padding:6px 0; vertical-align:top; }
  td.k { color:#6b7280; width:130px; }
  td.v { font-family:Consolas, monospace; word-break:break-all; }
  .dot { display:inline-block; width:8px; height:8px; border-radius:50%; background:#9ca3af; margin-right:6px; }
  .on { background:#22c55e; } .off { background:#cbd5e1; }
  button { font-size:13px; padding:7px 16px; border-radius:6px; border:1px solid #1d4ed8; background:#1d4ed8; color:#fff; cursor:pointer; }
  button.ghost { background:#fff; color:#1d4ed8; }
  button:disabled { opacity:.45; cursor:not-allowed; }
  input { font-size:13px; padding:6px 10px; border:1px solid #dbe4ee; border-radius:6px; width:90px; }
  .hint { font-size:12px; color:#6b7280; margin-top:10px; line-height:1.6; }
  .err { color:#b91c1c; }
</style>
</head>
<body>
<div class="card">
  <h1>连接状态</h1>
  <table>
    <tr><td class="k">跟踪</td><td class="v"><span id="dot" class="dot"></span><span id="tracking">-</span></td></tr>
    <tr><td class="k">目标域名</td><td class="v" id="host">-</td></tr>
    <tr><td class="k">当前尝试IP</td><td class="v" id="ip">-</td></tr>
    <tr><td class="k">已尝试个数</td><td class="v" id="attempted">-</td></tr>
    <tr><td class="k">已用时长</td><td class="v" id="elapsed">-</td></tr>
    <tr><td class="k">本次总时限</td><td class="v" id="deadline">-</td></tr>
    <tr><td class="k">最近错误</td><td class="v err" id="error">-</td></tr>
  </table>
  <div class="hint">页面关闭后轮询停止，跟踪会自动关闭，不影响日常使用。</div>
</div>

<div class="card">
  <h1>连接超时设置</h1>
  <input id="seconds" type="number" min="5" max="3600" step="5"> 秒
  <button onclick="saveTimeout()">保存</button>
  <button class="ghost" onclick="cancelConnect()">中止当前连接</button>
  <div class="hint">全局生效、立即应用，默认 30 秒。网络不佳时可以调大（如 180 秒），让每个候选 IP 有更充分的尝试时间。范围 5 ~ 3600。</div>
</div>

<script>
let trackingStarted = false;
function fmt(ms) {
  if (!ms) return '-';
  return (ms / 1000).toFixed(1) + ' 秒';
}
async function poll() {
  try {
    const r = await fetch('/connectProgress', { cache: 'no-store' });
    const d = await r.json();
    document.getElementById('host').textContent = d.host || '-';
    document.getElementById('ip').textContent = d.currentIp || '-';
    document.getElementById('attempted').textContent = d.attempted || 0;
    document.getElementById('elapsed').textContent = d.active ? fmt(d.elapsedMs) : '-';
    document.getElementById('deadline').textContent = fmt(d.deadlineMs);
    document.getElementById('error').textContent = d.lastError || '-';
    document.getElementById('tracking').textContent = d.tracking ? (d.active ? '跟踪中（连接进行中）' : '跟踪中（空闲）') : '未启用';
    document.getElementById('dot').className = 'dot ' + (d.tracking ? 'on' : 'off');
    if (!trackingStarted) {
      trackingStarted = true;
      document.getElementById('seconds').value = Math.round(d.deadlineMs / 1000);
    }
  } catch (e) {
  }
}
async function saveTimeout() {
  const s = document.getElementById('seconds').value;
  const r = await fetch('/setConnectTimeout?seconds=' + encodeURIComponent(s), { method: 'POST' });
  const v = await r.text();
  alert('已保存：' + v + ' 秒（立即生效）');
}
async function cancelConnect() {
  const r = await fetch('/cancelConnect', { method: 'POST' });
  const v = await r.text();
  alert(v === 'cancelled' ? '已中止当前连接' : '当前没有进行中的连接');
}
poll();
setInterval(poll, 1000);
</script>
</body>
</html>
""";
    }

    /// <summary>
    /// 连接进度的json序列化上下文
    /// 使用源生成而不依赖反射，以兼容trim发布
    /// </summary>
    [JsonSerializable(typeof(ConnectProgress))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    internal sealed partial class ConnectMonitorContext : JsonSerializerContext
    {
    }
}
