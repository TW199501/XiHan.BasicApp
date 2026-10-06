// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;

namespace XiHan.BasicApp.Core.Data;

/// <summary>
/// SQL Server 连接配置约定：CodeFirst 建表时字符串列使用 nvarchar
/// </summary>
public static class SqlServerConnectionSettings
{
    /// <summary>
    /// SQL Server 连接开启 <see cref="ConnMoreSettings.SqlServerCodeFirstNvarchar"/>；MoreSettings 为 null 时新建，否则只改这一项；其他数据库类型不处理
    /// </summary>
    /// <param name="config">连接配置</param>
    public static void Apply(ConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.DbType != DbType.SqlServer)
        {
            return;
        }

        config.MoreSettings ??= new ConnMoreSettings();
        config.MoreSettings.SqlServerCodeFirstNvarchar = true;
    }
}
