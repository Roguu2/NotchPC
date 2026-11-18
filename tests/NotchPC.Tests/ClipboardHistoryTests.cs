using NotchPC.Core.Clipboard;

namespace NotchPC.Tests;

public sealed class ClipboardHistoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NotchPC.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static ClipEntry Text(string s, int minute = 0) => new()
    {
        Kind = ClipKind.Text,
        Text = s,
        At = T0.AddMinutes(minute),
        Hash = ClipEntry.HashOf(ClipKind.Text, s, null, []),
    };

    [Fact]
    public void Newest_first_and_duplicates_move_to_the_top()
    {
        var h = new ClipboardHistory();
        h.Add(Text("a", 0));
        h.Add(Text("b", 1));
        h.Add(Text("a", 2));

        Assert.Equal(["a", "b"], h.Items.Select(e => e.Text));
        Assert.Equal(2, h.Items.Count);
    }

    [Fact]
    public void Limit_drops_the_oldest_unpinned_entry()
    {
        var h = new ClipboardHistory(limit: 2);
        var first = Text("first", 0);
        h.Add(first);
        h.SetPinned(first.Id, true);
        h.Add(Text("second", 1));

        var removed = h.Add(Text("third", 2));

        Assert.Equal(["second"], removed.Select(e => e.Text));
        Assert.Equal(["third", "first"], h.Items.Select(e => e.Text));
    }

    [Fact]
    public void Re_copying_keeps_the_pin()
    {
        var h = new ClipboardHistory();
        var a = Text("a");
        h.Add(a);
        h.SetPinned(a.Id, true);
        h.Add(Text("b", 1));

        h.Add(Text("a", 2));

        Assert.True(h.Items[0].Pinned);
    }

    [Fact]
    public void View_puts_pinned_first_and_filters()
    {
        var h = new ClipboardHistory();
        var pinned = Text("Adres: Kraków", 0);
        h.Add(pinned);
        h.SetPinned(pinned.Id, true);
        h.Add(Text("kod 1234", 1));
        h.Add(Text("KRAKÓW znowu", 2));

        Assert.Equal("Adres: Kraków", h.View(null)[0].Text);
        Assert.Equal(["Adres: Kraków", "KRAKÓW znowu"], h.View("kraków").Select(e => e.Text));
    }

    [Fact]
    public void Clear_keeps_pinned_entries()
    {
        var h = new ClipboardHistory();
        var keep = Text("keep");
        h.Add(keep);
        h.SetPinned(keep.Id, true);
        h.Add(Text("drop", 1));

        var removed = h.ClearUnpinned();

        Assert.Equal(["drop"], removed.Select(e => e.Text));
        Assert.Equal(["keep"], h.Items.Select(e => e.Text));
    }

    [Fact]
    public void Files_are_searchable_by_name()
    {
        var e = new ClipEntry { Kind = ClipKind.Files, Files = [@"C:\Users\x\Desktop\raport.pdf"], Hash = "h" };

        Assert.Equal("raport.pdf", e.SearchText);
    }

    [Fact]
    public void Store_round_trips_through_the_protector_and_never_writes_plain_text()
    {
        static byte[] Flip(byte[] b) => b.Select(x => (byte)(x ^ 0x5A)).ToArray();
        var store = new ClipboardStore(_dir, Flip, Flip);
        var entry = Text("tajne hasło? nie, zwykły tekst");

        store.SaveIndex([entry]);
        store.SaveImage(entry.Id, [1, 2, 3]);

        Assert.Equal(entry.Text, store.LoadIndex().Single().Text);
        Assert.Equal([1, 2, 3], store.LoadImage(entry.Id));
        Assert.DoesNotContain("zwykły", File.ReadAllText(Path.Combine(_dir, "history.dat")), StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_store_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "history.dat"), [1, 2, 3]);

        var store = new ClipboardStore(_dir, b => b, _ => throw new System.Security.Cryptography.CryptographicException());

        Assert.Empty(store.LoadIndex());
    }
}
