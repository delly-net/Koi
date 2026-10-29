using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Koi.Api.Data;

/// <summary>
/// 把 <see cref="DateTimeOffset"/> 以 UTC ticks（long）落库。
/// SQLite 提供程序不翻译 DateTimeOffset 之间的比较（列默认存 TEXT 字符串），
/// 导致 <c>Where(x => x.ExpiresAt &lt;= now)</c> 之类的查询与 <c>ExecuteDelete</c> 抛
/// 「could not be translated」；转换为整数后比较、排序与批量删除均可翻译，索引依然可用。
/// </summary>
public sealed class UtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcTicksConverter()
        : base(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
    {
    }
}
