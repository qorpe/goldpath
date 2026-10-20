using System.Text;
using Xunit;

namespace Goldpath.Bulk.Tests;

public class CsvFormatTests
{
    private static async Task<(List<GoldpathBulkRawRow> Rows, List<GoldpathBulkRawError> Errors, int Total)> ReadAsync(
        string text, char delimiter = ',')
    {
        var format = new GoldpathCsvFormat(new GoldpathCsvOptions { Delimiter = delimiter });
        var rows = new List<GoldpathBulkRawRow>();
        var errors = new List<GoldpathBulkRawError>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var total = await format.ReadAsync(
            stream,
            row => { rows.Add(row); return Task.CompletedTask; },
            error => { errors.Add(error); return Task.CompletedTask; },
            CancellationToken.None);
        return (rows, errors, total);
    }

    [Fact]
    public async Task Maps_columns_by_header_name_case_insensitive()
    {
        var (rows, errors, total) = await ReadAsync("iban,AMOUNT\nTR1,10\nTR2,20\n");
        Assert.Empty(errors);
        Assert.Equal(2, total);
        Assert.Equal("TR1", rows[0].Fields["Iban"]);
        Assert.Equal("20", rows[1].Fields["amount"]);
        Assert.Equal(1, rows[0].RowNumber);
        Assert.Equal(2, rows[1].RowNumber);
    }

    [Fact]
    public async Task Quoted_fields_carry_delimiters_and_escaped_quotes()
    {
        var (rows, _, _) = await ReadAsync("Name,Note\n\"Çelik, Ömer\",\"said \"\"hi\"\"\"\n");
        Assert.Equal("Çelik, Ömer", rows[0].Fields["Name"]);
        Assert.Equal("said \"hi\"", rows[0].Fields["Note"]);
    }

    [Fact]
    public async Task Semicolon_delimiter_is_a_configuration()
    {
        var (rows, _, _) = await ReadAsync("A;B\n1;2\n", delimiter: ';');
        Assert.Equal("1", rows[0].Fields["A"]);
        Assert.Equal("2", rows[0].Fields["B"]);
    }

    [Fact]
    public async Task A_broken_line_is_a_row_error_not_an_exception_and_the_rest_still_parses()
    {
        var (rows, errors, total) = await ReadAsync("A,B\n1,2\nonly-one-field\n3,4\n");
        Assert.Equal(3, total);
        Assert.Equal(2, rows.Count);
        var error = Assert.Single(errors);
        Assert.Equal(2, error.RowNumber);
        Assert.Contains("expected 2 fields", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_headerless_file_fails_with_a_teaching_message()
    {
        var (rows, errors, total) = await ReadAsync("");
        Assert.Empty(rows);
        Assert.Equal(0, total);
        Assert.Contains("no header row", Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    /// <summary>D3: a spreadsheet's "Unicode" export (UTF-16 with its byte-order mark) reads like UTF-8.</summary>
    [Fact]
    public async Task A_utf16_export_with_its_byte_order_mark_parses()
    {
        var format = new GoldpathCsvFormat(new GoldpathCsvOptions());
        var rows = new List<GoldpathBulkRawRow>();
        using var stream = new MemoryStream([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Name,Amount\nÇelik,10\n")]);

        var total = await format.ReadAsync(stream, row => { rows.Add(row); return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(1, total);
        Assert.Equal("Çelik", rows[0].Fields["Name"]);
        Assert.Equal("10", rows[0].Fields["Amount"]);
    }

    /// <summary>The format seam reads a stream it does not own: the caller's stream stays open.</summary>
    [Fact]
    public async Task The_reader_leaves_the_callers_stream_open()
    {
        var format = new GoldpathCsvFormat(new GoldpathCsvOptions());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("A\n1\n"));

        await format.ReadAsync(stream, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(stream.CanRead, "disposing the reader must not close the caller's stream");
    }

    [Fact]
    public async Task Blank_lines_are_noise_not_data()
    {
        var (rows, errors, total) = await ReadAsync("A,B\n1,2\n\n\n3,4\n");
        Assert.Empty(errors);
        Assert.Equal(2, total);
        Assert.Equal(2, rows.Count);
    }
}
