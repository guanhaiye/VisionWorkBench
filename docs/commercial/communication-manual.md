# TCP 通信协议手册

支持行、分隔符和长度前缀 framing。生产环境开启 `RequireAuthentication`，请求包含 `clientId`、ISO UTC `timestamp`、随机 `nonce`、`requestId`、`command` 和 HMAC-SHA256 `signature`；服务端校验时间窗、签名和 nonce 重放。相同客户端/项目/requestId 的相同请求返回缓存结果，不同请求体返回冲突。
