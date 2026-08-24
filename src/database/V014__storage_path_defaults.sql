-- V014：清除 V001 种子里写死的开发机盘符
--
-- V001 把 repository_path / staging_path 直接种成了 'E:\BackupRepository' 与
-- 'D:\BackupStaging'——那是开发机上的盘符。目标机上通常没有这两个盘，而解析仓库根的
-- 代码会对该路径执行 Directory.CreateDirectory，于是任何一次仓库路径解析都抛异常。
-- 表现出来就是概览页的容量统计 500，整个概览加载失败，而且看不出跟盘符有关。
--
-- 改成 JSON null：设置读取端只接受 JsonValueKind.String，取到 null 就按「未配置」
-- 走回退链（Storage:RepositoryPath 配置项 → 安装目录下的 data\repository）。
-- 保留行本身，管理界面上这两个键仍然存在，只是没有值。
--
-- 只清理仍然等于种子值的行：管理员已经显式配置过的路径不能碰。

UPDATE system_settings
   SET setting_value = 'null'::jsonb,
       updated_at    = now(),
       row_version   = row_version + 1
 WHERE setting_key = 'repository_path'
   AND setting_value #>> '{}' = 'E:\BackupRepository';  -- legacy-path-cleanup

UPDATE system_settings
   SET setting_value = 'null'::jsonb,
       updated_at    = now(),
       row_version   = row_version + 1
 WHERE setting_key = 'staging_path'
   AND setting_value #>> '{}' = 'D:\BackupStaging';  -- legacy-path-cleanup
