# 安全说明

## 报告漏洞
请不要在公开 Issue 里披露安全问题。请通过 GitHub 的 [Private vulnerability reporting](https://github.com/FlippySun/GameVoiceTranslator/security/advisories/new) 私下报告，我们会尽快回复。

## 设计上的安全措施
- API Key 使用 Windows DPAPI（当前用户）加密保存，不以明文写入磁盘，也不会写进日志。
- 虚拟声卡安装包只从 VB-Audio 官网下载，运行前（包括管理员进程里）校验 Authenticode 签名，签名无效或签名者不是 VB-Audio 作者时拒绝运行。
- 不注入游戏进程、不读写游戏内存、不 Hook 图形接口。
- 发送方向任何环节出错，虚拟麦克风只会静音，不会改发用户的原声。
