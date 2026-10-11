// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.BasicApp.Web.Core.Logging;

namespace XiHan.BasicApp.Web.Core.Tests.Logging;

/// <summary>
/// 日志值清理测试
/// </summary>
public sealed class LogValueSanitizerTests
{
    /// <summary>
    /// 回车与换行被去掉，其余字符保留
    /// </summary>
    [Theory]
    [InlineData("authorization_code", "authorization_code")]
    [InlineData("github\r\nINFO 伪造的日志行", "githubINFO 伪造的日志行")]
    [InlineData("/api/Order\n", "/api/Order")]
    [InlineData("\r", "")]
    public void Clean_ShouldRemoveCarriageReturnAndLineFeed(string value, string expected)
    {
        Assert.Equal(expected, LogValueSanitizer.Clean(value));
    }

    /// <summary>
    /// 空值返回空字符串
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clean_ShouldReturnEmptyForNullOrEmpty(string? value)
    {
        Assert.Equal(string.Empty, LogValueSanitizer.Clean(value));
    }
}
