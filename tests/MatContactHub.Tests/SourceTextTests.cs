using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Pages.Sources;
using MatContactHub.Services.Sources;

namespace MatContactHub.Tests;

/// <summary>The texts of the data sources that are built at run time (a name and a value of an enum or a list) exist in the catalog.</summary>
public sealed class SourceTextTests
{
    public static TheoryData<string> Keys()
    {
        var keys = new TheoryData<string>();
        IEnumerable<string> all =
        [
            .. FieldMapper.Targets.Select(t => "Sources.Target." + t.Name),
            .. FieldMapper.Groups.Select(g => "Sources.Group." + g),
            .. Enum.GetNames<FieldTransform>().Select(n => "Sources.Transform." + n),
            .. Enum.GetNames<RestAuth>().Select(n => "Sources.Rest.Auth." + n),
            .. Enum.GetNames<RestPagination>().SelectMany(n => new[] { "Sources.Rest.Pagination." + n, "Sources.Rest.Pagination." + n + ".Hint" }),
            .. Enum.GetNames<CsvOrigin>().Select(n => "Sources.Csv.Origin." + n),
            .. Enum.GetNames<CsvDelimiter>().Select(n => "Sources.Csv.Delimiter." + n),
            .. Enum.GetNames<CsvEncoding>().Select(n => "Sources.Csv.Encoding." + n),
            .. Enum.GetNames<SyncTrigger>().Select(n => "Sources.Trigger." + n),
            .. Enum.GetNames<SyncState>().Select(n => "Sources.State." + n),
            .. Enum.GetNames<DataSourceKind>().Select(n => "Sources.Kind." + n),
            .. EditModel.Tabs.Select(t => "Sources.Tab." + t),
            .. new[] { "NoKey", "KeyTooLong", "Empty", "Duplicate", "Revived" }.Select(r => "Sources.Run." + r),
        ];
        foreach (var key in all)
        {
            keys.Add(key);
        }

        return keys;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_key_exists(string key) => Assert.True(TextCatalog.Entries.ContainsKey(key), key);
}
