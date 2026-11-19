using System.Globalization;
using NotchPC.Core.Remote;

namespace NotchPC.Tests;

public sealed class RemoteFilesTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("../../Windows/evil.exe", "evil.exe")]
    [InlineData(@"C:\Users\x\doc.pdf", "doc.pdf")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("name. . ", "name")]
    [InlineData("", "file")]
    [InlineData("..", "file")]
    public void Safe_names(string raw, string safe) => Assert.Equal(safe, RemoteFiles.SafeName(raw));

    [Fact]
    public void Long_names_keep_the_extension()
    {
        var name = RemoteFiles.SafeName(new string('a', 300) + ".mp4");
        Assert.Equal(RemoteFiles.MaxNameLength, name.Length);
        Assert.EndsWith(".mp4", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Unique_adds_a_number()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "photo.jpg", "photo (2).jpg" };
        Assert.Equal("photo (3).jpg", RemoteFiles.Unique("photo.jpg", taken.Contains));
        Assert.Equal("other.jpg", RemoteFiles.Unique("other.jpg", taken.Contains));
    }

    [Fact]
    public void Ids_ignore_case_and_differ_per_file()
    {
        Assert.Equal(RemoteFiles.IdFor(@"C:\A\b.txt"), RemoteFiles.IdFor(@"c:\a\B.TXT"));
        Assert.NotEqual(RemoteFiles.IdFor(@"C:\A\b.txt"), RemoteFiles.IdFor(@"C:\A\c.txt"));
        Assert.Equal(20, RemoteFiles.IdFor("x").Length);
    }

    [Fact]
    public void Sizes() => Assert.Equal("3,4 MB", RemoteFiles.Size(3_565_158, CultureInfo.GetCultureInfo("pl-PL")));

    [Fact]
    public void File_and_clipboard_commands()
    {
        Assert.IsType<RemoteCommand.ListFiles>(RemoteCommand.Parse("""{"t":"files"}"""));
        Assert.IsType<RemoteCommand.ListClips>(RemoteCommand.Parse("""{"t":"clips"}"""));
        Assert.Equal("hej", Assert.IsType<RemoteCommand.SetClipboard>(RemoteCommand.Parse("""{"t":"setclip","s":"hej"}""")).Value);
        Assert.Null(RemoteCommand.Parse("""{"t":"setclip","s":""}"""));
        Assert.Null(RemoteCommand.Parse($$"""{"t":"setclip","s":"{{new string('x', RemoteCommand.MaxClipboard + 1)}}"}"""));
    }
}
