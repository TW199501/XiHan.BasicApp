-- MySQL：收发件箱与幂等记录表的时间列改为 datetime(6)，收件箱去重键 Dedup_Key 改为 utf8mb4_bin。
--
-- 针对 CodeFirst 按旧实体建出的表：时间列为 datetime（0 位小数秒），去重键使用数据库默认排序规则。
-- 每张表先查 information_schema，有需要修改的列才执行一次 ALTER TABLE；表不存在或已是目标定义时不做任何改动，可重复执行。
-- 列的类型、可空性与注释与实体一致；时间值原样保留，只补上小数位。
-- utf8mb4_bin 比较时忽略尾端空格：旧数据中有只差尾端空格的去重键时，ALTER 因唯一索引冲突失败，该表保持原样。
-- 条件判断写在临时存储过程里，执行后删除；需要 CREATE ROUTINE 权限。脚本不使用 DELIMITER 与用户变量。

DROP PROCEDURE IF EXISTS xihan_upgrade_5_6_1;

CREATE PROCEDURE xihan_upgrade_5_6_1()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema = DATABASE() AND table_name = 'Sys_Event_Outbox'
                 AND column_name IN ('Created_Time', 'Claim_Time')
                 AND data_type = 'datetime' AND datetime_precision < 6) THEN
        ALTER TABLE `Sys_Event_Outbox`
            MODIFY COLUMN `Created_Time` datetime(6) NOT NULL COMMENT '事件创建时间',
            MODIFY COLUMN `Claim_Time` datetime(6) NULL COMMENT '领取时刻';
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema = DATABASE() AND table_name = 'Sys_Event_Inbox'
                 AND ((column_name IN ('Created_Time', 'Next_Retry_Time', 'Claim_Time', 'Handled_Time')
                       AND data_type = 'datetime' AND datetime_precision < 6)
                   OR (column_name = 'Dedup_Key' AND collation_name <> 'utf8mb4_bin'))) THEN
        ALTER TABLE `Sys_Event_Inbox`
            MODIFY COLUMN `Dedup_Key` varchar(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL COMMENT '去重键，有消息标识时等于消息标识',
            MODIFY COLUMN `Created_Time` datetime(6) NOT NULL COMMENT '入箱时刻',
            MODIFY COLUMN `Next_Retry_Time` datetime(6) NULL COMMENT '下次重试时刻',
            MODIFY COLUMN `Claim_Time` datetime(6) NULL COMMENT '领取时刻',
            MODIFY COLUMN `Handled_Time` datetime(6) NULL COMMENT '完结时刻';
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema = DATABASE() AND table_name = 'Sys_Idempotency_Record'
                 AND column_name IN ('Lease_Expires_Time', 'Expires_Time', 'Created_Time', 'Completed_Time')
                 AND data_type = 'datetime' AND datetime_precision < 6) THEN
        ALTER TABLE `Sys_Idempotency_Record`
            MODIFY COLUMN `Lease_Expires_Time` datetime(6) NOT NULL COMMENT '处理中租约到期时间',
            MODIFY COLUMN `Expires_Time` datetime(6) NULL COMMENT '完成或不确定记录过期时间',
            MODIFY COLUMN `Created_Time` datetime(6) NOT NULL COMMENT '创建时间',
            MODIFY COLUMN `Completed_Time` datetime(6) NULL COMMENT '完成时间';
    END IF;
END;

CALL xihan_upgrade_5_6_1();

DROP PROCEDURE xihan_upgrade_5_6_1;
