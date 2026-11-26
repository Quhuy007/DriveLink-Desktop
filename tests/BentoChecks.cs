using DriveLink;
using DriveLink.Ui;
using System.Text.Json;

/// <summary>Bố cục bento do người dùng sắp xếp: xếp ô vào lưới, sửa cài đặt hỏng/cũ, lưu và đọc lại.</summary>
static class BentoChecks
{
    private static string Describe(BentoLayout layout, int columns) =>
        string.Join(" ", layout.Pack(columns).Placements.Select(p => $"{p.Id}@{p.Row},{p.Column}x{p.RowSpan}x{p.ColumnSpan}"));

    public static void Run(Action<bool, string> check)
    {
        var standard = BentoLayout.Default();
        check(Describe(standard, 2) == "send@0,0x1x1 transfer@0,1x2x1 spaces@1,0x1x1 cross@2,0x1x1 storage@2,1x1x1" && standard.Pack(2).Rows == 3 && standard.IsDefault,
            "The default bento layout is the same as before customization (two columns, Transfers tall)");
        check(Describe(standard, 1) == "send@0,0x1x1 transfer@1,0x2x1 spaces@3,0x1x1 cross@4,0x1x1 storage@5,0x1x1" && standard.Pack(1).Rows == 6,
            "A narrow window stacks every visible tile in one column, in the user's order");

        var custom = BentoLayout.Default();
        custom.Swap(BentoLayout.Send, BentoLayout.Storage);
        custom[BentoLayout.Cross].Visible = false;
        custom[BentoLayout.Transfer].Tall = false;
        custom[BentoLayout.Spaces].Tall = true;
        check(Describe(custom, 2) == "storage@0,0x1x1 transfer@0,1x1x1 spaces@1,0x2x1 send@1,1x2x1" && custom.Pack(2).Rows == 3 && !custom.IsDefault,
            "Swapping, hiding and resizing tiles repacks the grid; the tile above an empty cell grows into it");
        foreach (var columns in new[] { 1, 2 })
        {
            // Mọi cách sắp xếp thứ tự/cỡ/ẩn đều cho lưới kín, không chồng lên nhau.
            var random = new Random(columns);
            var tight = true;
            for (var round = 0; round < 300; round++)
            {
                var layout = BentoLayout.Default();
                layout.Tiles = layout.Tiles.OrderBy(_ => random.Next()).ToList();
                foreach (var t in layout.Tiles) { t.Tall = random.Next(2) == 0; t.Visible = random.Next(4) != 0; }
                var (placements, rows) = layout.Pack(columns);
                var cells = placements.SelectMany(p => Enumerable.Range(p.Row, p.RowSpan).SelectMany(r => Enumerable.Range(p.Column, p.ColumnSpan).Select(c => (r, c)))).ToList();
                tight &= cells.Count == cells.Distinct().Count() && cells.Count == rows * columns && placements.Count == layout.Tiles.Count(t => t.Visible);
            }
            check(tight, $"Every arrangement fills a {columns}-column grid without holes or overlaps");
        }

        var lone = new BentoLayout { Tiles = [new() { Id = BentoLayout.Send }, new() { Id = BentoLayout.Transfer }, new() { Id = BentoLayout.Spaces }] }.Normalize();
        foreach (var t in lone.Tiles.Skip(3)) t.Visible = false;
        check(Describe(lone, 2) == "send@0,0x1x1 transfer@0,1x1x1 spaces@1,0x1x2" && lone.Pack(2).Rows == 2, "An odd number of tiles fills the last row across both columns");

        var allHidden = BentoLayout.Default(); foreach (var t in allHidden.Tiles) t.Visible = false;
        check(allHidden.Pack(2).Rows == 0 && allHidden.Pack(2).Placements.Count == 0, "Hiding every small tile leaves only the file list");

        var broken = new BentoLayout { Tiles = [new() { Id = "unknown" }, new() { Id = BentoLayout.Storage, Tall = true }, new() { Id = BentoLayout.Storage }, null!], FilesOnRight = true }.Normalize();
        check(broken.Tiles.Select(t => t.Id).SequenceEqual([BentoLayout.Storage, BentoLayout.Send, BentoLayout.Transfer, BentoLayout.Spaces, BentoLayout.Cross]) && broken[BentoLayout.Storage].Tall && broken[BentoLayout.Transfer].Tall && broken.FilesOnRight,
            "A damaged or older saved layout drops unknown and duplicate tiles and appends missing ones");

        var settings = new UserSettings { Layout = custom };
        settings.Layout.FilesOnRight = true;
        var reloaded = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!.Layout.Normalize();
        check(Describe(reloaded, 2) == Describe(custom, 2) && reloaded.FilesOnRight && !reloaded[BentoLayout.Cross].Visible, "The custom layout is saved with the settings and restored exactly");
        var old = JsonSerializer.Deserialize<UserSettings>("{\"Theme\":\"dark\"}")!;
        check(old.Layout.Normalize().IsDefault, "Settings saved before this feature open with the default layout");
    }
}
