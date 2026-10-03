using System.Text;
using Mailserver.Imap.Mime;
using Mailserver.Imap.Protocol;

namespace Mailserver.Tests;

public class MimeStructureTests
{
    // A forwarded message: multipart/mixed with a text part and an attached message/rfc822, which is itself multipart.
    private const string Forwarded =
        "From: Alice <alice@example.test>\r\n" +
        "Subject: Fwd: Angebot\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"outer\"\r\n" +
        "\r\n" +
        "preamble\r\n" +
        "--outer\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "Siehe unten.\r\n" +
        "--outer\r\n" +
        "Content-Type: message/rfc822\r\n" +
        "Content-Disposition: attachment\r\n" +
        "\r\n" +
        "From: Bob <bob@remote.test>\r\n" +
        "Subject: Angebot\r\n" +
        "Content-Type: multipart/alternative; boundary=inner\r\n" +
        "\r\n" +
        "--inner\r\n" +
        "Content-Type: text/plain\r\n" +
        "\r\n" +
        "Preis: 100 EUR\r\n" +
        "--inner\r\n" +
        "Content-Type: text/html\r\n" +
        "\r\n" +
        "<p>Preis: 100 EUR</p>\r\n" +
        "--inner--\r\n" +
        "--outer--\r\n" +
        "epilogue\r\n";

    private static readonly MimeNode Root = MimeNode.Parse(Encoding.ASCII.GetBytes(Forwarded));

    [Theory]
    [InlineData("BODY[1]", "Siehe unten.")]
    [InlineData("BODY[2.HEADER.FIELDS (SUBJECT)]", "Subject: Angebot\r\n\r\n")]
    [InlineData("BODY[2.1]", "Preis: 100 EUR")]
    [InlineData("BODY[2.2]", "<p>Preis: 100 EUR</p>")]
    [InlineData("BODY[2.1.MIME]", "Content-Type: text/plain\r\n\r\n")]
    [InlineData("BODY[2.MIME]", "Content-Type: message/rfc822\r\nContent-Disposition: attachment\r\n\r\n")]
    [InlineData("BODY[HEADER.FIELDS.NOT (FROM MIME-VERSION CONTENT-TYPE)]", "Subject: Fwd: Angebot\r\n\r\n")]
    [InlineData("BODY[1]<6.5>", "unten")]
    [InlineData("BODY[9]", "")]
    public void Extracts_exact_section_bytes(string section, string expected)
    {
        var parsed = BodySection.TryParse(section);

        Assert.NotNull(parsed);
        Assert.Equal(expected, Encoding.ASCII.GetString(parsed.Extract(Root)));
    }

    [Fact]
    public void Body_of_rfc822_part_is_the_complete_inner_message()
    {
        var inner = Encoding.ASCII.GetString(BodySection.TryParse("BODY[2]")!.Extract(Root));

        Assert.StartsWith("From: Bob", inner);
        Assert.EndsWith("--inner--", inner);
    }

    [Fact]
    public void Bodystructure_describes_nested_message()
    {
        var response = new ImapResponse();
        StructureWriter.WriteBodyStructure(response, Root, extensible: true);
        var text = Encoding.UTF8.GetString(response.ToMemory().Span);

        Assert.StartsWith("((\"text\" \"plain\" (\"charset\" \"utf-8\") NIL NIL \"7BIT\" 12 1 NIL NIL NIL NIL)(\"message\" \"rfc822\"", text);
        Assert.Contains("\"Angebot\"", text);  // envelope subject of the inner message
        Assert.Contains("\"alternative\"", text);
        Assert.Contains("(\"attachment\" NIL)", text);
        Assert.EndsWith(" \"mixed\" (\"boundary\" \"outer\") NIL NIL NIL)", text);
    }

    [Theory]
    [InlineData("Entwürfe", "Entw&APw-rfe")]
    [InlineData("A&B", "A&-B")]
    [InlineData("日本語", "&ZeVnLIqe-")]
    [InlineData("Plain", "Plain")]
    public void Encodes_folder_names_as_modified_utf7(string name, string encoded)
    {
        Assert.Equal(encoded, ModifiedUtf7.Encode(name));
        Assert.Equal(name, ModifiedUtf7.Decode(encoded));
    }

    [Theory]
    [InlineData("1:3,5", 5, new long[] { 1, 2, 3, 5 })]
    [InlineData("4:*", 6, new long[] { 4, 5, 6 })]
    [InlineData("*", 6, new long[] { 6 })]
    [InlineData("10:*", 6, new long[] { 6 })]
    public void Resolves_sequence_sets(string set, long max, long[] expected)
    {
        var parsed = SequenceSet.Parse(set);

        Assert.Equal(expected, Enumerable.Range(1, (int)max).Select(i => (long)i).Where(n => parsed.Contains(n, max)));
    }

    [Fact]
    public void Formats_uid_sets_compactly() => Assert.Equal("1:3,7,9:10", SequenceSet.Format([1, 2, 3, 7, 9, 10]));
}
