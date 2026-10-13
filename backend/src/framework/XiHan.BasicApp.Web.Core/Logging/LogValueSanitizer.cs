// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.BasicApp.Web.Core.Logging;

/// <summary>
/// 写入日志前清理来自请求的值
/// </summary>
public static class LogValueSanitizer
{
    /// <summary>
    /// 去掉回车与换行，空值返回空字符串
    /// </summary>
    /// <param name="value">来自请求的原始值</param>
    /// <returns>不含回车与换行的值</returns>
    public static string Clean(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}
