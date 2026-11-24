using System.Text;
using MatContactHub.Services.Sources;

namespace MatContactHub.Tests;

/// <summary>The CSV reader: delimiters (found and chosen), quotes with delimiters, quotes and line breaks inside, line ends, the byte order mark, Windows-1252.</summary>
public sealed class SourceCsvTests
{
    private static List<SourceRecord> Read(string text, CsvSettings? settings = null, Encoding? encoding = null)
    {
        var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(text);
        return [.. CsvParser.Records(new MemoryStream(bytes), settings ?? new CsvSettings())];
    }

    private static List<SourceRecord> Read(byte[] bytes, CsvSettings? settings = null) => [.. CsvParser.Records(new MemoryStream(bytes), settings ?? new CsvSettings())];

    [Theory]
    [InlineData("id;name;city\n1;Ada;Berlin\n", ';')]
    [InlineData("id,name,city\n1,Ada,Berlin\n", ',')]
    [InlineData("id\tname\tcity\n1\tAda\tBerlin\n", '\t')]
    [InlineData("id|name|city\n1|Ada|Berlin\n", '|')]
    public void The_delimiter_is_found(string text, char expected)
    {
        Assert.Equal(expected, CsvParser.Detect(text));
        var record = Assert.Single(Read(text));
        Assert.Equal("1", record["id"]);
        Assert.Equal("Ada", record["NAME"]);
        Assert.Equal("Berlin", record["city"]);
    }

    [Fact]
    public void A_delimiter_inside_quotes_does_not_count_for_the_detection()
        => Assert.Equal(';', CsvParser.Detect("\"a,b,c\";\"d,e\";f\n"));

    [Fact]
    public void A_chosen_delimiter_wins_over_the_detection()
    {
        var records = Read("a,b;c\n1,2;3\n", new CsvSettings { Delimiter = CsvDelimiter.Semicolon });
        Assert.Equal("1,2", records[0]["a,b"]);
        Assert.Equal("3", records[0]["c"]);
    }

    [Fact]
    public void Quoted_fields_hold_delimiters_quotes_and_line_breaks()
    {
        var text = "id;name;notes\r\n1;\"Müller; Hans\";\"Line one\r\nLine two\"\r\n2;\"Say \"\"hi\"\"\";\"\"\r\n";
        var records = Read(text);
        Assert.Equal(2, records.Count);
        Assert.Equal("Müller; Hans", records[0]["name"]);
        Assert.Equal("Line one\r\nLine two", records[0]["notes"]);
        Assert.Equal("Say \"hi\"", records[1]["name"]);
        Assert.Equal(string.Empty, records[1]["notes"]);
    }

    [Theory]
    [InlineData("a;b\n1;2\n3;4")]
    [InlineData("a;b\r\n1;2\r\n3;4\r\n")]
    [InlineData("a;b\r1;2\r3;4\r")]
    [InlineData("a;b\n\n1;2\n\n\n3;4\n\n")]
    public void Every_line_end_works_and_empty_lines_are_left_out(string text)
    {
        var records = Read(text);
        Assert.Equal(2, records.Count);
        Assert.Equal("2", records[0]["b"]);
        Assert.Equal("3", records[1]["a"]);
    }

    [Fact]
    public void The_utf8_byte_order_mark_is_not_part_of_the_first_name()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Vorname;Nachname\nJürgen;Größe\n")).ToArray();
        var record = Assert.Single(Read(bytes));
        Assert.Equal("Jürgen", record["Vorname"]);
        Assert.Equal("Größe", record["Nachname"]);
    }

    [Fact]
    public void A_byte_order_mark_wins_over_the_chosen_encoding()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Ort\nMünchen\n")).ToArray();
        var record = Assert.Single(Read(bytes, new CsvSettings { Encoding = CsvEncoding.Windows1252 }));
        Assert.Equal("München", record["Ort"]);
    }

    [Fact]
    public void Windows_1252_umlauts_are_read()
    {
        var cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        var bytes = cp1252.GetBytes("Name;Straße;Betrag\nÄrzte Müller;Weißdornweg 3;€ 5\n");
        var record = Assert.Single(Read(bytes, new CsvSettings { Encoding = CsvEncoding.Windows1252 }));
        Assert.Equal("Ärzte Müller", record["Name"]);
        Assert.Equal("Weißdornweg 3", record["Straße"]);
        Assert.Equal("€ 5", record["Betrag"]);
    }

    [Fact]
    public void Latin1_is_read()
    {
        var bytes = Encoding.Latin1.GetBytes("Ort\nKöln\n");
        Assert.Equal("Köln", Assert.Single(Read(bytes, new CsvSettings { Encoding = CsvEncoding.Latin1 }))["Ort"]);
    }

    [Fact]
    public void Windows_1252_read_as_utf8_is_not_silently_right()
    {
        // the wrong encoding gives replacement characters: that is why the page offers the choice
        var bytes = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes("Ort\nKöln\n");
        Assert.NotEqual("Köln", Assert.Single(Read(bytes))["Ort"]);
    }

    [Fact]
    public void Without_a_header_the_fields_are_numbered()
    {
        var records = Read("1;Ada\n2;Grace;extra\n", new CsvSettings { HasHeader = false });
        Assert.Equal(2, records.Count);
        Assert.Equal("1", records[0]["Column1"]);
        Assert.Equal("Ada", records[0]["Column2"]);
        Assert.Equal("extra", records[1]["Column3"]);
    }

    [Fact]
    public void Missing_fields_are_empty_and_extra_ones_get_a_column_name()
    {
        var records = Read("a;b;c\n1\n1;2;3;4\n");
        Assert.Null(records[0]["c"]);
        Assert.Equal("4", records[1]["Column4"]);
    }

    [Fact]
    public void Empty_and_repeated_header_names_are_made_unique()
    {
        var record = Assert.Single(Read("Name;;Name\nA;B;C\n"));
        Assert.Equal("A", record["Name"]);
        Assert.Equal("B", record["Column2"]);
        Assert.Equal("C", record["Name_2"]);
    }

    [Fact]
    public async Task The_uploaded_file_is_read_through_the_connector()
    {
        using var kit = new SourceTestKit();
        var token = Guid.NewGuid();
        Directory.CreateDirectory(kit.Env.SourcesDirectory);
        await File.WriteAllTextAsync(kit.Env.CsvPath(token), "id,name\n1,Ada\n2,Grace\n");
        var connector = CsvConnector.For(new CsvSettings(), token, kit.Env);
        var records = await SourceTestKit.ReadAllAsync(connector);
        Assert.Equal(["Ada", "Grace"], records.Select(r => r["name"]));
        Assert.Equal(["id", "name"], await ((ISourceConnector)connector).DiscoverFieldsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_upload_is_a_worded_error()
    {
        using var kit = new SourceTestKit();
        var connector = CsvConnector.For(new CsvSettings(), Guid.NewGuid(), kit.Env);
        await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(connector));
    }

    [Fact]
    public async Task A_file_at_an_address_is_fetched()
    {
        using var kit = new SourceTestKit(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("id;Firma\n7;ACME\n")) });
        var connector = CsvConnector.For(new CsvSettings { Origin = CsvOrigin.Url, Url = "https://files.example.com/a.csv" }, Guid.NewGuid(), kit.Env);
        var record = Assert.Single(await SourceTestKit.ReadAllAsync(connector));
        Assert.Equal("ACME", record["firma"]);
        Assert.Equal("https://files.example.com/a.csv", kit.Handler.Requests.Single().Request.RequestUri!.ToString());
    }

    [Fact]
    public void A_large_file_is_read_in_blocks()
    {
        var sb = new StringBuilder("id;name\n");
        for (var i = 0; i < 20_000; i++)
        {
            sb.Append(i).Append(";\"Name ").Append(i).Append("\n(second line)\"\n");
        }

        var records = Read(sb.ToString());
        Assert.Equal(20_000, records.Count);
        Assert.Equal("Name 19999\n(second line)", records[^1]["name"]);
    }
}
