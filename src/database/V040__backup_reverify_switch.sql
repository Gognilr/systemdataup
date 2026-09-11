-- V040 定期复查加关闭开关 + 默认调稀（整改清单 2026-09-10 · R18）
--
-- 定期复查抓的是**入库之后**才会发生的三件事：磁盘静默损坏、误删、勒索软件加密。
-- 这三样入库那一次核对无论多严格都看不到——它们全都发生在核对完成之后。
-- 所以这一层保留、不默认关闭（2026-09-10 定板）。
--
-- 但原来的「24 小时 / 5 份」偏密：复查要整份重算 SHA-256，是纯磁盘读，
-- 与上传抢的是同一块盘。调稀到「7 天 / 1 份」之后仍然能在合理周期内轮到每一份，
-- 而对当晚备份窗口的影响小得多。
--
-- 同时补上一个显式的启用/停用开关。原来的 clamp 是 1~720 小时、1~200 份，
-- **没有关闭的办法**：现场真要临时停掉它（比如正在做一次全量迁移，盘已经满负荷），
-- 只能把间隔改成 720 小时——那是「关掉」的一个变相写法，而不是关掉。

INSERT INTO system_settings (setting_key, setting_value, encrypted, updated_at)
VALUES
    -- 显式开关。默认开：这一层抓的三样东西没有别的地方能抓。
    ('backup_reverify_enabled', 'true'::jsonb, false, now())
ON CONFLICT (setting_key) DO NOTHING;

-- 默认值调稀。用 UPDATE 而不是 INSERT ... ON CONFLICT DO NOTHING：
-- 这两个键 V033 已经种过，DO NOTHING 会让新默认值一个字都不生效。
-- 项目尚未进入生产、部署方式是快照全新安装，因此直接改就是想要的效果。
UPDATE system_settings SET setting_value = '168'::jsonb, updated_at = now()
WHERE setting_key = 'backup_reverify_interval_hours';

UPDATE system_settings SET setting_value = '1'::jsonb, updated_at = now()
WHERE setting_key = 'backup_reverify_batch_size';
