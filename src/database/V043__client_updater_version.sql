-- V043 升级执行器版本的可见性
--
-- updater 住在安装目录的**同级**目录（…\BackupMonitor\Updater），而升级只换安装目录——
-- 它换不到自己。后果是每台机器上的 updater 永久停在当初安装时的版本，
-- 而这件事服务端完全不知道：客户端列表里 Agent 版本是最新的，一切看起来都对。
--
-- 这个漂移平时不发作，只在真要改 updater 的那一天一次性爆发：
-- 2026-09-11 修好的「升级死循环」里，三处修复有一处就在 updater 里，
-- 于是它对全网所有存量机器都是不生效的，而没有任何地方能看出这一点。
--
-- 客户端从 1.3.2 起会在心跳里带上 updater 版本，服务端记下来，
-- 和 Agent 版本走同一套漂移判定（AgentVersionService）——
-- 「它的 updater 是哪一版」从此和「它的 Agent 是哪一版」一样自动可见。
ALTER TABLE clients ADD COLUMN IF NOT EXISTS updater_version varchar(64) NULL;

COMMENT ON COLUMN clients.updater_version IS
    '这台客户端上升级执行器的版本；1.3.2 起由心跳上报，为空表示没装或还没报过';
