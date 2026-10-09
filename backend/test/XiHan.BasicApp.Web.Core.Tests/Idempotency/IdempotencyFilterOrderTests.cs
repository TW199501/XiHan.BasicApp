// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Mvc;
using XiHan.BasicApp.Web.Core.Idempotency;
using XiHan.Framework.Web.Api.Filters;

namespace XiHan.BasicApp.Web.Core.Tests.Idempotency;

/// <summary>
/// 幂等过滤器插入位置测试
/// </summary>
public sealed class IdempotencyFilterOrderTests
{
    /// <summary>
    /// 外层过滤器在工作单元之前，完成过滤器紧接在工作单元之后
    /// </summary>
    [Fact]
    public void Filters_AreInsertedAroundUnitOfWorkFilter()
    {
        var options = new MvcOptions();
        options.Filters.Add(new ServiceFilterAttribute(typeof(XiHanCacheFilter)));
        options.Filters.Add(new ServiceFilterAttribute(typeof(XiHanUnitOfWorkFilter)));

        IdempotencyServiceCollectionExtensions.InsertFilters(options);

        var types = options.Filters.OfType<ServiceFilterAttribute>().Select(filter => filter.ServiceType).ToList();
        Assert.Equal([typeof(XiHanCacheFilter), typeof(IdempotencyFilter), typeof(XiHanUnitOfWorkFilter), typeof(IdempotencyCompletionFilter)], types);
    }

    /// <summary>
    /// 重复调用不会重复插入
    /// </summary>
    [Fact]
    public void InsertFilters_IsIdempotent()
    {
        var options = new MvcOptions();
        options.Filters.Add(new ServiceFilterAttribute(typeof(XiHanUnitOfWorkFilter)));

        IdempotencyServiceCollectionExtensions.InsertFilters(options);
        IdempotencyServiceCollectionExtensions.InsertFilters(options);

        Assert.Equal(3, options.Filters.Count);
    }

    /// <summary>
    /// 缺少工作单元过滤器时拒绝注册
    /// </summary>
    [Fact]
    public void InsertFilters_WithoutUnitOfWorkFilter_Throws()
    {
        var options = new MvcOptions();

        Assert.Throws<InvalidOperationException>(() => IdempotencyServiceCollectionExtensions.InsertFilters(options));
    }
}
