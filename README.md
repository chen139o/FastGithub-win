# FastGithub-win

FastGithub-win 是 GitHub 加速的工具，优化了原版网络。

> **目前只对 Windows 做了优化并提供安装包，其他平台未做测试。**

## 致谢

本项目基于 [creazyboyone/FastGithub](https://github.com/creazyboyone/FastGithub) 修改而来，感谢原作者的付出。

上游更早的源头是 [dotnetcore/FastGithub](https://github.com/dotnetcore/FastGithub)（原仓库已不可访问）。

## 1 写在前面

* fastgithub不具备“翻墙”功能,也没有相关的计划
* fastgithub不支持Windows7等已被发行方停止支持的操作系统，并且也不会主动提供支持
* fastgithub不能为您的游戏加速
* fastgithub没有主动在github之外的任何渠道发布

## 2 部署方式

### 2.1 windows-x64

* 启动：解压后**以管理员身份运行** `fastgithub.exe`（它会开一个控制台窗口显示日志；不加管理员会因为绑不了 80/443、加载不了 WinDivert 驱动而失败）。
* 退出：用 `Ctrl+C`，**不要直接点窗口的 X**。程序在退出时会执行恢复逻辑——之前注释掉的 hosts 行、写入的代理设置都靠这一步还原。强杀（任务管理器结束进程）会跳过恢复，留下残留。

### 2.2 windows-x64服务

* `fastgithub.exe start` // 以windows服务安装并启动
* `fastgithub.exe stop` // 以windows服务卸载并删除

## 3 软件功能

* 提供域名的纯净IP解析；
* 提供IP测速并选择最快的IP；
* 提供域名的tls连接自定义配置；
* google的CDN资源替换，解决大量国外网站无法加载js和css的问题；

## 4 证书验证

### 4.1 git

程序启动时会自动为 git 配置 TLS 后端（Windows 上使用 `schannel`），让 git 通过系统证书存储信任本程序安装的自签 CA 证书，**因此不需要关闭证书校验**。

如果仍然提示 `SSL certificate problem`，可以手动执行：

    git config --global http.sslBackend schannel
    git config --global http.sslverify true

> 旧版本会执行 `git config --global http.sslverify false`。本版本已不再这样做，并会把之前遗留的 `false` 改回 `true`。

### 4.2 firefox

firefox提示`连接有潜在的安全问题`</br>
设置->隐私与安全->证书->查看证书->证书颁发机构，导入cacert/fastgithub.cer，勾选“信任由此证书颁发机构来标识网站”

## 5 安全性说明

FastGithub为每台不同的主机生成自颁发CA证书，保存在cacert文件夹下。客户端设备需要安装和无条件信任自颁发的CA证书，请不要将证书私钥泄露给他人，以免造成损失。

## 6 合法性说明

《国际联网暂行规定》第六条规定：“计算机信息网络直接进行国际联网，必须使用邮电部国家公用电信网提供的国际出入口信道。任何单位和个人不得自行建立或者使用其他信道进行国际联网。”

FastGithub本地代理使用的都是“公用电信网提供的国际出入口信道”，从国外Github服务器到国内用户电脑上FastGithub程序的流量，使用的是正常流量通道，其间未对流量进行任何额外加密（仅有网页原有的TLS加密，区别于VPN的流量加密），而FastGithub获取到网页数据之后发生的整个代理过程完全在国内，不再适用国际互联网相关之规定。
