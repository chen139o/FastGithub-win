using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static PInvoke.AdvApi32;

namespace FastGithub.DomainResolve
{
    public static class ServiceInstallUtil
    {
        /// <summary>
        /// 服务已在运行时StartService返回的系统错误码
        /// </summary>
        private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;

        /// <summary>
        /// 安装并启动服务
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="binaryPath"></param>
        /// <param name="startType"></param>
        /// <returns></returns>
        /// <exception cref="Win32Exception">打开服务管理器、创建服务或启动服务失败，消息中包含系统错误原因</exception>
        [SupportedOSPlatform("windows")]
        public static bool InstallAndStartService(string serviceName, string binaryPath, ServiceStartType startType = ServiceStartType.SERVICE_AUTO_START)
        {
            using var hSCManager = OpenSCManager(null, null, ServiceManagerAccess.SC_MANAGER_ALL_ACCESS);
            if (hSCManager.IsInvalid == true)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"打开服务管理器失败，无法安装服务{serviceName}（通常是没有管理员权限）");
            }

            var hService = OpenService(hSCManager, serviceName, ServiceAccess.SERVICE_ALL_ACCESS);
            if (hService.IsInvalid == true)
            {
                hService = CreateService(
                    hSCManager,
                    serviceName,
                    serviceName,
                    ServiceAccess.SERVICE_ALL_ACCESS,
                    ServiceType.SERVICE_WIN32_OWN_PROCESS,
                    startType,
                    ServiceErrorControl.SERVICE_ERROR_NORMAL,
                    Path.GetFullPath(binaryPath),
                    lpLoadOrderGroup: null,
                    lpdwTagId: 0,
                    lpDependencies: null,
                    lpServiceStartName: null,
                    lpPassword: null);
            }

            if (hService.IsInvalid == true)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"创建服务{serviceName}失败");
            }

            using (hService)
            {
                if (StartService(hService, 0, null) == false)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ERROR_SERVICE_ALREADY_RUNNING)
                    {
                        throw new Win32Exception(error, $"启动服务{serviceName}失败");
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// 停止并删除服务
        /// </summary>
        /// <param name="serviceName"></param>
        /// <returns></returns>
        /// <exception cref="Win32Exception">打开服务管理器或删除服务失败，消息中包含系统错误原因</exception>
        [SupportedOSPlatform("windows")]
        public static bool StopAndDeleteService(string serviceName)
        {
            using var hSCManager = OpenSCManager(null, null, ServiceManagerAccess.SC_MANAGER_ALL_ACCESS);
            if (hSCManager.IsInvalid == true)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"打开服务管理器失败，无法停止服务{serviceName}（通常是没有管理员权限）");
            }

            using var hService = OpenService(hSCManager, serviceName, ServiceAccess.SERVICE_ALL_ACCESS);
            if (hService.IsInvalid == true)
            {
                // 服务不存在，视为已经处于停止状态
                return true;
            }

            var status = new SERVICE_STATUS();
            if (QueryServiceStatus(hService, ref status) == true)
            {
                if (status.dwCurrentState != ServiceState.SERVICE_STOP_PENDING &&
                    status.dwCurrentState != ServiceState.SERVICE_STOPPED)
                {
                    ControlService(hService, ServiceControl.SERVICE_CONTROL_STOP, ref status);
                }
            }

            if (DeleteService(hService) == false)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"删除服务{serviceName}失败");
            }
            return true;
        }
    }
}
