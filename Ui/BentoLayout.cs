namespace DriveLink.Ui;

/// <summary>Một ô nhỏ trên màn hình chính: hiện hay ẩn, cỡ thường (1 hàng) hay cao (2 hàng).</summary>
public sealed class BentoTile
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
    public bool Tall { get; set; }
    public BentoTile Clone() => new() { Id = Id, Visible = Visible, Tall = Tall };
}

/// <summary>Vị trí của một ô trong lưới: hàng, cột và số hàng/cột chiếm.</summary>
public readonly record struct BentoPlacement(string Id, int Row, int Column, int RowSpan, int ColumnSpan);

/// <summary>
/// Bố cục bento do người dùng sắp xếp (lưu trong Cài đặt, theo từng máy):
/// thứ tự các ô nhỏ, ô nào ẩn, ô nào cao, và danh sách tệp nằm bên trái hay bên phải.
/// Thứ tự được xếp vào lưới từ trái sang phải, trên xuống dưới, ô nào vừa chỗ trống đầu tiên thì vào đó;
/// chỗ trống còn lại được lấp bằng cách giãn ô bên cạnh (ra hai cột) hoặc ô phía trên (xuống thêm hàng) để lưới luôn kín.
/// </summary>
public sealed class BentoLayout
{
    public const string Send = "send", Transfer = "transfer", Spaces = "spaces", Cross = "cross", Storage = "storage";
    /// <summary>Các ô theo thứ tự mặc định (giống bố cục trước khi có tùy chỉnh).</summary>
    public static IReadOnlyList<string> Ids { get; } = [Send, Transfer, Spaces, Cross, Storage];

    public List<BentoTile> Tiles { get; set; } = [];
    public bool FilesOnRight { get; set; }

    public static BentoLayout Default() => new() { Tiles = Ids.Select(id => new BentoTile { Id = id, Tall = id == Transfer }).ToList() };

    public static string Title(string id) => id switch
    {
        Send => "Gửi tệp", Transfer => "Truyền tải", Spaces => "Không gian", Cross => "Gửi chéo", Storage => "Dung lượng", _ => id,
    };

    public BentoLayout Clone() => new() { Tiles = Tiles.Select(t => t.Clone()).ToList(), FilesOnRight = FilesOnRight };

    /// <summary>
    /// Sửa bố cục đọc từ tệp cài đặt: bỏ ô lạ hoặc trùng, thêm ô còn thiếu (ví dụ ô mới ở bản sau) vào cuối với cỡ mặc định.
    /// Bố cục rỗng (cài đặt cũ chưa có mục này) trở thành bố cục mặc định.
    /// </summary>
    public BentoLayout Normalize()
    {
        var defaults = Default().Tiles.ToDictionary(t => t.Id);
        var seen = new HashSet<string>();
        var tiles = (Tiles ?? []).Where(t => t is not null && defaults.ContainsKey(t.Id) && seen.Add(t.Id)).Select(t => t.Clone()).ToList();
        tiles.AddRange(Ids.Where(id => !seen.Contains(id)).Select(id => defaults[id].Clone()));
        return new() { Tiles = tiles, FilesOnRight = FilesOnRight };
    }

    public BentoTile this[string id] => Tiles.First(t => t.Id == id);
    public IEnumerable<BentoTile> Hidden => Tiles.Where(t => !t.Visible);
    public bool IsDefault => Normalize() is var n && !n.FilesOnRight && n.Tiles.Select(t => (t.Id, t.Visible, t.Tall)).SequenceEqual(Default().Tiles.Select(t => (t.Id, t.Visible, t.Tall)));

    /// <summary>Đổi chỗ hai ô trong thứ tự (kéo ô này thả lên ô kia).</summary>
    public void Swap(string a, string b)
    {
        var i = Tiles.FindIndex(t => t.Id == a); var j = Tiles.FindIndex(t => t.Id == b);
        if (i < 0 || j < 0 || i == j) return;
        (Tiles[i], Tiles[j]) = (Tiles[j], Tiles[i]);
    }

    /// <summary>Xếp các ô đang hiện vào lưới <paramref name="columns"/> cột. Trả về vị trí từng ô và tổng số hàng.</summary>
    public (IReadOnlyList<BentoPlacement> Placements, int Rows) Pack(int columns)
    {
        columns = Math.Max(1, columns);
        var used = new HashSet<(int Row, int Column)>();
        var placed = new List<BentoPlacement>();
        foreach (var tile in Tiles.Where(t => t.Visible))
        {
            var span = tile.Tall ? 2 : 1;
            for (var row = 0; ; row++)
            {
                var column = Enumerable.Range(0, columns).Cast<int?>().FirstOrDefault(c => Enumerable.Range(row, span).All(r => !used.Contains((r, c!.Value))));
                if (column is not { } c) continue;
                for (var r = row; r < row + span; r++) used.Add((r, c));
                placed.Add(new(tile.Id, row, c, span, 1));
                break;
            }
        }
        // Ô có chỗ trống ngay bên cạnh (suốt chiều cao của nó) thì giãn ra cả hai cột, để lưới không bị lỗ.
        if (columns == 2)
            for (var i = 0; i < placed.Count; i++)
            {
                var p = placed[i]; var other = 1 - p.Column;
                if (!Enumerable.Range(p.Row, p.RowSpan).All(r => !used.Contains((r, other)))) continue;
                for (var r = p.Row; r < p.Row + p.RowSpan; r++) used.Add((r, other));
                placed[i] = p with { Column = 0, ColumnSpan = 2 };
            }
        var rows = placed.Count == 0 ? 0 : placed.Max(p => p.Row + p.RowSpan);
        // Ô trống còn lại (ví dụ dưới một ô thường, cạnh một ô cao) thì ô ngay phía trên kéo dài xuống lấp vào.
        for (var row = 1; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                if (used.Contains((row, column))) continue;
                var above = placed.FindIndex(p => p.Column == column && p.ColumnSpan == 1 && p.Row + p.RowSpan == row);
                if (above < 0) continue;
                placed[above] = placed[above] with { RowSpan = placed[above].RowSpan + 1 };
                used.Add((row, column));
            }
        return (placed, rows);
    }
}
